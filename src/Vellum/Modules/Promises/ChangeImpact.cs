using System.Globalization;
using System.Security.Claims;
using Deedbox;
using Microsoft.EntityFrameworkCore;
using Vellum.Modules.Import;
using Vellum.Modules.Import.Proposals;
using Vellum.Modules.Import.Scans;
using Vellum.Modules.Modelling.Model;
using Vellum.Modules.Schemas;
using Vellum.Modules.Trace.Engine;
using Vellum.Modules.Workspaces;
using Vellum.Modules.Workspaces.Authorization;
using Vellum.Shared;

namespace Vellum.Modules.Promises;

/// <summary>
/// How sure an entry of the impact report is. <c>definite</c>: it stands on a contract diff
/// or on source lines that the report cites. <c>possible</c>: it is reached only through a
/// flow or a service, and nothing shows that it depends on the change. <c>unknown</c>: the
/// scan did not look where the answer is.
/// </summary>
public static class ImpactCertainty
{
    public const string Definite = "definite";
    public const string Possible = "possible";
    public const string Unknown = "unknown";

    /// <summary>The strongest of the given values. A report entry is as sure as its surest reason.</summary>
    public static string Strongest(IEnumerable<string> values)
    {
        var all = values.ToList();
        return all.Contains(Definite) ? Definite : all.Contains(Possible) ? Possible : Unknown;
    }
}

/// <summary>What the report says of a promise. None of them is a result of a check, except <c>broken</c>.</summary>
public static class ImpactStanding
{
    /// <summary>A deterministic check of the promise failed against this very scan.</summary>
    public const string Broken = "broken";
    /// <summary>The change touches something the promise names. No check failed against this scan.</summary>
    public const string AtRisk = "at-risk";
    public const string Possible = "possible";
    public const string Unknown = "unknown";
}

/// <summary>One side of the comparison. <paramref name="ScanId"/> is null for a base when no other snapshot exists.</summary>
public sealed record ImpactSideDto(Guid? ScanId, string? Ref, string? CommitSha);

/// <summary>One thing the source of the scan changes against the model or against the base snapshot.</summary>
/// <param name="Id"><c>proposal:&lt;id&gt;</c> or <c>pattern:&lt;fact key&gt;</c>. Reasons name it.</param>
/// <param name="Kind"><c>contract</c>: a proposal of the scan. <c>source-pattern</c>: a pattern fact of the source.</param>
/// <param name="Action"><c>add</c>, <c>change</c>, <c>remove</c>, <c>conflict</c> or <c>relink</c>.</param>
/// <param name="Basis">One sentence: what the certainty stands on.</param>
/// <param name="Diff">For a contract change: each field with its value in the model and in the source. Long values are cut.</param>
/// <param name="Evidence">The source lines. For a removed pattern: the lines of the base commit where it was.</param>
/// <param name="ElementId">The element of the model that owns the thing, when it has one.</param>
/// <param name="Pattern">For a source pattern: its name, for example <c>transactional-outbox</c>.</param>
public sealed record ImpactChangeDto(
    string Id, string Kind, string Action, string EntityKind, Guid? EntityId, string Name,
    string Certainty, string Basis,
    IReadOnlyList<ProposalChangeDto> Diff, IReadOnlyList<EvidenceDto> Evidence, Guid? ElementId, string? Pattern);

/// <param name="ChangeId">The <see cref="ImpactChangeDto.Id"/> the reason stands on.</param>
public sealed record ImpactReasonDto(string Certainty, string ChangeId, string Text);

/// <param name="StepIds">The steps that name what changed, and <c>assertion:&lt;id&gt;</c> or <c>fault</c> entries.</param>
public sealed record ImpactFlowDto(Guid ScenarioId, string Name, IReadOnlyList<string> StepIds, string Certainty, IReadOnlyList<ImpactReasonDto> Reasons);

/// <summary>A source pattern check of the promise and its result against this scan.</summary>
/// <param name="Result">The result of the newest run of the obligation that read this scan, whatever ran after it against other scans. Else <c>not-run</c>.</param>
public sealed record ImpactCheckDto(string ObligationId, string Key, string Title, string Result, string? Detail);

