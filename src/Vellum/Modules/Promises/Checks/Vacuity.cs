using Vellum.Modules.Trace.Engine;

namespace Vellum.Modules.Promises.Checks;

/// <summary>
/// Finds assertions that hold without the scenario exercising what they assert. Such an
/// assertion is no evidence: it would hold whatever the design did.
/// <list type="bullet">
/// <item>An event count of zero is vacuous when no step of the scenario can emit an event of
/// that kind for that target under the faults that were played. <see cref="Possible"/> is an
/// over-approximation of what the engine can emit: it ignores how many attempts a fault
/// fails and the order of the steps, so it never calls a real check vacuous because of
/// timing.</item>
/// <item>An outcome is vacuous when the step never ran in the playback (the engine reads a
/// step with no outcome as skipped), and, when faults were played, when the playback of the
/// step is the same with and without them: the faults did not reach it.</item>
/// </list>
/// </summary>
internal static class Vacuity
{
    /// <summary>An event the scenario can emit: the kind and the IDs an assertion target can match.</summary>
    private readonly record struct Emission(TraceEventKind Kind, Guid? From, Guid? To, Guid? Target);

    /// <summary>The reason an assertion that passed is vacuous, or null when it is not.</summary>
    public static string? Reason(
        Assertion assertion, TraceModel model, Scenario played, TraceResult result, TraceResult? withoutFaults)
    {
        switch (assertion)
        {
            case EventCountAssertion { Expected: 0 } count:
                var possible = Possible(model, played);
                var canOccur = possible.Any(e => e.Kind == count.Kind
                    && (count.Target is not { } target || e.Target == target || e.From == target || e.To == target));
                return canOccur
                    ? null
                    : count.Target is { } named
                        ? $"No step of the scenario can emit a {count.Kind} event for {named} under the faults that were played, so a count of zero shows nothing."
                        : $"No step of the scenario can emit a {count.Kind} event under the faults that were played, so a count of zero shows nothing.";

            case OutcomeAssertion outcome:
                if (!result.Outcomes.ContainsKey(outcome.Step))
                    return $"Step '{outcome.Step}' never ran in this playback. The engine reads a step with no outcome as skipped, so this outcome shows nothing.";
                if (withoutFaults is not null && !Reached(outcome.Step, result, withoutFaults))
                    return $"The faults that were played do not reach step '{outcome.Step}': its playback is the same with and without them.";
                return null;

            default:
                return null;
        }
    }

    /// <summary>
    /// True when the faults changed anything at or before the step's own events: the step has
    /// an event at or after the first place where the two playbacks differ, or its outcome differs.
    /// </summary>
    private static bool Reached(string stepId, TraceResult faulted, TraceResult plain)
    {
        var differsOutcome = faulted.Outcomes.TryGetValue(stepId, out var withFault) != plain.Outcomes.TryGetValue(stepId, out var without)
                             || withFault != without;
        if (differsOutcome)
            return true;

        var first = 0;
        var shared = Math.Min(faulted.Events.Count, plain.Events.Count);
        while (first < shared && Same(faulted.Events[first], plain.Events[first]))
            first++;
        if (first == faulted.Events.Count && first == plain.Events.Count)
            return false;

        return faulted.Events.Skip(first).Any(e => e.StepId == stepId) || plain.Events.Skip(first).Any(e => e.StepId == stepId);
    }

    private static bool Same(TraceEvent a, TraceEvent b) =>
        a.Kind == b.Kind && a.StepId == b.StepId && a.Attempt == b.Attempt && a.AtMs == b.AtMs
        && a.From == b.From && a.To == b.To && a.Target == b.Target && a.Detail == b.Detail;

