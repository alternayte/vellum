using System.Security.Cryptography;
using System.Text.Json;
using Vellum.Modules.Modelling.Model;

namespace Vellum.Modules.Trace.Engine;

/// <summary>
/// Deterministic playback of a scenario over a contract snapshot. Time is virtual: only
/// declared timeouts and retry backoff advance it. Work is scheduled on one queue ordered by
/// virtual time, then by scheduling order, so events, fault counts and outcomes all follow
/// time order. The engine reads no clock, no random source and no I/O.
/// </summary>
public static class TraceEngine
{
    public const string Version = "1";

    public static TraceResult Run(TraceModel model, Scenario scenario)
    {
        var issues = ScenarioValidator.Validate(model, scenario);
        if (issues.Count > 0)
            return Rejected(issues);

        var playback = new Playback(model, scenario);
        playback.Run();
        if (playback.Overflowed)
        {
            return Rejected([new ScenarioIssue("", "trace-too-large",
                $"The scenario produces more than {TraceLimits.MaxEvents} events. Nested retries multiply; lower the attempts or flatten the steps.")]);
        }

        var assertions = scenario.Assertions.Select(a => Evaluate(a, playback.Events, playback.Outcomes)).ToList();
        return new TraceResult(Version, [], playback.Events, playback.Outcomes, assertions, Hash(playback.Events));
    }

    private static TraceResult Rejected(IReadOnlyList<ScenarioIssue> issues) =>
        new(Version, issues, [], new Dictionary<string, StepOutcome>(), [], Hash([]));

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

    /// <summary>
    /// The hash covers a canonical form: object keys are sorted, so a payload that went
    /// through <c>jsonb</c> storage (which reorders keys) keeps its hash.
    /// </summary>
    private static string Hash(IReadOnlyList<TraceEvent> events)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartArray();
            foreach (var e in events)
            {
                writer.WriteStartObject();
                writer.WriteNumber("sequence", e.Sequence);
                writer.WriteNumber("atMs", e.AtMs);
                writer.WriteString("kind", e.Kind.ToString());
                writer.WriteString("stepId", e.StepId);
                writer.WriteNumber("attempt", e.Attempt);
                WriteId("from", e.From);
                WriteId("to", e.To);
                WriteId("target", e.Target);
                WriteId("message", e.Message);
                writer.WriteString("detail", e.Detail);
                writer.WritePropertyName("headers");
                writer.WriteStartObject();
                foreach (var (key, value) in (e.Headers ?? new Dictionary<string, string>()).OrderBy(h => h.Key, StringComparer.Ordinal))
                    writer.WriteString(key, value);
                writer.WriteEndObject();
                writer.WritePropertyName("payload");
                if (e.Payload is { } payload) WriteCanonical(writer, payload);
                else writer.WriteNullValue();
                writer.WriteEndObject();
            }
            writer.WriteEndArray();