/// <param name="State"><c>draft</c> or <c>approved</c>. A draft is not a requirement.</param>
/// <param name="Revision">The revision the report read: the one in force, or the draft when none is approved.</param>
/// <param name="Standing"><c>broken</c>, <c>at-risk</c>, <c>possible</c> or <c>unknown</c>.</param>
public sealed record ImpactPromiseDto(
    Guid Id, string RequirementId, string Title, string State, int Revision, string Standing, string Certainty,
    IReadOnlyList<ImpactReasonDto> Reasons, IReadOnlyList<ImpactCheckDto> Checks);

/// <summary>
/// A pinned state that an impact report is read against, in place of the project as it is now.
/// </summary>
/// <param name="PromiseVersions">The version of each promise stream: runs recorded after it are not read.</param>
/// <param name="Tracked">The followed ref of each repository (<c>host/owner/name</c>), as the pinned state names it.</param>
internal sealed record ImpactInputs(
    ModelState Model, IReadOnlyList<SchemaRow> Schemas, IReadOnlyList<Scenario> Scenarios, IReadOnlyList<PromiseDto> Promises,
    IReadOnlyDictionary<Guid, long> PromiseVersions, IReadOnlyDictionary<string, TrackedRefEntity> Tracked);

/// <param name="Kind"><c>tracking</c> or <c>comparison</c>.</param>
/// <param name="Limits">What the report does not show, as sentences.</param>
/// <param name="PromisesNotAffected">The promises of the project that the report gives no reason for.</param>
public sealed record ChangeImpactDto(
    Guid ScanId, string Repository, string Kind, ImpactSideDto Base, ImpactSideDto Head,
    IReadOnlyList<string> Limits,
    IReadOnlyList<ImpactChangeDto> Changes, IReadOnlyList<ImpactFlowDto> Flows, IReadOnlyList<ImpactPromiseDto> Promises,
    int PromisesNotAffected);

/// <summary>
/// The change impact of one completed scan (SDD §8, capability 2). It reads and writes nothing.
/// <list type="number">
/// <item>Changes: every proposal of the scan, and every source pattern fact that the base
/// snapshot and the snapshot of this scan do not both hold. The base snapshot is that of the
/// scan the accepted facts follow, or, when this scan is that scan, of the newest older scan
/// that read another commit of the repository whole. A proposal is <c>definite</c>: it cites its lines. A pattern that is
/// gone is <c>definite</c> only when the scan read every file where the base found it, else
/// <c>unknown</c>. A pattern that is new is <c>definite</c> only when the base scan read every
/// file where this scan finds it, else <c>unknown</c>.</item>
/// <item>Flows: a scenario with a step that names a removed or changed contract is
/// <c>definite</c> (<see cref="ProposalImpact"/>). A scenario that runs in the service of a
/// changed pattern is <c>possible</c>: nothing says which step needs the pattern.</item>
/// <item>Promises: a promise that requires a changed pattern, or links a changed contract,
/// has the certainty of that change. A promise reached only through an affected scenario is
/// <c>possible</c>. A promise with no reason is not in the report.</item>
/// </list>
/// Nothing here is a result of a check. A promise is <c>broken</c> only when a deterministic
/// check of it failed against this scan.
/// </summary>
public static class ChangeImpact
{
    private const string PatternAttribute = "pattern";
    private const string ServiceAttribute = "service";
    private const string ConditionalAttribute = "conditional";

    /// <summary>The most characters of one value of a diff.</summary>
    public const int MaxDiffValue = 4_000;

    public static async Task<IResult> Handle(
        Guid projectId, Guid importId, ClaimsPrincipal user, HttpContext http,
        WorkspaceAuthorizationService auth, CancellationToken ct)
    {
        await auth.RequireProjectRoleAsync(projectId, PromiseAccess.UserId(user), WorkspaceRole.Viewer, ct);
        var (impact, refusal) = await ComputeAsync(http.RequestServices, projectId, importId, ct);
        return impact is null ? refusal! : Results.Ok(impact);
    }

    /// <summary>The report against the project as it is now, or the refusal: 404 for a scan that is not of the project, 409 for one that is not completed.</summary>
    public static Task<(ChangeImpactDto? Impact, IResult? Refusal)> ComputeAsync(
        IServiceProvider services, Guid projectId, Guid scanId, CancellationToken ct) =>
        ComputeAsync(services, projectId, scanId, null, ct);

