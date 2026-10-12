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
                "The scenario is too large to play. Nested retries multiply; lower the attempts or flatten the steps.")]);
        }

        var assertions = scenario.Assertions.Select(a => Evaluate(a, playback.Events, playback.Outcomes)).ToList();
        return new TraceResult(Version, [], playback.Events, playback.Outcomes, assertions, Hash(playback.Events, playback.Outcomes));
    }

    private static TraceResult Rejected(IReadOnlyList<ScenarioIssue> issues) =>
        new(Version, issues, [], new Dictionary<string, StepOutcome>(), [], Hash([], new Dictionary<string, StepOutcome>()));

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
    /// The hash covers the events and the outcomes in canonical form (see
    /// <see cref="CanonicalJson"/>), so a scenario that went through storage keeps its hash.
    /// </summary>
    private static string Hash(IReadOnlyList<TraceEvent> events, IReadOnlyDictionary<string, StepOutcome> outcomes)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var stream = new HashingStream(hash);
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteStartArray("events");
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
                writer.WriteStartObject("headers");
                foreach (var (key, value) in (e.Headers ?? new Dictionary<string, string>()).OrderBy(h => h.Key, StringComparer.Ordinal))
                    writer.WriteString(key, value);
                writer.WriteEndObject();
                writer.WritePropertyName("payload");
                if (e.Payload is { } payload) CanonicalJson.Write(writer, payload);
                else writer.WriteNullValue();
                writer.WriteEndObject();
                // Flushed per event: the hash never holds more than one event in memory.
                writer.Flush();
            }
            writer.WriteEndArray();

            writer.WriteStartObject("outcomes");
            foreach (var (stepId, outcome) in outcomes.OrderBy(o => o.Key, StringComparer.Ordinal))
                writer.WriteString(stepId, outcome.ToString());
            writer.WriteEndObject();
            writer.WriteEndObject();

            void WriteId(string name, Guid? id)
            {
                if (id is { } value) writer.WriteString(name, value);
                else writer.WriteNull(name);
            }
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private sealed class HashingStream(IncrementalHash hash) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override void Write(byte[] buffer, int offset, int count) => hash.AppendData(buffer, offset, count);
        public override void Write(ReadOnlySpan<byte> buffer) => hash.AppendData(buffer);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    /// <summary>
    /// The outcomes of one attempt of a call or one delivery to a consumer. A step reads
    /// outcomes through its own attempt, then the enclosing ones. An attempt's outcomes reach
    /// the enclosing scope only when the attempt settles the step: a retried attempt, and
    /// work that still runs after its caller gave up, decide nothing.
    /// </summary>
    private sealed class Scope(Scope? parent)
    {
        private readonly Dictionary<string, StepOutcome> _outcomes = new(StringComparer.Ordinal);

        public Dictionary<string, StepOutcome> Outcomes => _outcomes;
        public void Record(string stepId, StepOutcome outcome) => _outcomes[stepId] = outcome;
        public StepOutcome? Find(string stepId) =>
            _outcomes.TryGetValue(stepId, out var outcome) ? outcome : parent?.Find(stepId);

        public void PromoteTo(Scope target)
        {
            foreach (var (stepId, outcome) in _outcomes)
                target._outcomes[stepId] = outcome;
        }
    }

    private sealed class Playback(TraceModel model, Scenario scenario)
    {
        private readonly PriorityQueue<Action, (long At, long Order)> _queue = new();
        private readonly Dictionary<Guid, Fault> _faults = scenario.Faults.ToDictionary(f => f.Target);
        private readonly Dictionary<Guid, int> _attemptsSeen = [];
        private readonly Scope _root = new(null);
        private long _order;
        private long _now;
        private int _worked;

        public List<TraceEvent> Events { get; } = [];
        public Dictionary<string, StepOutcome> Outcomes => _root.Outcomes;
        public bool Overflowed { get; private set; }

        public void Run()
        {
            Sequence(scenario.Steps, 0, _root, _ => { });
            while (!Overflowed && _queue.TryDequeue(out var work, out var due))
            {
                // Work that emits nothing still costs time, so it has its own limit.
                if (++_worked > TraceLimits.MaxWork)
                {
                    Overflowed = true;
                    return;
                }
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
                    scope.Record(skipped.Id, StepOutcome.Skipped);
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

            // The caller's own deadline. Zero means the contract declares none. The timer is
            // queued before the callee's work, so a response at the deadline instant is late.
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
                attemptScope.PromoteTo(scope);
                Emit(TraceEventKind.ResponseReceived, step.Id, attempt, to, from, target, step.Response);
                scope.Record(step.Id, StepOutcome.Succeeded);
                done(true);
            });

            void Fail(TraceEventKind kind, string detail)
            {
                if (settled)
                    return;
                settled = true;
                Emit(kind, step.Id, attempt, from, to, target, detail: detail);

                if (attempt < operation.Retry.MaxAttempts)
                {
                    Emit(TraceEventKind.RetryScheduled, step.Id, attempt + 1, from, to, target,
                        detail: $"Next attempt in {operation.Retry.BackoffMs} ms.");
                    After(operation.Retry.BackoffMs, () => Call(step, attempt + 1, scope, done));
                    return;
                }

                // The last attempt is what happened; what ran inside it explains the failure.
                attemptScope.PromoteTo(scope);
                scope.Record(step.Id, StepOutcome.Failed);
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
                scope.Record(step.Id, StepOutcome.Failed);
                done(false);
                return;
            }

            Emit(TraceEventKind.Published, step.Id, 1, from, target: channel, payload: step.Payload,
                message: step.Message, headers: step.Headers);
            scope.Record(step.Id, StepOutcome.Succeeded);

            // Fan-out order is by subscription ID so it does not depend on dictionary order.
            foreach (var subscription in model.Subscriptions.Values
                         .Where(s => s.ChannelId == step.Channel)
                         .OrderBy(s => s.Id.Value))
            {
                var handler = step.Handlers.GetValueOrDefault(subscription.Id) ?? [];
                After(0, () => Deliver(step, subscription, handler, 1, scope));
            }

            // The publisher does not wait for its consumers.
            done(true);
        }

        private void Deliver(PublishStep step, SubscriptionContract subscription, IReadOnlyList<Step> handler, int attempt, Scope publisher)
        {
            Guid consumer = subscription.ConsumerId, target = subscription.Id;

            // Each delivery has its own outcomes, so a redelivery that overlaps a slow first
            // run cannot steer it. It reads what the publisher had settled when it published.
            var delivery = new Scope(publisher);
            Emit(TraceEventKind.Delivered, step.Id, attempt, step.From, consumer, target, step.Payload,
                message: step.Message, headers: step.Headers);

            var subscriptionFault = NextFault(subscription.Id);
            var consumerFault = NextFault(subscription.ConsumerId);
            if ((subscriptionFault ?? consumerFault) is { } fault)
            {
                Fail(fault is FaultKind.Outage ? "The consumer is unavailable." : "The consumer did not acknowledge the message.");
                return;
            }

            // A consumer is its own flow: giving up on the publisher does not cancel it, and
            // its outcomes are final for the scenario once the delivery settles.
            Sequence(handler, 0, delivery, ok =>
            {
                if (ok)
                    delivery.PromoteTo(_root);
                else
                    Fail("A step inside the consumer failed.");
            });

            void Fail(string detail)
            {
                Emit(TraceEventKind.DeliveryFailed, step.Id, attempt, step.From, consumer, target, detail: detail);

                if (attempt < subscription.Retry.MaxAttempts)
                {
                    Emit(TraceEventKind.RetryScheduled, step.Id, attempt + 1, step.From, consumer, target,
                        detail: $"Next attempt in {subscription.Retry.BackoffMs} ms.");
                    After(subscription.Retry.BackoffMs, () => Deliver(step, subscription, handler, attempt + 1, publisher));
                    return;
                }

                delivery.PromoteTo(_root);

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
                scope.Record(step.Id, StepOutcome.Failed);
                done(false);
                return;
            }

            Emit(TraceEventKind.DataWritten, step.Id, 1, by, store, payload: step.Change, detail: step.Dataset);
            scope.Record(step.Id, StepOutcome.Succeeded);
            done(true);
        }

        private void Parallel(ParallelStep step, Scope scope, Action<bool> done)
        {
            var remaining = step.Branches.Count;
            var ok = true;
            if (remaining == 0)
            {
                scope.Record(step.Id, StepOutcome.Succeeded);
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
                    scope.Record(step.Id, ok ? StepOutcome.Succeeded : StepOutcome.Failed);
                    done(ok);
                }));
            }
        }

        private void Branch(BranchStep step, Scope scope, Action<bool> done)
        {
            var taken = (scope.Find(step.WhenStep) ?? StepOutcome.Skipped) == step.Is;
            Emit(TraceEventKind.BranchTaken, step.Id, 1, detail: taken ? "then" : "else");

            Sequence(taken ? step.Then : step.Else, 0, scope, ok =>
            {
                scope.Record(step.Id, ok ? StepOutcome.Succeeded : StepOutcome.Failed);
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