    /// <summary>Every event the scenario can emit on this model under its faults.</summary>
    private static HashSet<Emission> Possible(TraceModel model, Scenario scenario)
    {
        var faults = (scenario.Faults ?? []).Where(f => f is not null).ToLookup(f => f.Target);
        var emitted = new HashSet<Emission>();
        var anyFailure = false;
        bool Faulted(Guid target) => faults.Contains(target);
        bool FaultedBy(Guid target, FaultKind kind) => faults[target].Any(f => f.Kind == kind);

        bool Walk(IReadOnlyList<Step>? steps)
        {
            var canFail = false;
            foreach (var step in steps ?? [])
            {
                if (step is not null)
                    canFail |= Visit(step);
            }
            return canFail;
        }

        // Returns true when the step can fail.
        bool Visit(Step step)
        {
            switch (step)
            {
                case CallStep call:
                {
                    if (!model.Operations.TryGetValue(call.Operation, out var operation))
                        return false;
                    Guid from = call.From, owner = operation.OwnerId, target = operation.Id;
                    emitted.Add(new(TraceEventKind.RequestSent, from, owner, target));
                    emitted.Add(new(TraceEventKind.ResponseReceived, owner, from, target));

                    var innerFails = Walk(call.Then);
                    var errorFault = FaultedBy(target, FaultKind.Error) || FaultedBy(target, FaultKind.Outage)
                                     || FaultedBy(owner, FaultKind.Error) || FaultedBy(owner, FaultKind.Outage);
                    var timeoutFault = FaultedBy(target, FaultKind.Timeout) || FaultedBy(owner, FaultKind.Timeout);
                    // Time advances only through a timeout or a retry backoff, so a deadline passes only when something inside can fail.
                    var timesOut = timeoutFault || (operation.TimeoutMs > 0 && innerFails);
                    var fails = errorFault || innerFails;
                    if (fails)
                        emitted.Add(new(TraceEventKind.RequestFailed, from, owner, target));
                    if (timesOut)
                        emitted.Add(new(TraceEventKind.RequestTimedOut, from, owner, target));
                    if ((fails || timesOut) && operation.Retry.MaxAttempts > 1)
                        emitted.Add(new(TraceEventKind.RetryScheduled, from, owner, target));
                    Walk(call.OnError);
                    return fails || timesOut;
                }

                case PublishStep publish:
                {
                    Guid from = publish.From, channel = publish.Channel;
                    emitted.Add(new(TraceEventKind.Published, from, null, channel));
                    var publishFails = Faulted(channel);
                    if (publishFails)
                        emitted.Add(new(TraceEventKind.PublishFailed, from, null, channel));

                    var reached = ChannelRouting.Reachable(model, publish.Channel);
                    foreach (var binding in model.Bindings.Values.Where(b => reached.Contains(b.SourceChannelId)))
                    {
                        Guid source = binding.SourceChannelId, bound = binding.TargetChannelId, route = binding.Id;
                        emitted.Add(new(TraceEventKind.Routed, source, bound, route));
                        emitted.Add(new(TraceEventKind.NotRouted, source, bound, route));
                    }
                    foreach (var subscription in model.Subscriptions.Values.Where(s => reached.Contains(s.ChannelId)))
                    {
                        Guid consumer = subscription.ConsumerId, target = subscription.Id;
                        emitted.Add(new(TraceEventKind.Delivered, from, consumer, target));
                        var handler = publish.Handlers is not null && publish.Handlers.TryGetValue(subscription.Id, out var steps) ? steps : null;
                        var deliveryFails = Walk(handler) | Faulted(target) | Faulted(consumer);
                        if (!deliveryFails)
                            continue;

                        emitted.Add(new(TraceEventKind.DeliveryFailed, from, consumer, target));
                        if (subscription.Retry.MaxAttempts > 1)
                            emitted.Add(new(TraceEventKind.RetryScheduled, from, consumer, target));
                        if (subscription.DeadLetterChannelId is not { } deadLetter)
                            emitted.Add(new(TraceEventKind.Dropped, consumer, null, target));
                        else
                        {
                            emitted.Add(new(TraceEventKind.DeadLettered, consumer, null, deadLetter.Value));
                            if (Faulted(deadLetter.Value))
                                emitted.Add(new(TraceEventKind.Dropped, consumer, null, deadLetter.Value));
                        }
                    }
                    // A consumer that fails does not fail the publisher.
                    return publishFails;
                }

                case WriteStep write:
                {
                    Guid by = write.By, store = write.Store;
                    emitted.Add(new(TraceEventKind.DataWritten, by, store, null));
                    if (!Faulted(store))
                        return false;
                    emitted.Add(new(TraceEventKind.DataWriteFailed, by, store, null));
                    return true;
                }

                case ParallelStep parallel:
                {
                    var canFail = false;
                    foreach (var branch in parallel.Branches ?? [])
                        canFail |= Walk(branch);
                    return canFail;
                }

                case BranchStep branch:
                    emitted.Add(new(TraceEventKind.BranchTaken, null, null, null));
                    return Walk(branch.Then) | Walk(branch.Else);

                default:
                    return false;
            }
        }

        anyFailure = Walk(scenario.Steps);
        // A step is skipped only after a step before it failed. Any failure anywhere may skip one.
        if (anyFailure || emitted.Any(e => e.Kind is TraceEventKind.RequestFailed or TraceEventKind.RequestTimedOut or TraceEventKind.DeliveryFailed
                or TraceEventKind.PublishFailed or TraceEventKind.DataWriteFailed))
            emitted.Add(new(TraceEventKind.StepSkipped, null, null, null));
        return emitted;
    }
}
