using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Vellum.Modules.Modelling.Model;

namespace Vellum.Modules.Trace.Engine;

/// <summary>
/// Deterministic playback of a scenario over a contract snapshot. Time is virtual: only
/// declared timeouts and retry backoff advance it. The engine reads no clock, no random
/// source and no I/O, so the same model and scenario always give the same result.
/// </summary>
public static class TraceEngine
{
    public const string Version = "1";

    private static readonly JsonSerializerOptions HashJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public static TraceResult Run(TraceModel model, Scenario scenario)
    {
        var issues = ScenarioValidator.Validate(model, scenario);
        if (issues.Count > 0)
        {
            return new TraceResult(Version, issues, [], new Dictionary<string, StepOutcome>(), [], Hash([]));
        }

        var playback = new Playback(model, scenario);
        playback.Sequence(scenario.Steps, 0);

        // Consumers run after the publisher has moved on, so events are recorded out of
        // time order. Emission order breaks ties, which keeps the result stable.
        var events = playback.Events
            .OrderBy(e => e.AtMs)
            .ThenBy(e => e.Sequence)
            .Select((e, index) => e with { Sequence = index + 1 })
            .ToList();

        var assertions = scenario.Assertions.Select(a => Evaluate(a, events, playback.Outcomes)).ToList();
        return new TraceResult(Version, [], events, playback.Outcomes, assertions, Hash(events));
    }

    private static AssertionResult Evaluate(
        Assertion assertion, IReadOnlyList<TraceEvent> events, IReadOnlyDictionary<string, StepOutcome> outcomes)
    {
        switch (assertion)
        {
            case OutcomeAssertion outcome:
                var actual = outcomes.GetValueOrDefault(outcome.Step, StepOutcome.Skipped);
                return new(assertion.Id, actual == outcome.Expected,
                    $"Step '{outcome.Step}' {Words(actual)}; expected it to have {Words(outcome.Expected)}.");

            case EventCountAssertion count:
                var seen = events.Count(e => e.Kind == count.Kind
                    && (count.Target is not { } target || e.Target == target || e.From == target || e.To == target));
                return new(assertion.Id, seen == count.Expected,
                    $"{seen} {count.Kind} event(s); expected {count.Expected}.");

            default:
                throw new InvalidOperationException($"Unsupported assertion {assertion.GetType().Name}.");
        }
    }

    private static string Words(StepOutcome outcome) => outcome switch
    {
        StepOutcome.Succeeded => "succeeded",
        StepOutcome.Failed => "failed",
        _ => "been skipped",
    };

