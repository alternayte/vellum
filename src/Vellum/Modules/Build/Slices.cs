using Vellum.Modules.Modelling;
using Vellum.Modules.Promises;
using Vellum.Modules.Review;
using Vellum.Modules.Trace.Engine;

namespace Vellum.Modules.Build;

/// <summary>One implementation slice: a service with what it serves and what it needs from others.</summary>
/// <param name="DependsOn">The services whose contracts this one calls or consumes, so they come first.</param>
/// <param name="RequirementIds">The promises that bind the service: only promises whose revision in force is approved. A draft and a retired promise bind nothing.</param>
/// <param name="InCycle">True when the service is in a dependency cycle; its place in the order is then by name.</param>
internal sealed record Slice(
    int Order, ElementState Service,
    IReadOnlyList<OperationState> Operations,
    IReadOnlyList<SubscriptionState> Consumes,
    IReadOnlyList<MessageState> Produces,
    IReadOnlyList<Guid> Calls,
    IReadOnlyList<Guid> PublishesTo,
    IReadOnlyList<Guid> WritesTo,
    IReadOnlyList<Guid> DependsOn,
    IReadOnlyList<string> RequirementIds,
    bool InCycle);

/// <summary>
/// The dependency-ordered breakdown of the model. A service is an element, other than an
/// actor or a store, that owns an operation, consumes a subscription, produces a message or
/// publishes in a scenario. Service A depends on service B when a scenario step of A calls an
/// operation of B, or when A consumes a channel that a scenario step of B publishes to. The
/// order puts a service after the services it depends on; ties go by name, then by ID.
/// </summary>
internal static class Slices
{
    public static IReadOnlyList<Slice> Of(BuildView view)
    {
        var model = view.Model;
        var steps = view.AllSteps.Select(s => s.Step).ToList();
        var calls = steps.OfType<CallStep>().Where(c => model.Operations.ContainsKey(c.Operation)).ToList();
        var publishes = steps.OfType<PublishStep>().ToList();
        var writes = steps.OfType<WriteStep>().ToList();

        var services = view.Elements
            .Where(e => e.Kind is not (ElementKind.Actor or ElementKind.Store))
            .Where(e => model.Operations.Values.Any(o => o.OwnerId == e.Id)
                        || model.Subscriptions.Values.Any(s => s.ConsumerId == e.Id)
                        || model.Messages.Values.Any(m => m.ProducerId == e.Id)
                        || publishes.Any(p => p.From == e.Id))
            .ToList();
        var ids = services.Select(s => s.Id.Value).ToHashSet();

        var dependencies = services.ToDictionary(s => s.Id.Value, s =>
        {
            var consumed = model.Subscriptions.Values.Where(x => x.ConsumerId == s.Id).Select(x => x.ChannelId).ToHashSet();
            return calls.Where(c => c.From == s.Id).Select(c => model.Operations[c.Operation].OwnerId.Value)
                .Concat(publishes.Where(p => consumed.Contains(p.Channel)).Select(p => p.From.Value))
                .Where(other => other != s.Id.Value && ids.Contains(other))
                .ToHashSet();
        });

        // Kahn's order with a fixed tie-break. What is left when nothing is free is a cycle.
        var ordered = new List<(ElementState Service, bool InCycle)>();
        var remaining = services.ToList();
        while (remaining.Count > 0)
        {
            var placed = ordered.Select(o => o.Service.Id.Value).ToHashSet();
            var next = remaining.FirstOrDefault(s => dependencies[s.Id.Value].All(placed.Contains));
            ordered.Add(next is null ? (remaining[0], true) : (next, false));
            remaining.Remove(next ?? remaining[0]);
        }

        return ordered.Select((entry, index) =>
        {
            var service = entry.Service;
            var operations = view.Operations.Where(o => o.OwnerId == service.Id).ToList();
            var consumes = view.Subscriptions.Where(s => s.ConsumerId == service.Id).ToList();
            var produces = view.Messages.Where(m => m.ProducerId == service.Id).ToList();
            var own = operations.Select(o => o.Id.Value)
                .Concat(consumes.Select(s => s.Id.Value)).Concat(consumes.Select(s => s.ChannelId.Value))
                .Concat(produces.Select(m => m.Id.Value)).Append(service.Id.Value).ToHashSet();

            return new Slice(
                index + 1, service, operations, consumes, produces,
                Sorted(calls.Where(c => c.From == service.Id).Select(c => c.Operation.Value).Where(id => model.Operations[id].OwnerId != service.Id)),
                Sorted(publishes.Where(p => p.From == service.Id).Select(p => p.Channel.Value)),
                Sorted(writes.Where(w => w.By == service.Id).Select(w => w.Store.Value)),
                [.. ordered.Select(o => o.Service.Id.Value).Where(dependencies[service.Id.Value].Contains)],
                [.. view.Requirements.Where(p => Links(p).Any(own.Contains)).Select(p => p.RequirementId)],
                entry.InCycle);
        }).ToList();

        List<Guid> Sorted(IEnumerable<Guid> values) => [.. values.Distinct().OrderBy(view.Name, StringComparer.Ordinal).ThenBy(id => id)];
    }