    /// <summary>
    /// The report against <paramref name="pinned"/> when it is given: the model, scenarios,
    /// schemas, promises, runs and followed refs of a pinned state, and nothing as it is now
    /// except the scan itself (its snapshot, its proposals and its files).
    /// </summary>
    internal static async Task<(ChangeImpactDto? Impact, IResult? Refusal)> ComputeAsync(
        IServiceProvider services, Guid projectId, Guid scanId, ImpactInputs? pinned, CancellationToken ct)
    {
        var db = services.GetRequiredService<ImportDbContext>();
        var scan = await db.Scans.AsNoTracking().FirstOrDefaultAsync(s => s.Id == scanId && s.ProjectId == projectId, ct);
        if (scan is null)
            return (null, Results.NotFound(new ErrorResponse("not_found", "Import not found")));
        var head = await db.Snapshots.AsNoTracking().FirstOrDefaultAsync(s => s.ScanId == scan.Id, ct);
        if (scan.Status != ScanStatus.Completed || head is null)
            return (null, Results.Conflict(new ErrorResponse("import_not_completed", "Only a completed scan has an impact report.")));

        ModelState model;
        ProposalImpact scenarios;
        IReadOnlyList<PromiseDto> current;
        TrackedRefEntity? tracked;
        if (pinned is null)
        {
            var streamId = await services.GetRequiredService<WorkspacesDbContext>().Projects.AsNoTracking()
                .Where(p => p.Id == projectId).Select(p => (Guid?)p.StreamId).FirstOrDefaultAsync(ct);
            if (streamId is null)
                return (null, Results.NotFound(new ErrorResponse("not_found", "Import not found")));
            var store = services.GetRequiredService<IEventStore>().ForProject(projectId);
            (model, _) = await store.Load<ModelState>(StreamId.From(streamId.Value), ct);
            scenarios = await ProposalImpact.LoadAsync(services, store, projectId, model, ct);
            current = await services.GetRequiredService<PromiseReader>().AllAsync(projectId, ct);
            tracked = await db.TrackedRefs.AsNoTracking()
                .FirstOrDefaultAsync(t => t.ProjectId == projectId && t.Host == scan.Host && t.Repository == scan.Repository, ct);
        }
        else
        {
            model = pinned.Model;
            scenarios = ProposalImpact.Of(model, pinned.Scenarios);
            current = pinned.Promises;
            tracked = pinned.Tracked.GetValueOrDefault(PromiseWorld.RepositoryKey(scan.Host, scan.Repository));
        }
        var kind = ScanKindInfo.Of(scan, tracked);
        var repository = $"{scan.Host}/{scan.Repository}";
        var limits = new List<string>
        {
            "A change in the source does not prove a change in behaviour. An entry marked definite names a thing that the change touches. Whether a flow or a promise still holds needs its checks.",
        };
        if (!kind.Tracking)
            limits.Add("This scan is a comparison. The model does not change, and a source pattern check against it cannot fail: only a scan of the ref that the model follows establishes that a pattern is absent.");
        if (!scan.IsComplete)
            limits.Add("This scan is partial. A limit stopped it before every file was read, so it proposes no removals and a missing item proves nothing.");

        var changes = new List<ImpactChangeDto>();
        var flowReasons = new Dictionary<Guid, (string Name, SortedSet<string> Steps, List<ImpactReasonDto> Reasons)>();
        void AddFlow(ImpactScenarioDto scenario, ImpactReasonDto reason)
        {
            if (!flowReasons.TryGetValue(scenario.Id, out var flow))
                flowReasons[scenario.Id] = flow = (scenario.Name, new SortedSet<string>(StringComparer.Ordinal), []);
            flow.Steps.UnionWith(scenario.StepIds);
            flow.Reasons.Add(reason);
        }

        // 1a. Contract changes: the proposals of the scan.
        var proposals = await db.Proposals.AsNoTracking()
            .Where(p => p.ScanId == scan.Id && p.Status != ProposalStatus.Superseded)
            .OrderBy(p => p.Seq)
            .ToListAsync(ct);
        // What the model holds is read from the model of the report, never from the status of
        // a proposal: an accepted proposal is in the model, and a pinned state that is older
        // than the decision does not hold it.
        var schemaIds = proposals.Where(p => p.EntityKind == EntityKinds.Schema).Select(p => p.EntityId).ToHashSet();
        Dictionary<Guid, SchemaRow> schemas = pinned is not null
            ? pinned.Schemas.Where(s => schemaIds.Contains(s.Id)).ToDictionary(s => s.Id)
            : schemaIds.Count == 0 ? [] : await services.GetRequiredService<SchemasDbContext>().Schemas.AsNoTracking()
                .Where(s => s.ProjectId == projectId && schemaIds.Contains(s.Id))
                .Select(s => new SchemaRow(s.Id, s.Name, s.Content))
                .ToDictionaryAsync(s => s.Id, ct);
        ModelEntity? Held(string entityKind, Guid entityId) => entityKind == EntityKinds.Schema
            ? schemas.TryGetValue(entityId, out var schema) ? ModelIndex.Of(schema) : null
            : ModelIndex.Read(model, entityKind, entityId);

        var changedFields = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var proposal in proposals)
        {
            var id = $"proposal:{proposal.Id}";
            var diff = ProposalJson.ReadChanges(proposal.Changes);
            changedFields[id] = [.. diff.Select(c => c.Field)];
            var relink = proposal.Action == ProposalAction.Relink;
            var held = Held(proposal.EntityKind, proposal.EntityId);
            var holdsSource = held is not null && diff.Count > 0 && diff.All(c => held.Values.GetValueOrDefault(c.Field) == c.To);
            changes.Add(new ImpactChangeDto(
                id, "contract", proposal.Action, proposal.EntityKind, proposal.EntityId, proposal.Name,
                relink ? ImpactCertainty.Unknown : ImpactCertainty.Definite,
                proposal.Action switch
                {
                    ProposalAction.Add when held is not null => "The source declares it at the cited lines. The model holds it now.",
                    ProposalAction.Add => "The source declares it at the cited lines. The model does not hold it.",
                    ProposalAction.Remove when held is null => "The scan read the files where it was declared and does not find it. The model does not hold it any more.",
                    ProposalAction.Remove => "The scan read the files where it was declared and does not find it. The model holds it.",
                    ProposalAction.Relink => "The extraction rules changed, and no exact rule says which fact of this scan it is. A person must choose.",
                    ProposalAction.Conflict or ProposalAction.Change when holdsSource => "The source at the cited lines differed from the model. The model holds the value of the source now.",
                    ProposalAction.Conflict => "The source at the cited lines differs from a value that a person set in the model.",
                    _ => "The source at the cited lines differs from the model.",
                },
                [.. diff.Select(c => new ProposalChangeDto(c.Field, Cut(c.From), Cut(c.To)))],
                Evidence(repository, head.CommitSha, ProposalJson.ReadEvidence(proposal.Evidence)),
                OwnerOf(model, proposal.EntityKind, proposal.EntityId), null));

            if (relink || proposal.Action == ProposalAction.Add)
                continue;
            var verb = proposal.Action == ProposalAction.Remove ? "removes" : "changes";
            foreach (var scenario in scenarios.Of(proposal.Action, proposal.EntityKind, proposal.EntityId, changedFields[id]).Scenarios)
            {
                AddFlow(scenario, new ImpactReasonDto(ImpactCertainty.Definite, id,
                    $"{Steps(scenario.StepIds)} {(scenario.StepIds.Count == 1 ? "names" : "name")} the {proposal.EntityKind} '{proposal.Name}', which this change {verb}."));
            }
        }