    private static string Hash(IReadOnlyList<TraceEvent> events) =>
        Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(events, HashJson)));

    private sealed class Playback(TraceModel model, Scenario scenario)
    {
        private readonly Dictionary<Guid, int> _attemptsSeen = [];

        public List<TraceEvent> Events { get; } = [];
        public Dictionary<string, StepOutcome> Outcomes { get; } = new(StringComparer.Ordinal);

        /// <summary>Runs steps in order. An unhandled failure skips the rest.</summary>
        public (bool Ok, long At) Sequence(IReadOnlyList<Step> steps, long at)
        {
            var ok = true;
            foreach (var step in steps)
            {
                if (!ok)
                {
                    Outcomes[step.Id] = StepOutcome.Skipped;
                    Emit(at, TraceEventKind.StepSkipped, step.Id, 0, detail: "An earlier step failed.");
                    continue;
                }

                (ok, at) = step switch
                {
                    CallStep call => Call(call, at),
                    PublishStep publish => Publish(publish, at),
                    WriteStep write => Write(write, at),
                    ParallelStep parallel => Parallel(parallel, at),
                    BranchStep branch => Branch(branch, at),
                    _ => throw new InvalidOperationException($"Unsupported step {step.GetType().Name}."),
                };
            }
            return (ok, at);
        }

        private (bool, long) Call(CallStep step, long at)
        {
            var operation = model.Operations[step.Operation];
            Guid from = step.From, to = operation.OwnerId, target = operation.Id;

            for (var attempt = 1; attempt <= operation.Retry.MaxAttempts; attempt++)
            {
                Emit(at, TraceEventKind.RequestSent, step.Id, attempt, from, to, target, step.Request);

                var fault = NextFault(operation.Id) ?? NextFault(operation.OwnerId);
                if (fault is FaultKind.Timeout)
                {
                    at += operation.TimeoutMs;
                    Emit(at, TraceEventKind.RequestTimedOut, step.Id, attempt, from, to, target,
                        detail: $"No response within {operation.TimeoutMs} ms.");
                }
                else if (fault is not null)
                {
                    Emit(at, TraceEventKind.RequestFailed, step.Id, attempt, from, to, target,
                        detail: fault is FaultKind.Outage ? "The callee is unavailable." : "The callee returned an error.");
                }
                else
                {
                    var (handled, end) = Sequence(step.Then, at);
                    at = end;
                    if (handled)
                    {
                        Emit(at, TraceEventKind.ResponseReceived, step.Id, attempt, to, from, target, step.Response);
                        Outcomes[step.Id] = StepOutcome.Succeeded;
                        return (true, at);
                    }
                    Emit(at, TraceEventKind.RequestFailed, step.Id, attempt, from, to, target,
                        detail: "A step inside the callee failed.");
                }

                if (attempt < operation.Retry.MaxAttempts)
                {
                    at += operation.Retry.BackoffMs;
                    Emit(at, TraceEventKind.RetryScheduled, step.Id, attempt + 1, from, to, target);
                }
            }

            Outcomes[step.Id] = StepOutcome.Failed;
            return step.OnError.Count > 0 ? Sequence(step.OnError, at) : (false, at);
        }

        private (bool, long) Publish(PublishStep step, long at)
        {
            Guid from = step.From, channel = step.Channel;

            if (NextFault(channel) is not null)
            {
                Emit(at, TraceEventKind.PublishFailed, step.Id, 1, from, target: channel, payload: step.Payload,
                    detail: "The channel is unavailable.");
                Outcomes[step.Id] = StepOutcome.Failed;
                return (false, at);
            }

            Emit(at, TraceEventKind.Published, step.Id, 1, from, target: channel, payload: step.Payload);
            Outcomes[step.Id] = StepOutcome.Succeeded;

            // Fan-out order is by subscription ID so it does not depend on dictionary order.
            foreach (var subscription in model.Subscriptions.Values
                         .Where(s => s.ChannelId == step.Channel)
                         .OrderBy(s => s.Id.Value))
            {
                var handler = step.Handlers.GetValueOrDefault(subscription.Id) ?? [];
                Deliver(step, subscription, handler, at);
            }

            // The publisher does not wait for its consumers.
            return (true, at);
        }

        private void Deliver(PublishStep step, SubscriptionContract subscription, IReadOnlyList<Step> handler, long at)
        {
            Guid consumer = subscription.ConsumerId, target = subscription.Id;

            for (var attempt = 1; attempt <= subscription.Retry.MaxAttempts; attempt++)
            {
                Emit(at, TraceEventKind.Delivered, step.Id, attempt, step.From, consumer, target, step.Payload);

                if ((NextFault(subscription.Id) ?? NextFault(subscription.ConsumerId)) is not null)
                {
                    Emit(at, TraceEventKind.DeliveryFailed, step.Id, attempt, step.From, consumer, target,
                        detail: "The consumer did not acknowledge the message.");
                }
                else
                {
                    var (handled, end) = Sequence(handler, at);
                    at = end;
                    if (handled)
                        return;
                    Emit(at, TraceEventKind.DeliveryFailed, step.Id, attempt, step.From, consumer, target,
                        detail: "A step inside the consumer failed.");
                }

                if (attempt < subscription.Retry.MaxAttempts)
                {
                    at += subscription.Retry.BackoffMs;
                    Emit(at, TraceEventKind.RetryScheduled, step.Id, attempt + 1, step.From, consumer, target);
                }
            }

            if (subscription.DeadLetterChannelId is { } deadLetter)
                Emit(at, TraceEventKind.DeadLettered, step.Id, subscription.Retry.MaxAttempts, consumer, target: deadLetter.Value, payload: step.Payload);
            else
                Emit(at, TraceEventKind.Dropped, step.Id, subscription.Retry.MaxAttempts, consumer, target: target, payload: step.Payload,
                    detail: "No dead-letter channel is declared; the message is lost.");
        }

        private (bool, long) Write(WriteStep step, long at)
        {
            Guid by = step.By, store = step.Store;

            if (NextFault(store) is not null)
            {
                Emit(at, TraceEventKind.DataWriteFailed, step.Id, 1, by, store, payload: step.Change,
                    detail: $"The store is unavailable; '{step.Dataset}' is unchanged.");
                Outcomes[step.Id] = StepOutcome.Failed;
                return (false, at);
            }

            Emit(at, TraceEventKind.DataWritten, step.Id, 1, by, store, payload: step.Change, detail: step.Dataset);
            Outcomes[step.Id] = StepOutcome.Succeeded;
            return (true, at);
        }

        private (bool, long) Parallel(ParallelStep step, long at)
        {
            var ok = true;
            var end = at;
            foreach (var branch in step.Branches)
            {
                var (branchOk, branchEnd) = Sequence(branch, at);
                ok &= branchOk;
                end = Math.Max(end, branchEnd);
            }
            Outcomes[step.Id] = ok ? StepOutcome.Succeeded : StepOutcome.Failed;
            return (ok, end);
        }

        private (bool, long) Branch(BranchStep step, long at)
        {
            var taken = Outcomes.GetValueOrDefault(step.WhenStep, StepOutcome.Skipped) == step.Is;
            Emit(at, TraceEventKind.BranchTaken, step.Id, 1, detail: taken ? "then" : "else");

            var (ok, end) = Sequence(taken ? step.Then : step.Else, at);
            Outcomes[step.Id] = ok ? StepOutcome.Succeeded : StepOutcome.Failed;
            return (ok, end);
        }

        /// <summary>Counts an attempt against a target and returns the fault that hits it.</summary>
        private FaultKind? NextFault(Guid target)
        {
            var fault = scenario.Faults.FirstOrDefault(f => f.Target == target);
            if (fault is null)
                return null;

            var attempt = _attemptsSeen[target] = _attemptsSeen.GetValueOrDefault(target) + 1;
            return fault.FailFirst is not { } limit || attempt <= limit ? fault.Kind : null;
        }

        private void Emit(
            long at, TraceEventKind kind, string stepId, int attempt,
            Guid? from = null, Guid? to = null, Guid? target = null,
            JsonElement? payload = null, string? detail = null) =>
            Events.Add(new TraceEvent(Events.Count + 1, at, kind, stepId, attempt, from, to, target, payload, detail));
    }
}
