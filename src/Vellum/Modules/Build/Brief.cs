using System.Collections.Immutable;
using System.Text;
using Vellum.Modules.Modelling;
using Vellum.Modules.Promises;
using Vellum.Modules.Trace.Engine;

namespace Vellum.Modules.Build;

public sealed record BriefSelection(IReadOnlyList<Guid> PromiseIds, IReadOnlyList<Guid> ScenarioIds, IReadOnlyList<Guid> ElementIds);

/// <summary>Everything the brief holds, by stable ID.</summary>
public sealed record BriefIncluded(
    IReadOnlyList<Guid> ElementIds, IReadOnlyList<Guid> OperationIds, IReadOnlyList<Guid> ChannelIds,
    IReadOnlyList<Guid> SubscriptionIds, IReadOnlyList<Guid> MessageIds, IReadOnlyList<Guid> SchemaIds,
    IReadOnlyList<Guid> ScenarioIds, IReadOnlyList<string> RequirementIds);

/// <param name="NotApproved">Requirement IDs of promises in scope that no person approved. They are not requirements.</param>
/// <param name="Retired">Requirement IDs of selected promises that are retired. They are not requirements.</param>
/// <param name="UnconfirmedFacts">IDs of facts in scope that are inferred, unknown or stale. They are left out of the fact tables.</param>
public sealed record BriefManifest(
    string Format, GeneratorInfo Generator, PinnedState Pinned, BriefSelection Selection, BriefIncluded Included,
    IReadOnlyList<string> NotApproved, IReadOnlyList<string> Retired, IReadOnlyList<Guid> UnconfirmedFacts,
    string Sha256, long Bytes, bool BuildReady, IReadOnlyList<Blocker> Blockers,
    // A member that is null is not written, so a brief of a selection alone is the bytes it was.
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] Guid? ChangeScanId = null);

public sealed record BriefResult(string Brief, BriefManifest Manifest);

/// <summary>
/// A change-scoped build brief: the part of the pinned architecture that a selection
/// of promises, scenarios and elements needs, and nothing else. The closure rule:
/// <list type="number">
/// <item>A selected element brings the operations it owns, the subscriptions it consumes and
/// the messages it produces.</item>
/// <item>A selected promise with an approved revision brings the scenarios, contracts and
/// elements that revision links, and the scenarios its scenario-check obligations name.</item>
/// <item>A scenario brings every element, operation, channel, message and subscription its
/// steps and faults name.</item>
/// <item>An operation brings its owner and its schemas. A subscription brings its channel,
/// its consumer and its dead-letter channel. A channel brings every subscription on it. A
/// message brings its producer and its schema. Rules 3 and 4 repeat until nothing is added.</item>
/// <item>Every approved promise that links anything gathered so far is added with its
/// obligations. Its own links are not followed, so the brief does not grow to the whole model.</item>
/// <item>The ancestors of the gathered elements are added for context.</item>
/// </list>
/// A promise with no approved revision is never a requirement: it is listed under "not
/// approved". Only the approved revision of a promise is shown, never a draft of it.
/// <para>
/// With a change (the impact report of a scan), the brief also holds that report as its last
/// section: what the source changes, and which flows and promises it reaches, each with its
/// certainty. The report is a statement about the source. It is never a requirement.
/// </para>
/// </summary>
public static class Brief
{
    public const string Format = "vellum-brief/1";

