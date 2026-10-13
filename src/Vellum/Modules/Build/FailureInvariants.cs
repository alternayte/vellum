using Vellum.Modules.Promises;
using Vellum.Modules.Trace.Engine;

namespace Vellum.Modules.Build;

/// <summary>A scenario step that a forbidden event constrains: the step can emit an event of that kind for that target.</summary>
internal sealed record ConstrainedStep(Guid ScenarioId, string StepId, int Row);

/// <summary>
/// One thing an approved promise forbids. <paramref name="Event"/> is null for the forbidden
/// outcome as the promise states it in words; such a row names no step, because Vellum does
/// not read steps out of text.
/// </summary>
internal sealed record FailureInvariant(
    PromiseDto Promise, ForbiddenEvent? Event, IReadOnlyList<Guid> ScenarioIds, IReadOnlyList<ConstrainedStep> Steps);

/// <summary>
/// The failure invariants of the pinned state: for each promise whose revision in force is
/// approved, its forbidden outcome and each of its structured forbidden events, with the
/// steps of its scenarios that the event constrains. The scenarios of a promise are the ones
/// it links and the ones its scenario checks play. A step is constrained by a forbidden event
/// when a step of its type can emit an event of that kind for that target, whatever faults
/// are played: an implementation of that step must never produce it.
/// </summary>
internal static class FailureInvariants
{
    public static IReadOnlyList<FailureInvariant> Of(BuildView view)
    {
        var invariants = new List<FailureInvariant>();
        foreach (var promise in view.Requirements)
        {
            var revision = promise.Current;
            var scenarioIds = revision.Links.ScenarioIds
                .Concat(revision.Obligations.Select(o => o.Recipe.ScenarioCheck?.ScenarioId).OfType<Guid>())
                .Distinct().Where(view.Steps.ContainsKey)
                .OrderBy(view.Name, StringComparer.Ordinal).ThenBy(id => id)
                .ToList();

            if (!string.IsNullOrWhiteSpace(revision.ForbiddenOutcome))
                invariants.Add(new FailureInvariant(promise, null, scenarioIds, []));
            foreach (var forbidden in revision.ForbiddenEvents)
            {
                invariants.Add(new FailureInvariant(promise, forbidden, scenarioIds,
                    [.. scenarioIds.SelectMany(id => view.Steps[id])
                        .Where(flat => CanEmit(view, flat.Step, forbidden))
                        .Select(flat => new ConstrainedStep(flat.ScenarioId, flat.Step.Id, flat.Row))]));
            }
        }
        return invariants;
    }

    /// <summary>True when a step of this type, on these contracts, can emit an event of the forbidden kind that an assertion on the forbidden target counts.</summary>
    private static bool CanEmit(BuildView view, Step step, ForbiddenEvent forbidden)
    {
        var model = view.Model;
        bool Hits(params Guid?[] ids) => forbidden.Target is not { } target || ids.Contains(target);

        switch (step)
        {
            case CallStep call when model.Operations.TryGetValue(call.Operation, out var operation):
                return forbidden.Kind is TraceEventKind.RequestSent or TraceEventKind.ResponseReceived or TraceEventKind.RequestFailed
                           or TraceEventKind.RequestTimedOut or TraceEventKind.RetryScheduled or TraceEventKind.StepSkipped
                       && (forbidden.Kind == TraceEventKind.StepSkipped
                           ? forbidden.Target is null
                           : Hits(call.From.Value, operation.Id.Value, operation.OwnerId.Value));

            case PublishStep publish:
                if (forbidden.Kind is TraceEventKind.Published or TraceEventKind.PublishFailed)
                    return Hits(publish.From.Value, publish.Channel.Value);
                if (forbidden.Kind == TraceEventKind.StepSkipped)
                    return forbidden.Target is null;
                var reached = BuildText.Reachable(model, publish.Channel);
                foreach (var subscription in model.Subscriptions.Values.Where(s => reached.Contains(s.ChannelId)))
                {
                    Guid consumer = subscription.ConsumerId, id = subscription.Id;
                    var deadLetter = subscription.DeadLetterChannelId?.Value;
                    var emits = forbidden.Kind switch
                    {
                        TraceEventKind.Delivered or TraceEventKind.DeliveryFailed or TraceEventKind.RetryScheduled => Hits(publish.From.Value, consumer, id),
                        TraceEventKind.DeadLettered => deadLetter is not null && Hits(consumer, deadLetter),
                        TraceEventKind.Dropped => Hits(consumer, id, deadLetter),
                        _ => false,
                    };
                    if (emits)
                        return true;
                }
                return false;

            case WriteStep write:
                return forbidden.Kind switch
                {
                    TraceEventKind.DataWritten or TraceEventKind.DataWriteFailed => Hits(write.By.Value, write.Store.Value),
                    TraceEventKind.StepSkipped => forbidden.Target is null,
                    _ => false,
                };

            case BranchStep:
                return forbidden.Kind is TraceEventKind.BranchTaken or TraceEventKind.StepSkipped && forbidden.Target is null;

            default:
                return forbidden.Kind == TraceEventKind.StepSkipped && forbidden.Target is null;
        }
    }
}