        // 1b. Source patterns: the base snapshot against the snapshot of this scan.
        var baseScan = await BaseScanAsync(db, scan, tracked, ct);
        var serviceElements = new Dictionary<string, Guid?>(StringComparer.Ordinal);
        async Task<Guid?> ServiceElementAsync(string? factKey)
        {
            if (factKey is null)
                return null;
            if (!serviceElements.TryGetValue(factKey, out var element))
            {
                var linked = await db.FactLinks.AsNoTracking()
                    .Where(l => l.ProjectId == projectId && l.Host == scan.Host && l.Repository == scan.Repository
                        && l.FactKey == factKey && l.EntityKind == EntityKinds.Element)
                    .OrderBy(l => l.Id).Select(l => (Guid?)l.EntityId).FirstOrDefaultAsync(ct);
                serviceElements[factKey] = element = linked is { } found && model.Elements.ContainsKey(found) ? found : null;
            }
            return element;
        }

        // A pattern change whose pattern the head still declares in the same service: the change
        // names one declaration, and nothing shows that a promise needed that one.
        var stillDeclared = new HashSet<string>(StringComparer.Ordinal);
        if (baseScan is null)
            limits.Add("No other snapshot of this repository exists, so the report cannot say which source patterns this scan adds or removes.");
        else
        {
            var before = await PatternsAsync(db, baseScan.Id, ct);
            var after = await PatternsAsync(db, scan.Id, ct);
            ScanCoverage? headCoverage = null;
            foreach (var (key, was) in before.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                after.TryGetValue(key, out var now);
                if (now is not null && (was.Condition is not null || now.Condition is null))
                    continue;

                var id = $"pattern:{key}";
                var element = await ServiceElementAsync(was.Service);
                var lines = Evidence(repository, baseScan.CommitSha!, was.Evidence);
                var other = after.Where(p => p.Key != key).OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Value)
                    .FirstOrDefault(f => f.Pattern == was.Pattern && f.Service == was.Service && (f.Condition is null || was.Condition is not null));
                var remains = other is null ? "" : $" The snapshot of commit {Short(head.CommitSha)} still declares the pattern in the same service: '{other.Name}'.";
                if (other is not null)
                    stillDeclared.Add(id);
                if (now is not null)
                {
                    changes.Add(new ImpactChangeDto(id, "source-pattern", ProposalAction.Change, "pattern", null, was.Name, ImpactCertainty.Definite,
                        $"At commit {Short(head.CommitSha)} the source declares the pattern only under #if {now.Condition}. At commit {Short(baseScan.CommitSha)} it declared it with no condition.{remains}",
                        [new ProposalChangeDto(ConditionalAttribute, null, now.Condition)],
                        Evidence(repository, head.CommitSha, now.Evidence), element, was.Pattern));
                    continue;
                }

                headCoverage ??= await CoverageAsync(db, scan, ct);
                var gap = !scan.IsComplete ? CoverageGap.Limit : !scan.TreeListed ? CoverageGap.Failed : headCoverage.GapFor(was.Evidence);
                changes.Add(new ImpactChangeDto(id, "source-pattern", ProposalAction.Remove, "pattern", null, was.Name,
                    gap is null ? ImpactCertainty.Definite : ImpactCertainty.Unknown,
                    (gap is null
                        ? $"The scan read every file where the pattern was declared at commit {Short(baseScan.CommitSha)}, and the snapshot of commit {Short(head.CommitSha)} has no such fact."
                        : $"The snapshot of commit {Short(head.CommitSha)} has no such fact, and the scan did not look at every file where the pattern was declared ({gap}). The pattern may still be there.") + remains,
                    [], lines, element, was.Pattern));
            }