    public static BriefResult Build(BuildInput input, BriefSelection selection, BuildLimits limits, ChangeImpactDto? change = null)
    {
        var model = input.Model;
        var promises = input.Promises.ToDictionary(p => p.Id);
        var scenarios = input.Scenarios.ToDictionary(s => s.Id.Value);

        HashSet<Guid> elements = [], operations = [], channels = [], subscriptions = [], messages = [], schemas = [], scenarioIds = [];
        var requirements = new HashSet<Guid>();
        var notApproved = new HashSet<Guid>();
        var retired = new List<PromiseDto>();

        void Take(Guid id)
        {
            if (model.Elements.ContainsKey(id)) elements.Add(id);
            else if (model.Operations.ContainsKey(id)) operations.Add(id);
            else if (model.Channels.ContainsKey(id)) channels.Add(id);
            else if (model.Subscriptions.ContainsKey(id)) subscriptions.Add(id);
            else if (model.Messages.ContainsKey(id)) messages.Add(id);
        }

        // Rule 1.
        foreach (var id in selection.ElementIds)
        {
            elements.Add(id);
            operations.UnionWith(model.Operations.Values.Where(o => o.OwnerId.Value == id).Select(o => o.Id.Value));
            subscriptions.UnionWith(model.Subscriptions.Values.Where(s => s.ConsumerId.Value == id).Select(s => s.Id.Value));
            messages.UnionWith(model.Messages.Values.Where(m => m.ProducerId.Value == id).Select(m => m.Id.Value));
        }

        // Rule 2.
        scenarioIds.UnionWith(selection.ScenarioIds);
        foreach (var promise in selection.PromiseIds.Select(id => promises[id]))
        {
            // Only a promise whose revision in force is approved is expanded and binds.
            if (promise.State == PromiseLifecycle.Retired)
            {
                retired.Add(promise);
                continue;
            }
            if (promise.State != PromiseLifecycle.Approved)
            {
                notApproved.Add(promise.Id);
                continue;
            }
            requirements.Add(promise.Id);
            foreach (var (kind, id) in promise.Current.Links.All())
            {
                if (kind == "scenario") scenarioIds.Add(id);
                else Take(id);
            }
            scenarioIds.UnionWith(promise.Current.Obligations.Select(o => o.Recipe.ScenarioCheck?.ScenarioId).OfType<Guid>());
        }
        scenarioIds.IntersectWith(scenarios.Keys);

        // Rule 3: scenarios are a fixed set from here on, so they are read once.
        var selected = new BuildView(input with { Scenarios = [.. scenarioIds.Select(id => scenarios[id])] });
        foreach (var step in selected.AllSteps.Select(s => s.Step))
        {
            switch (step)
            {
                case CallStep call: Take(call.From); Take(call.Operation); break;
                case PublishStep publish:
                    Take(publish.From); Take(publish.Channel); Take(publish.Message);
                    foreach (var handler in publish.Handlers?.Keys ?? [])
                        Take(handler);
                    break;
                case WriteStep write: Take(write.By); Take(write.Store); break;
            }
        }
        foreach (var fault in selected.Scenarios.SelectMany(s => s.Faults ?? []).Where(f => f is not null))
            Take(fault.Target);

        // Rule 4, to a fixed point.
        for (var before = -1; before != elements.Count + operations.Count + channels.Count + subscriptions.Count + messages.Count + schemas.Count;)
        {
            before = elements.Count + operations.Count + channels.Count + subscriptions.Count + messages.Count + schemas.Count;
            foreach (var operation in operations.Select(id => model.Operations[id]).ToList())
            {
                elements.Add(operation.OwnerId);
                if (operation.RequestSchemaId is { } request) schemas.Add(request);
                if (operation.ResponseSchemaId is { } response) schemas.Add(response);
            }
            foreach (var subscription in subscriptions.Select(id => model.Subscriptions[id]).ToList())
            {
                channels.Add(subscription.ChannelId);
                if (model.Elements.ContainsKey(subscription.ConsumerId)) elements.Add(subscription.ConsumerId);
                if (subscription.DeadLetterChannelId is { } deadLetter && model.Channels.ContainsKey(deadLetter)) channels.Add(deadLetter);
            }
            // A route belongs to the channel it starts from: the brief takes the channels it leads to.
            foreach (var binding in model.Bindings.Values.Where(b => channels.Contains(b.SourceChannelId)))
                channels.Add(binding.TargetChannelId);
            channels.IntersectWith(model.Channels.Keys.Select(id => id.Value));
            subscriptions.UnionWith(model.Subscriptions.Values.Where(s => channels.Contains(s.ChannelId)).Select(s => s.Id.Value));
            foreach (var message in messages.Select(id => model.Messages[id]).ToList())
            {
                if (model.Elements.ContainsKey(message.ProducerId)) elements.Add(message.ProducerId);
                if (message.SchemaId is { } schema) schemas.Add(schema);
            }
        }
        elements.IntersectWith(model.Elements.Keys.Select(id => id.Value));

        // Rule 5.
        var gathered = elements.Concat(operations).Concat(channels).Concat(subscriptions).Concat(messages).Concat(scenarioIds).ToHashSet();
        foreach (var promise in input.Promises.Where(p => p.Current.Links.All().Any(link => gathered.Contains(link.Id))))
        {
            if (promise.State == PromiseLifecycle.Approved) requirements.Add(promise.Id);
            else if (promise.State == PromiseLifecycle.Draft) notApproved.Add(promise.Id);
        }

        // Rule 6.
        foreach (var id in elements.ToList())
        {
            for (var parent = model.Elements[id].ParentId; parent is { } up && model.Elements.ContainsKey(up) && elements.Add(up); parent = model.Elements[up].ParentId)
            {
            }
        }

        var scoped = model with
        {
            Elements = model.Elements.Where(e => elements.Contains(e.Key)).ToImmutableDictionary(),
            Relationships = model.Relationships.Where(r => elements.Contains(r.Value.FromId) && elements.Contains(r.Value.ToId)).ToImmutableDictionary(),
            Messages = model.Messages.Where(m => messages.Contains(m.Key)).ToImmutableDictionary(),
            Operations = model.Operations.Where(o => operations.Contains(o.Key)).ToImmutableDictionary(),
            Channels = model.Channels.Where(c => channels.Contains(c.Key)).ToImmutableDictionary(),
            Subscriptions = model.Subscriptions.Where(s => subscriptions.Contains(s.Key)).ToImmutableDictionary(),
            Bindings = model.Bindings.Where(b => channels.Contains(b.Value.SourceChannelId) && channels.Contains(b.Value.TargetChannelId)).ToImmutableDictionary(),
        };
        var scopedInput = input with
        {
            Model = scoped,
            Schemas = [.. input.Schemas.Where(s => schemas.Contains(s.Id))],
            Scenarios = [.. selected.Scenarios],
            Promises = [.. input.Promises.Where(p => requirements.Contains(p.Id) || notApproved.Contains(p.Id))],
        };
        // Names of what the scope leaves out come from the whole pinned state, never as a bare ID.
        var whole = new BuildView(input);
        var view = new BuildView(scopedInput, whole);

        // "Approved architecture" leaves out what no person confirmed: inferred, unknown and stale facts.
        static bool Confirmed(FactStatus status, StaleMark? stale) => stale is null && status is not (FactStatus.Inferred or FactStatus.Unknown);
        var confirmed = new BuildView(scopedInput with
        {
            Model = scoped with
            {
                Elements = scoped.Elements.Where(e => Confirmed(e.Value.FactStatus, e.Value.Stale)).ToImmutableDictionary(),
                Relationships = scoped.Relationships.Where(r => Confirmed(r.Value.FactStatus, r.Value.Stale)).ToImmutableDictionary(),
                Messages = scoped.Messages.Where(m => Confirmed(m.Value.FactStatus, m.Value.Stale)).ToImmutableDictionary(),
                Operations = scoped.Operations.Where(o => Confirmed(o.Value.FactStatus, o.Value.Stale)).ToImmutableDictionary(),
                Channels = scoped.Channels.Where(c => Confirmed(c.Value.FactStatus, c.Value.Stale)).ToImmutableDictionary(),
                Subscriptions = scoped.Subscriptions.Where(s => Confirmed(s.Value.FactStatus, s.Value.Stale)).ToImmutableDictionary(),
                Bindings = scoped.Bindings.Where(b => Confirmed(b.Value.FactStatus, b.Value.Stale)).ToImmutableDictionary(),
            },
            Scenarios = [],
        }, view, "not confirmed");
        var unconfirmed = Blockers.Facts(view).Where(f => !Confirmed(f.Status, f.Stale)).OrderBy(f => f.Kind, StringComparer.Ordinal).ThenBy(f => f.Id).ToList();

        var routes = ContractDocuments.Routes(view);
        // The review reads the whole pinned state: a rule needs the scenarios and the promises
        // that the scope leaves out. Its blockers are those about a thing in scope.
        var inScope = gathered.Concat(elements).Concat(requirements).ToHashSet();
        IReadOnlyList<Blocker> blockers =
            [.. Blockers.Of(view, routes), .. Blockers.FromReview(whole.Review.Where(f => inScope.Contains(f.Finding.Subject.Id)))];
        var approved = view.Promises.Where(p => requirements.Contains(p.Id)).ToList();
        var drafts = view.Promises.Where(p => notApproved.Contains(p.Id)).ToList();

        var md = new Markdown();
        var pin = input.Pin;
        md.Heading(1, $"{input.ProjectName}: build brief");
        md.Line(SddDocument.PinnedLine(pin));
        md.Line($"Project `{input.ProjectId}`. Generator vellum-build {Md.Raw(Bundle.GeneratorVersion)}.").Blank();
        md.Line($"Read this first:").Blank();
        md.Line($"- This brief holds only the part of the architecture that the selection needs. It is not the whole system.");
        md.Line(SddDocument.DataNotice);
        md.Line(SddDocument.EscapeNotice);
        md.Line($"- Implement the requirements of section 4 and satisfy the checks of section 5. Do not change a promise: propose an amendment in Vellum, where a person decides.");
        md.Line($"- Do not implement anything from section 6 as a requirement.").Blank();

        md.Heading(2, $"1. Scope");
        md.Line($"Selected:").Blank();
        foreach (var promise in selection.PromiseIds.Select(id => promises[id]).OrderBy(p => p.RequirementId, StringComparer.Ordinal))
            md.Line($"- promise {Md.Code(promise.RequirementId)} {promise.Current.Title}");
        foreach (var id in selection.ScenarioIds.Order())
            md.Line($"- scenario {SddDocument.Ref(view, id)}");
        foreach (var id in selection.ElementIds.Order())
            md.Line($"- element {SddDocument.Ref(view, id)}");
        md.Blank();
        md.Line($"Closure rule: a selected element brings its own operations, subscriptions and messages. A selected approved promise brings what it links. A scenario brings everything its steps and faults name. An operation brings its owner and schemas; a subscription its channel, consumer and dead-letter channel; a channel every subscription on it; a message its producer and schema. Every approved promise that links anything in scope is added with its checks, and its own links are not followed. Parent elements are added for context.").Blank();
        md.Line($"In scope: {view.Elements.Count} element(s), {view.Operations.Count} operation(s), {view.Channels.Count} channel(s), {view.Subscriptions.Count} subscription(s), {view.Messages.Count} message(s), {view.Schemas.Count} schema(s), {view.Scenarios.Count} scenario(s), {approved.Count} requirement(s).").Blank();

        // A brief holds no contract file, so no file of it can be missing.
        var noFile = new HashSet<string>();
        md.Heading(2, $"2. Approved architecture facts");
        md.Line($"Only facts that are declared, observed in source or measured, and not stale.").Blank();
        SddDocument.Elements(md, "2.1 Elements", confirmed);
        SddDocument.Operations(md, "2.2 Operations", confirmed, ContractDocuments.Routes(confirmed), noFile);
        SddDocument.Messaging(md, "2.3 Channels, subscriptions and messages", confirmed);
        SddDocument.Schemas(md, "2.4 Schemas", confirmed);
        SddDocument.Scenarios(md, "3. Scenarios", view);

        md.Heading(2, $"4. Requirements (approved promises)");
        if (approved.Count == 0)
            md.Line($"No approved promise is in scope.").Blank();
        foreach (var promise in approved)
            SddDocument.Promise(md, view, promise, promise.Current);

        SddDocument.AcceptanceTests(md, "5. Required checks", view);

        md.Heading(2, $"6. Not approved: do not implement as requirements");
        if (drafts.Count == 0 && unconfirmed.Count == 0 && retired.Count == 0)
            md.Line($"Nothing in scope is unapproved.").Blank();
        if (drafts.Count > 0)
        {
            md.Line($"Promises that no person approved:").Blank();
            md.Table(["Requirement ID", "Title", "Origin", "Claim"],
                drafts.Select(p => new[] { Md.Code(p.RequirementId), Md.Text(p.Current.Title), Md.Text(p.Origin), Md.Text(p.Current.Claim) }));
        }
        if (retired.Count > 0)
        {
            md.Line($"Selected promises that are retired. They bind nothing:").Blank();
            md.Table(["Requirement ID", "Title"],
                retired.OrderBy(p => p.RequirementId, StringComparer.Ordinal).Select(p => new[] { Md.Code(p.RequirementId), Md.Text(p.Current.Title) }));
        }
        if (unconfirmed.Count > 0)
        {
            md.Line($"Facts in scope that no person confirmed. They are not in section 2:").Blank();
            md.Table(["ID", "Kind", "Name", "Truth"],
                unconfirmed.Select(f => new[] { Md.Code(f.Id), Md.Raw(f.Kind), Md.Text(f.Name), SddDocument.Truth(f.Status, f.Stale) }));
        }

        SddDocument.BlockerSection(md, "7. Blockers in scope", blockers);
        if (change is not null)
            ChangeSection(md, change);

        var bytes = Encoding.UTF8.GetBytes(md.ToString());
        if (bytes.Length > limits.MaxFileBytes)
            throw new BundleTooLargeException($"The brief is larger than {limits.MaxFileBytes} bytes. Select less.");

        static IReadOnlyList<Guid> Sorted(IEnumerable<Guid> ids) => [.. ids.Order()];
        var manifest = new BriefManifest(
            Format, Bundle.Generator,
            input.Pinned,
            new BriefSelection(Sorted(selection.PromiseIds), Sorted(selection.ScenarioIds), Sorted(selection.ElementIds)),
            new BriefIncluded(
                Sorted(elements), Sorted(operations), Sorted(channels), Sorted(subscriptions), Sorted(messages),
                Sorted(view.Schemas.Select(s => s.Id)), Sorted(view.Scenarios.Select(s => s.Id.Value)),
                [.. approved.Select(p => p.RequirementId)]),
            [.. drafts.Select(p => p.RequirementId)],
            [.. retired.Select(p => p.RequirementId).Order(StringComparer.Ordinal)],
            [.. unconfirmed.Select(f => f.Id)],
            BuildText.Sha256(bytes), bytes.Length, blockers.Count == 0, blockers, change?.ScanId);
        return new BriefResult(md.ToString(), manifest);
    }