    private static IEnumerable<Guid> Links(PromiseDto promise)
    {
        var links = promise.Current.Links;
        return links.OperationIds.Concat(links.ChannelIds).Concat(links.SubscriptionIds).Concat(links.MessageIds).Concat(links.ElementIds);
    }
}

/// <param name="Code">A fixed code such as <c>scenario-invalid</c> or <c>promise-gap</c>.</param>
/// <param name="Subject">The stable ID the blocker is about: an architecture ID, a scenario ID or a requirement ID.</param>
public sealed record Blocker(string Code, string Subject, string Message);

/// <summary>
/// What stands between the pinned state and Build Ready (blocking issues prevent
/// Build Ready, not export). A bundle with a blocker is still exported; its manifest says
/// <c>buildReady: false</c> and lists them.
/// </summary>
internal static class Blockers
{
    public static IReadOnlyList<Blocker> Of(BuildView view, IReadOnlyDictionary<Guid, IReadOnlyList<Route>> routes)
    {
        var blockers = new List<Blocker>();

        foreach (var scenario in view.Scenarios)
        {
            foreach (var issue in view.Playbacks[scenario.Id.Value].Issues)
            {
                blockers.Add(new("scenario-invalid", scenario.Id.Value.ToString(),
                    $"Scenario '{scenario.Name}' cannot be played on the pinned model: [{issue.Code}{(issue.StepId.Length > 0 ? " " + issue.StepId : "")}] {issue.Message}"));
            }
        }

        foreach (var (id, kind, name, status, stale) in Facts(view))
        {
            if (stale is not null)
                blockers.Add(new("fact-stale", id.ToString(), $"The {kind} '{name}' is marked stale since {stale.SinceRevision} ({stale.Reason}). Confirm it or remove it."));
            if (status == FactStatus.Inferred)
                blockers.Add(new("fact-inferred", id.ToString(), $"The {kind} '{name}' is inferred. A person must confirm it before it is built from."));
            if (status == FactStatus.Unknown)
                blockers.Add(new("fact-unknown", id.ToString(), $"The truth of the {kind} '{name}' is unknown."));
        }

        foreach (var (owner, routed) in routes.OrderBy(r => r.Key))
        {
            var kept = routed.Select(r => r.Operation.Id).ToHashSet();
            foreach (var duplicate in view.Operations.Where(o => o.OwnerId.Value == owner && o.Transport == Transport.Http && !kept.Contains(o.Id)
                                                                 && routed.Any(r => string.Equals($"{r.Method} {r.Path}", o.Name.Trim(), StringComparison.OrdinalIgnoreCase))))
            {
                blockers.Add(new("contract-conflict", duplicate.Id.Value.ToString(),
                    $"Operation '{duplicate.Name}' of '{view.Name(owner)}' has the same method and path as another operation of that service. Only one is in the OpenAPI document."));
            }
        }

        // The model says who produces a message. A scenario that publishes it from another element disagrees with the model.
        foreach (var scenario in view.Scenarios)
        {
            foreach (var publish in view.Steps[scenario.Id.Value].Select(s => s.Step).OfType<PublishStep>())
            {
                if (view.Model.Messages.TryGetValue(publish.Message, out var message) && message.ProducerId != publish.From)
                {
                    blockers.Add(new("producer-mismatch", message.Id.Value.ToString(),
                        $"The model says '{view.Name(message.ProducerId)}' ({message.ProducerId.Value}) produces the message '{message.Name}'. Step '{publish.Id}' of scenario '{scenario.Name}' ({scenario.Id.Value}) publishes it from '{view.Name(publish.From)}' ({publish.From.Value}). Change the producer of the message or the scenario."));
                }
            }
        }

        foreach (var promise in view.Promises)
        {
            if (promise.State == PromiseLifecycle.Draft)
            {
                blockers.Add(new("promise-not-approved", promise.RequirementId,
                    $"{promise.RequirementId} '{promise.Current.Title}' has no approved revision. It is not a requirement yet."));
                continue;
            }
            if (promise.State == PromiseLifecycle.Retired)
                continue;

            var status = promise.Current.EvidenceStatus;
            foreach (var gap in status.Gaps)
                blockers.Add(new("promise-gap", promise.RequirementId, $"{promise.RequirementId}: {gap}"));
            if (status.Counts.Failed > 0)
            {
                blockers.Add(new("promise-check-failed", promise.RequirementId,
                    $"{promise.RequirementId}: {status.Counts.Failed} obligation(s) failed their newest check."));
            }
            // An approved promise is evidence-backed only by results on the pinned inputs. A
            // result on inputs that moved is no such result, and neither is no result at all.
            var stale = status.Obligations.Where(o => o.Result is RunResult.Passed or RunResult.Failed && o.Current != true).Select(o => o.ObligationId).ToList();
            var missing = status.Obligations.Where(o => o.Result is RunResult.NotRun or RunResult.Unsupported).Select(o => o.ObligationId).ToList();
            if (stale.Count > 0 || missing.Count > 0 || status.Obligations.Count == 0)
            {
                var parts = new List<string>();
                if (stale.Count > 0)
                    parts.Add($"the newest result of {string.Join(", ", stale)} is for inputs that are not the pinned ones (stale)");
                if (missing.Count > 0)
                    parts.Add($"{string.Join(", ", missing)} {(missing.Count == 1 ? "has" : "have")} no result");
                if (status.Obligations.Count == 0)
                    parts.Add("it has no obligation");
                blockers.Add(new("promise-evidence-stale", promise.RequirementId,
                    $"{promise.RequirementId}: {string.Join("; ", parts)}. Run the checks on the pinned inputs, or record evidence for the commit the model follows."));
            }
        }

        return blockers;
    }