            void WriteId(string name, Guid? id)
            {
                if (id is { } value) writer.WriteString(name, value);
                else writer.WriteNull(name);
            }
        }
        return Convert.ToHexStringLower(SHA256.HashData(stream.ToArray()));
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                    WriteCanonical(writer, item);
                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }

    /// <summary>
    /// The attempt a step runs in. When the caller gives up on an attempt, work already
    /// started inside it keeps emitting events (the callee does not know), but it no longer
    /// decides any outcome.
    /// </summary>
    private sealed class Scope(Scope? parent)
    {
        public bool Abandoned { get; set; }
        public bool IsLive => !Abandoned && (parent?.IsLive ?? true);
    }

    private sealed class Playback(TraceModel model, Scenario scenario)
    {
        private readonly PriorityQueue<Action, (long At, long Order)> _queue = new();
        private readonly Dictionary<Guid, Fault> _faults = scenario.Faults.ToDictionary(f => f.Target);
        private readonly Dictionary<Guid, int> _attemptsSeen = [];
        private long _order;
        private long _now;

        public List<TraceEvent> Events { get; } = [];
        public Dictionary<string, StepOutcome> Outcomes { get; } = new(StringComparer.Ordinal);
        public bool Overflowed { get; private set; }

        public void Run()
        {
            Sequence(scenario.Steps, 0, new Scope(null), _ => { });
            while (!Overflowed && _queue.TryDequeue(out var work, out var due))
            {
                _now = due.At;
                work();
            }
        }

        private void After(long delayMs, Action work) => _queue.Enqueue(work, (_now + delayMs, _order++));

        /// <summary>Runs steps in order. An unhandled failure skips the rest.</summary>
        private void Sequence(IReadOnlyList<Step> steps, int index, Scope scope, Action<bool> done)
        {
            if (index == steps.Count)
            {
                done(true);
                return;
            }

            Start(steps[index], scope, ok =>
            {
                if (ok)
                {
                    // Queued, not called: a long sequence must not grow the stack, and work
                    // that became due at this instant earlier runs first.
                    After(0, () => Sequence(steps, index + 1, scope, done));
                    return;
                }

                foreach (var skipped in steps.Skip(index + 1))
                {
                    Record(skipped.Id, StepOutcome.Skipped, scope);
                    Emit(TraceEventKind.StepSkipped, skipped.Id, 0, detail: "An earlier step failed.");
                }
                done(false);
            });
        }

        private void Start(Step step, Scope scope, Action<bool> done)
        {
            switch (step)
            {
                case CallStep call: Call(call, 1, scope, done); break;
                case PublishStep publish: Publish(publish, scope, done); break;
                case WriteStep write: Write(write, scope, done); break;
                case ParallelStep parallel: Parallel(parallel, scope, done); break;
                case BranchStep branch: Branch(branch, scope, done); break;
                default: throw new InvalidOperationException($"Unsupported step {step.GetType().Name}.");
            }
        }

        private void Call(CallStep step, int attempt, Scope scope, Action<bool> done)
        {
            var operation = model.Operations[step.Operation];
            Guid from = step.From, to = operation.OwnerId, target = operation.Id;
            var attemptScope = new Scope(scope);
            var settled = false;

            // A retry starts clean: nothing from the abandoned attempt may steer a branch.
            Forget(step.Then);
            Emit(TraceEventKind.RequestSent, step.Id, attempt, from, to, target, step.Request);

            // Both targets see the attempt, so each fault counts every attempt against it.
            var operationFault = NextFault(operation.Id);
            var ownerFault = NextFault(operation.OwnerId);
            var fault = operationFault ?? ownerFault;

            if (fault is FaultKind.Timeout)
            {
                After(operation.TimeoutMs, () => Fail(TraceEventKind.RequestTimedOut, $"No response within {operation.TimeoutMs} ms."));
                return;
            }
            if (fault is not null)
            {
                Fail(TraceEventKind.RequestFailed, fault is FaultKind.Outage ? "The callee is unavailable." : "The callee returned an error.");
                return;
            }

            // The caller's own deadline. Zero means the contract declares none.
            if (operation.TimeoutMs > 0)
                After(operation.TimeoutMs, () => Fail(TraceEventKind.RequestTimedOut, $"No response within {operation.TimeoutMs} ms."));

            Sequence(step.Then, 0, attemptScope, ok =>
            {
                if (settled)
                    return;
                if (!ok)
                {
                    Fail(TraceEventKind.RequestFailed, "A step inside the callee failed.");
                    return;
                }
                settled = true;
                Emit(TraceEventKind.ResponseReceived, step.Id, attempt, to, from, target, step.Response);
                Record(step.Id, StepOutcome.Succeeded, scope);
                done(true);
            });

            void Fail(TraceEventKind kind, string detail)
            {
                if (settled)
                    return;
                settled = true;
                attemptScope.Abandoned = true;
                Emit(kind, step.Id, attempt, from, to, target, detail: detail);

                if (attempt < operation.Retry.MaxAttempts)
                {
                    Emit(TraceEventKind.RetryScheduled, step.Id, attempt + 1, from, to, target,
                        detail: $"Next attempt in {operation.Retry.BackoffMs} ms.");
                    After(operation.Retry.BackoffMs, () => Call(step, attempt + 1, scope, done));
                    return;
                }

                Record(step.Id, StepOutcome.Failed, scope);
                if (step.OnError.Count > 0)
                    Sequence(step.OnError, 0, scope, done);
                else
                    done(false);
            }
        }

        private void Publish(PublishStep step, Scope scope, Action<bool> done)
        {
            Guid from = step.From, channel = step.Channel;

            if (NextFault(channel) is { } fault)
            {
                Emit(TraceEventKind.PublishFailed, step.Id, 1, from, target: channel, payload: step.Payload,
                    detail: fault is FaultKind.Outage ? "The channel is unavailable." : "The channel rejected the message.",
                    message: step.Message, headers: step.Headers);
                Record(step.Id, StepOutcome.Failed, scope);
                done(false);
                return;
            }

            Emit(TraceEventKind.Published, step.Id, 1, from, target: channel, payload: step.Payload,
                message: step.Message, headers: step.Headers);
            Record(step.Id, StepOutcome.Succeeded, scope);

            // Fan-out order is by subscription ID so it does not depend on dictionary order.
            foreach (var subscription in model.Subscriptions.Values
                         .Where(s => s.ChannelId == step.Channel)
                         .OrderBy(s => s.Id.Value))
            {
                var handler = step.Handlers.GetValueOrDefault(subscription.Id) ?? [];
                After(0, () => Deliver(step, subscription, handler, 1));
            }

            // The publisher does not wait for its consumers.
            done(true);
        }

        private void Deliver(PublishStep step, SubscriptionContract subscription, IReadOnlyList<Step> handler, int attempt)
        {
            Guid consumer = subscription.ConsumerId, target = subscription.Id;

            Forget(handler);
            Emit(TraceEventKind.Delivered, step.Id, attempt, step.From, consumer, target, step.Payload,
                message: step.Message, headers: step.Headers);

            var subscriptionFault = NextFault(subscription.Id);
            var consumerFault = NextFault(subscription.ConsumerId);
            if ((subscriptionFault ?? consumerFault) is { } fault)
            {
                Fail(fault is FaultKind.Outage ? "The consumer is unavailable." : "The consumer did not acknowledge the message.");
                return;
            }

            // A consumer is its own flow: giving up on the publisher does not cancel it.
            Sequence(handler, 0, new Scope(null), ok =>
            {
                if (!ok)
                    Fail("A step inside the consumer failed.");
            });

            void Fail(string detail)
            {
                Emit(TraceEventKind.DeliveryFailed, step.Id, attempt, step.From, consumer, target, detail: detail);

                if (attempt < subscription.Retry.MaxAttempts)
                {
                    Emit(TraceEventKind.RetryScheduled, step.Id, attempt + 1, step.From, consumer, target,
                        detail: $"Next attempt in {subscription.Retry.BackoffMs} ms.");
                    After(subscription.Retry.BackoffMs, () => Deliver(step, subscription, handler, attempt + 1));
                    return;
                }

                if (subscription.DeadLetterChannelId is not { } deadLetter)
                {
                    Emit(TraceEventKind.Dropped, step.Id, attempt, consumer, target: target, payload: step.Payload,
                        detail: "No dead-letter channel is declared; the message is lost.", message: step.Message);
                }
                else if (NextFault(deadLetter) is not null)
                {
                    Emit(TraceEventKind.Dropped, step.Id, attempt, consumer, target: deadLetter.Value, payload: step.Payload,
                        detail: "The dead-letter channel is unavailable; the message is lost.", message: step.Message);
                }
                else
                {
                    Emit(TraceEventKind.DeadLettered, step.Id, attempt, consumer, target: deadLetter.Value, payload: step.Payload,
                        message: step.Message, headers: step.Headers);
                }
            }
        }

        private void Write(WriteStep step, Scope scope, Action<bool> done)
        {
            Guid by = step.By, store = step.Store;

            if (NextFault(store) is { } fault)
            {
                Emit(TraceEventKind.DataWriteFailed, step.Id, 1, by, store, payload: step.Change,
                    detail: $"The store {(fault is FaultKind.Outage ? "is unavailable" : "rejected the write")}; '{step.Dataset}' is unchanged.");
                Record(step.Id, StepOutcome.Failed, scope);
                done(false);
                return;
            }

            Emit(TraceEventKind.DataWritten, step.Id, 1, by, store, payload: step.Change, detail: step.Dataset);
            Record(step.Id, StepOutcome.Succeeded, scope);
            done(true);
        }

        private void Parallel(ParallelStep step, Scope scope, Action<bool> done)
        {
            var remaining = step.Branches.Count;
            var ok = true;
            if (remaining == 0)
            {
                Record(step.Id, StepOutcome.Succeeded, scope);
                done(true);
                return;
            }

            foreach (var branch in step.Branches)
            {
                After(0, () => Sequence(branch, 0, scope, branchOk =>
                {
                    ok &= branchOk;
                    if (--remaining > 0)
                        return;
                    Record(step.Id, ok ? StepOutcome.Succeeded : StepOutcome.Failed, scope);
                    done(ok);
                }));
            }
        }

        private void Branch(BranchStep step, Scope scope, Action<bool> done)
        {
            var taken = Outcomes.GetValueOrDefault(step.WhenStep, StepOutcome.Skipped) == step.Is;
            Emit(TraceEventKind.BranchTaken, step.Id, 1, detail: taken ? "then" : "else");

            Sequence(taken ? step.Then : step.Else, 0, scope, ok =>
            {
                Record(step.Id, ok ? StepOutcome.Succeeded : StepOutcome.Failed, scope);
                done(ok);
            });
        }

        /// <summary>Counts an attempt against a target and returns the fault that hits it.</summary>
        private FaultKind? NextFault(Guid target)
        {
            if (!_faults.TryGetValue(target, out var fault))
                return null;

            var attempt = _attemptsSeen[target] = _attemptsSeen.GetValueOrDefault(target) + 1;
            return fault.FailFirst is not { } limit || attempt <= limit ? fault.Kind : null;
        }

        private void Record(string stepId, StepOutcome outcome, Scope scope)
        {
            if (scope.IsLive)
                Outcomes[stepId] = outcome;
        }

        private void Forget(IReadOnlyList<Step> steps)
        {
            foreach (var step in steps)
            {
                Outcomes.Remove(step.Id);
                switch (step)
                {
                    case CallStep call: Forget(call.Then); Forget(call.OnError); break;
                    case ParallelStep parallel: foreach (var branch in parallel.Branches) Forget(branch); break;
                    case BranchStep branch: Forget(branch.Then); Forget(branch.Else); break;
                    case PublishStep publish: foreach (var handler in publish.Handlers.Values) Forget(handler); break;
                }
            }
        }

        private void Emit(
            TraceEventKind kind, string stepId, int attempt,
            Guid? from = null, Guid? to = null, Guid? target = null,
            JsonElement? payload = null, string? detail = null,
            MessageId? message = null, IReadOnlyDictionary<string, string>? headers = null)
        {
            if (Events.Count >= TraceLimits.MaxEvents)
            {
                Overflowed = true;
                return;
            }
            Events.Add(new TraceEvent(
                Events.Count + 1, _now, kind, stepId, attempt, from, to, target, payload, detail,
                message?.Value, headers is { Count: > 0 } ? headers : null));
        }
    }
}