    /// <summary>The impact report of a scan. Every name and path in it is text of a repository, so it is written as data.</summary>
    private static void ChangeSection(Markdown md, ChangeImpactDto change)
    {
        md.Heading(2, $"8. Change under review");
        md.Line($"Scan {Md.Code(change.ScanId)} of {Md.Code(change.Repository)}: ref {Md.Code(change.Head.Ref ?? "none")} at commit {Md.Code(change.Head.CommitSha)}, a {Md.Raw(change.Kind)} scan, compared with ref {Md.Code(change.Base.Ref ?? "none")} at commit {Md.Code(change.Base.CommitSha ?? "none")}.").Blank();
        if (change.PullRequest is { } pull)
        {
            md.Line($"The commit is the head of pull request {Md.Raw($"#{pull.Number}")} ({Md.Text(pull.Title ?? "no title")}), {Md.Raw(pull.Merged ? "merged" : pull.State)}: base {Md.Code(pull.BaseRef)} at {Md.Code(pull.BaseSha)}, head {Md.Code(pull.HeadRef)} at {Md.Code(pull.HeadSha)}{(pull.FromFork ? Md.Raw(", from another repository") : Md.Raw(""))}.").Blank();
        }
        md.Line($"This section states what the source of that commit changes. It is not a requirement. `definite` stands on the cited diff or source lines; `possible` is reached only through a flow or a service; `unknown` means the scan did not look.").Blank();
        foreach (var limit in change.Limits)
            md.Line($"- {limit}");
        md.Blank();

        static Md Lines(ImpactChangeDto c) => c.Evidence.Count == 0 ? Md.Text("none") : Md.Join(", ", c.Evidence.Take(5).Select(e => Md.Code(e.Pointer)));
        if (change.Changes.Count == 0)
            md.Line($"The scan changes nothing.").Blank();
        else
        {
            md.Table(["Change", "Kind", "Name", "Certainty", "Basis", "Source lines"], change.Changes.Select(c => new[]
            {
                Md.Raw(c.Action), Md.Text(c.EntityKind), Md.Text(c.Name), Md.Raw(c.Certainty), Md.Text(c.Basis), Lines(c),
            }));
        }
        // One row for each reason, so a possible reason never stands under a definite heading.
        if (change.Flows.Count > 0)
        {
            md.Line($"Affected scenarios:").Blank();
            md.Table(["Scenario", "Certainty", "Why"], change.Flows.SelectMany(f => f.Reasons.Select(r => new[]
            {
                Md.Of($"{Md.Text(f.Name)} ({Md.Code(f.ScenarioId)})"), Md.Raw(r.Certainty), Md.Text(r.Text),
            })));
            md.Line($"Affected steps. Each names the changes it stands on; their source lines are in the table of changes.").Blank();
            md.Table(["Scenario", "Step", "Certainty", "Changes"], change.Flows.SelectMany(f => f.Steps.Select(s => new[]
            {
                Md.Text(f.Name), Md.Of($"{Md.Code(s.StepId)} {Md.Text(s.Label ?? s.Kind)}"), Md.Raw(s.Certainty),
                Md.Join(", ", s.Reasons.Select(r => r.ChangeId).Distinct().Select(id => Md.Text(change.Changes.FirstOrDefault(c => c.Id == id)?.Name ?? id))),
            })));
        }
        if (change.Promises.Count > 0)
        {
            md.Line($"Affected promises. `broken`: a check of the promise failed against this scan. `at-risk`: the change touches something the promise names, and no check failed against this scan.").Blank();
            md.Table(["Requirement ID", "Standing", "Certainty", "Why"], change.Promises.SelectMany(p => p.Reasons.Select(r => new[]
            {
                Md.Code(p.RequirementId), Md.Raw(p.Standing), Md.Raw(r.Certainty), Md.Text(r.Text),
            })));
            var checks = change.Promises.SelectMany(p => p.Checks).ToList();
            if (checks.Count > 0)
            {
                md.Line($"Source pattern checks against this scan:").Blank();
                md.Table(["Obligation", "Result", "Detail"], checks.Select(c => new[] { Md.Code(c.ObligationId), Md.Raw(c.Result), Md.Text(c.Detail) }));
            }
        }
    }
}