            // An added pattern is definite only when the base scan read the files where the head
            // declares it: a base that did not look there proves no absence.
            ScanCoverage? baseCoverage = null;
            foreach (var (key, now) in after.Where(p => !before.ContainsKey(p.Key)).OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                baseCoverage ??= await CoverageAsync(db, baseScan, ct);
                var gap = !baseScan.IsComplete ? CoverageGap.Limit : !baseScan.TreeListed ? CoverageGap.Failed : baseCoverage.GapFor(now.Evidence);
                changes.Add(new ImpactChangeDto($"pattern:{key}", "source-pattern", ProposalAction.Add, "pattern", null, now.Name,
                    gap is null ? ImpactCertainty.Definite : ImpactCertainty.Unknown,
                    gap is null
                        ? $"The source declares the pattern at the cited lines of commit {Short(head.CommitSha)}. The base scan read those files at commit {Short(baseScan.CommitSha)}, and its snapshot has no such fact."
                        : $"The source declares the pattern at the cited lines of commit {Short(head.CommitSha)}. The snapshot of commit {Short(baseScan.CommitSha)} has no such fact, and the base scan did not look at every file where the pattern is now declared ({gap}). The pattern may have been there before.",
                    [], Evidence(repository, head.CommitSha, now.Evidence), await ServiceElementAsync(now.Service), now.Pattern));
            }
        }

        // 2. Flows of a service whose pattern changed: possible, never definite.
        foreach (var change in changes.Where(c => c is { Kind: "source-pattern", ElementId: not null } && c.Action != ProposalAction.Add))
        {
            var service = change.ElementId!.Value;
            var targets = new HashSet<Guid> { service };
            targets.UnionWith(model.Operations.Values.Where(o => o.OwnerId.Value == service).Select(o => o.Id.Value));
            targets.UnionWith(model.Subscriptions.Values.Where(s => s.ConsumerId.Value == service).Select(s => s.Id.Value));
            targets.UnionWith(model.Messages.Values.Where(m => m.ProducerId.Value == service).Select(m => m.Id.Value));
            var name = model.Elements[service].Name;
            foreach (var scenario in scenarios.Naming(targets))
            {
                AddFlow(scenario, new ImpactReasonDto(
                    change.Certainty == ImpactCertainty.Definite ? ImpactCertainty.Possible : ImpactCertainty.Unknown, change.Id,
                    $"{Steps(scenario.StepIds)} {(scenario.StepIds.Count == 1 ? "runs" : "run")} in or against '{name}', the service of the pattern '{change.Pattern}'. Nothing shows which step needs the pattern."));
            }
        }

        var flows = flowReasons
            .Select(f => new ImpactFlowDto(f.Key, f.Value.Name, [.. f.Value.Steps], ImpactCertainty.Strongest(f.Value.Reasons.Select(r => r.Certainty)), f.Value.Reasons))
            .OrderBy(f => f.Name, StringComparer.Ordinal).ThenBy(f => f.ScenarioId)
            .ToList();

        // 3. Promises.
        var all = current.Where(p => p.State != PromiseLifecycle.Retired).ToList();
        // The runs that read this scan. A run against another scan, however new, says nothing about this one.
        List<RunRow> scanRuns = all.Count == 0 ? [] : await services.GetRequiredService<PromisesDbContext>().Runs.AsNoTracking()
            .Where(r => r.ProjectId == projectId && EF.Functions.JsonContains(r.Document, $$"""{"scanId":"{{scan.Id}}"}"""))
            .Select(r => new RunRow(r.PromiseId, r.PromiseRevision, r.ObligationId, r.Version, r.Document))
            .ToListAsync(ct);
        // The newest run of the obligation that read this scan and applies to the revision as it
        // is: of the same obligation (a run of an edited obligation does not apply), and not of a
        // draft when the revision is approved. With a pinned state, only the runs its promise
        // stream held at the pinned version.
        VerificationRun? RunOn(Guid promiseId, PromiseRevisionDto revision, ObligationDto obligation)
        {
            var upTo = pinned?.PromiseVersions.GetValueOrDefault(promiseId) ?? long.MaxValue;
            var hash = PromiseViews.ObligationHash(obligation.Kind, obligation.Recipe);
            return scanRuns
                .Where(r => r.PromiseId == promiseId && r.PromiseRevision == revision.Revision && r.ObligationId == obligation.Id && r.Version <= upTo)
                .OrderByDescending(r => r.Version)
                .Select(r => PromiseJson.Read<VerificationRun>(r.Document))
                .FirstOrDefault(run => run.ScanId == scan.Id
                    && (run.Inputs is null || run.ObligationHash == hash)
                    && !(run.Draft == true && revision.Status != RevisionStatus.Draft));
        }

        var promises = new List<ImpactPromiseDto>();
        foreach (var promise in all)
        {
            var revision = promise.Current;
            var reasons = new List<ImpactReasonDto>();

            var required = revision.Obligations.Select(o => o.Recipe.SourcePattern)
                .OfType<SourcePatternRecipe>().Select(r => (r.Repository, r.Pattern, r.Service))
                .Concat(revision.RequiresPattern is { } needs ? [(needs.Repository, needs.Pattern, needs.Service)] : [])
                .Where(r => r.Repository == scan.Repository)
                .Distinct().ToList();
            foreach (var change in changes.Where(c => c.Kind == "source-pattern" && c.Action != ProposalAction.Add))
            {
                if (!required.Any(r => r.Pattern == change.Pattern && (r.Service is null || r.Service == change.ElementId)))
                    continue;
                reasons.Add(stillDeclared.Contains(change.Id)
                    ? new ImpactReasonDto(
                        change.Certainty == ImpactCertainty.Definite ? ImpactCertainty.Possible : ImpactCertainty.Unknown, change.Id,
                        $"It requires the pattern '{change.Pattern}' in {scan.Repository}. {change.Basis} Nothing shows that the promise needs the declaration that changed.")
                    : new ImpactReasonDto(change.Certainty, change.Id,
                        $"It requires the pattern '{change.Pattern}' in {scan.Repository}. {change.Basis}"));
            }

            var linked = revision.Links.All().Where(l => l.Kind != "scenario").ToList();
            foreach (var change in changes.Where(c => c.Kind == "contract" && c.Action is ProposalAction.Change or ProposalAction.Remove or ProposalAction.Conflict))
            {
                var verb = change.Action == ProposalAction.Remove ? "removes" : "changes";
                var fields = change.Diff.Count == 0 ? "" : $" ({string.Join(", ", change.Diff.Select(d => d.Field))})";
                if (linked.Any(l => l.Id == change.EntityId))
                {
                    reasons.Add(new ImpactReasonDto(ImpactCertainty.Definite, change.Id,
                        $"It links the {change.EntityKind} '{change.Name}', which this change {verb}{fields}."));
                }
                else if (change.EntityKind == EntityKinds.Schema && change.EntityId is { } schema)
                {
                    foreach (var (_, id) in linked)
                    {
                        var user = model.Messages.TryGetValue(id, out var message) && message.SchemaId?.Value == schema ? $"message '{message.Name}'"
                            : model.Operations.TryGetValue(id, out var operation) && (operation.RequestSchemaId?.Value == schema || operation.ResponseSchemaId?.Value == schema) ? $"operation '{operation.Name}'"
                            : null;
                        if (user is not null)
                        {
                            reasons.Add(new ImpactReasonDto(ImpactCertainty.Definite, change.Id,
                                $"It links the {user}, whose schema '{change.Name}' this change {verb}{fields}."));
                        }
                    }
                }
            }

            var played = revision.Links.ScenarioIds
                .Concat(revision.Obligations.Select(o => o.Recipe.ScenarioCheck?.ScenarioId).OfType<Guid>()).ToHashSet();
            // One reason for each affected scenario. Through a flow alone a promise is possible at most.
            foreach (var flow in flows.Where(f => played.Contains(f.ScenarioId)))
            {
                reasons.Add(new ImpactReasonDto(
                    flow.Certainty == ImpactCertainty.Unknown ? ImpactCertainty.Unknown : ImpactCertainty.Possible, flow.Reasons[0].ChangeId,
                    $"Its scenario '{flow.Name}' is an affected flow. Nothing shows that the promise depends on what changed there."));
            }

            if (reasons.Count == 0)
                continue;

            var checks = revision.Obligations
                .Where(o => o.Recipe.SourcePattern is { } recipe && recipe.Repository == scan.Repository)
                .Select(o => RunOn(promise.Id, revision, o) is { } run
                    ? new ImpactCheckDto(o.Id, o.Key, o.Title, run.Result, run.Detail)
                    : new ImpactCheckDto(o.Id, o.Key, o.Title, RunResult.NotRun, "This check was not run against this scan."))
                .ToList();
            var certain = ImpactCertainty.Strongest(reasons.Select(r => r.Certainty));
            promises.Add(new ImpactPromiseDto(
                promise.Id, promise.RequirementId, revision.Title, promise.State, revision.Revision,
                checks.Any(c => c.Result == RunResult.Failed) ? ImpactStanding.Broken
                    : certain == ImpactCertainty.Definite ? ImpactStanding.AtRisk
                    : certain == ImpactCertainty.Possible ? ImpactStanding.Possible
                    : ImpactStanding.Unknown,
                certain, reasons, checks));
        }

        return (new ChangeImpactDto(
            scan.Id, scan.Repository, kind.Kind,
            new ImpactSideDto(baseScan?.Id, baseScan?.ResolvedRef ?? tracked?.RefName, baseScan?.CommitSha),
            new ImpactSideDto(scan.Id, scan.ResolvedRef, head.CommitSha),
            limits, changes, flows, promises, all.Count - promises.Count), null);
    }

    private sealed record RunRow(Guid PromiseId, int PromiseRevision, string ObligationId, long Version, string Document);

    internal sealed record PatternRow(string FactKey, string Name, string Attributes, string Evidence);

    /// <summary>The facts of one snapshot that have a <c>pattern</c> attribute. The database filters them: a snapshot can hold many thousand facts.</summary>
    internal static IQueryable<PatternRow> PatternRows(ImportDbContext db, Guid scanId) =>
        db.SnapshotFacts.AsNoTracking().Where(f => f.ScanId == scanId && EF.Functions.JsonExists(f.Attributes, PatternAttribute)).OrderBy(f => f.Seq)
            .Select(f => new PatternRow(f.FactKey, f.Name, f.Attributes, f.Evidence));

    private sealed record PatternFact(string Name, string Pattern, string? Service, string? Condition, List<EvidenceRef> Evidence);

    /// <summary>
    /// The pattern facts of one snapshot by fact key. A fact is one by its <c>pattern</c>
    /// attribute alone, as <see cref="Checks.SourcePatternCheck"/> reads it.
    /// </summary>
    private static async Task<Dictionary<string, PatternFact>> PatternsAsync(ImportDbContext db, Guid scanId, CancellationToken ct)
    {
        var rows = await PatternRows(db, scanId).ToListAsync(ct);
        var found = new Dictionary<string, PatternFact>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            var attributes = ProposalJson.ReadAttributes(row.Attributes);
            if (attributes.TryGetValue(PatternAttribute, out var pattern))
            {
                found.TryAdd(row.FactKey, new PatternFact(
                    row.Name, pattern, attributes.GetValueOrDefault(ServiceAttribute), attributes.GetValueOrDefault(ConditionalAttribute),
                    ProposalJson.ReadEvidence(row.Evidence)));
            }
        }
        return found;
    }

    /// <summary>
    /// The scan whose snapshot this scan is compared with: the one the accepted facts follow.
    /// When this scan is that scan, the newest older scan of the repository that read another
    /// commit whole: a scan of some paths, or a partial one, is no base to miss a pattern in.
    /// </summary>
    private static async Task<ScanEntity?> BaseScanAsync(ImportDbContext db, ScanEntity scan, TrackedRefEntity? tracked, CancellationToken ct)
    {
        if (tracked?.ScanId is { } followed && followed != scan.Id
            && await db.Snapshots.AsNoTracking().AnyAsync(s => s.ScanId == followed, ct))
            return await db.Scans.AsNoTracking().FirstOrDefaultAsync(s => s.Id == followed, ct);

        return await db.Scans.AsNoTracking()
            .Where(s => s.ProjectId == scan.ProjectId && s.Host == scan.Host && s.Repository == scan.Repository && s.Id != scan.Id
                && s.Status == ScanStatus.Completed && s.CommitSha != null && s.CommitSha != scan.CommitSha && s.CompletedAt < scan.CompletedAt
                && s.IsComplete && s.IncludePaths.Length == 0 && s.ExcludePaths.Length == 0
                && db.Snapshots.Any(snapshot => snapshot.ScanId == s.Id))
            .OrderByDescending(s => s.CompletedAt).ThenBy(s => s.Id)
            .FirstOrDefaultAsync(ct);
    }

    private static async Task<ScanCoverage> CoverageAsync(ImportDbContext db, ScanEntity scan, CancellationToken ct)
    {
        var rows = await db.ScanFiles.AsNoTracking().Where(f => f.ScanId == scan.Id)
            .Select(f => new { f.Path, f.State, f.Reason }).ToListAsync(ct);
        var files = rows.ToDictionary(
            f => f.Path, f => f.State == ScanFileEntity.Read ? null : f.Reason ?? CoverageGap.ParseFailure, StringComparer.Ordinal);
        return new ScanCoverage(
            files, ScanScope.AbsentGap(ScanLimits.FromJson(scan.Limits), scan.IncludePaths, scan.ExcludePaths), scan.ExtractionGap);
    }

    /// <summary>The element that owns the entity: the element itself, the owner of an operation, the consumer of a subscription, the producer of a message.</summary>
    private static Guid? OwnerOf(ModelState model, string entityKind, Guid id) => entityKind switch
    {
        EntityKinds.Element when model.Elements.ContainsKey(id) => id,
        EntityKinds.Operation when model.Operations.TryGetValue(id, out var operation) => operation.OwnerId.Value,
        EntityKinds.Subscription when model.Subscriptions.TryGetValue(id, out var subscription) => subscription.ConsumerId.Value,
        EntityKinds.Message when model.Messages.TryGetValue(id, out var message) => message.ProducerId.Value,
        _ => null,
    };

    private static List<EvidenceDto> Evidence(string repository, string commitSha, IEnumerable<EvidenceRef> evidence) =>
        [.. evidence.Select(e => new EvidenceDto(
            e.Path, e.StartLine, e.EndLine, e.Rule,
            EvidenceLinks.Pointer(repository, commitSha, e), EvidenceLinks.Url(repository, commitSha, e)))];

    private static string Steps(IReadOnlyList<string> stepIds) =>
        string.Create(CultureInfo.InvariantCulture, $"{(stepIds.Count == 1 ? "Step" : "Steps")} {string.Join(", ", stepIds)}");

    private static string Short(string? commitSha) => commitSha is { Length: > 10 } ? commitSha[..10] : commitSha ?? "";

    private static string? Cut(string? value) => value is { Length: > MaxDiffValue } ? value[..MaxDiffValue] + "…" : value;
}