    /// <summary>
    /// The blockers of the review: one <c>review-&lt;rule ID&gt;</c> for each blocking concern that
    /// no accepted decision settles. The subject is the ID the finding is about. A draft is
    /// never blocked: only Build Ready is.
    /// </summary>
    public static IEnumerable<Blocker> FromReview(IEnumerable<FindingView> findings) =>
        findings.Where(f => f.Blocks).Select(f => new Blocker($"review-{f.Finding.RuleId}", f.Finding.Subject.Id.ToString(),
            f.NeedsReReview
                ? $"{f.Finding.Reason} {f.Decision} named this finding, and the finding changed since. Review it again, or change the model."
                : $"{f.Finding.Reason} Change the model, or settle the finding with an accepted decision that names {f.Finding.RuleId} and this subject."));

    public static IEnumerable<(Guid Id, string Kind, string Name, FactStatus Status, StaleMark? Stale)> Facts(BuildView view) =>
        view.Elements.Select(e => (e.Id.Value, "element", e.Name, e.FactStatus, e.Stale))
            .Concat(view.Relationships.Select(r => (r.Id.Value, "relationship", r.Label ?? $"{view.Name(r.FromId)} to {view.Name(r.ToId)}", r.FactStatus, r.Stale)))
            .Concat(view.Messages.Select(m => (m.Id.Value, "message", m.Name, m.FactStatus, m.Stale)))
            .Concat(view.Operations.Select(o => (o.Id.Value, "operation", o.Name, o.FactStatus, o.Stale)))
            .Concat(view.Channels.Select(c => (c.Id.Value, "channel", c.Name, c.FactStatus, c.Stale)))
            .Concat(view.Subscriptions.Select(s => (s.Id.Value, "subscription", view.Name(s.Id), s.FactStatus, s.Stale)))
            .Concat(view.Bindings.Select(b => (b.Id.Value, "binding", view.Name(b.Id), b.FactStatus, b.Stale)));
}
