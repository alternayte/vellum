using System.Globalization;
using System.Text.Json.Serialization;
using Vellum.Modules.Trace.Engine;

namespace Vellum.Modules.Promises;

/// <param name="Passed">Obligations whose newest run passed and is current. A pass whose input moved is in <paramref name="Stale"/>.</param>
/// <param name="Failed">Obligations whose newest run failed and is current.</param>
/// <param name="Stale">Obligations whose newest run passed or failed on inputs that are no longer the current ones.</param>
public sealed record EvidenceCountsDto(int Passed, int Failed, int NotRun, int Unsupported, int Stale);

/// <param name="Key">The last part of the ID (<c>O2</c>): what a URL names the obligation by.</param>
/// <param name="Supported">True for the kinds Vellum checks by itself. Derived from the kind.</param>
/// <param name="Execution"><c>deterministic</c>, <c>external-evidence</c> or <c>none</c>.</param>
/// <param name="Source"><c>generated</c> (the generator made it from the links), <c>manual</c> (a person wrote it) or <c>agent</c> (an agent token wrote it).</param>
/// <param name="AddedBy">
/// Who added the obligation to this draft: <c>user:&lt;id&gt;</c> or <c>agent-token:&lt;id&gt;</c>.
/// Left out for an obligation that the revision copied from an approved one, and for one that
/// was added before events named their actor.
/// </param>
/// <param name="LastChangedBy">Who changed the obligation last, after it was added. Left out while nobody did.</param>
public sealed record ObligationDto(
    string Id, string Key, int Number, string Kind, bool Supported, string Execution,
    string Title, ObligationRecipe Recipe, string Source,
    // A member that is null is not written, so a bundle of a state that has none is the bytes it was.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? AddedBy = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? LastChangedBy = null)
{
    public static ObligationDto From(Obligation obligation, ObligationActors? actors = null) => new(
        obligation.Id, obligation.Key, obligation.Number, obligation.Kind,
        ObligationKind.IsDeterministic(obligation.Kind), ObligationKind.Execution(obligation.Kind),
        obligation.Title, obligation.Recipe, obligation.Source, actors?.AddedBy, actors?.LastChangedBy);
}

/// <summary>
/// The result of one obligation: that of its newest run, with what exactly was checked. An
/// obligation with no run that applies is <c>not-run</c>, and one of a kind Vellum does not
/// know is <c>unsupported</c>.
/// </summary>
/// <param name="Current">
/// For <c>passed</c> and <c>failed</c>: true when every input the run recorded is still the
/// current one. False means the result is for inputs that moved; <paramref name="Changed"/>
/// names each. Null when there is no result to be current.
/// </param>
/// <param name="Inputs">Everything the run read, at the version it read it.</param>
/// <param name="Assertions">For a scenario check: each assertion, with whether it is vacuous.</param>
/// <param name="EffectiveFaults">For a scenario check: the faults that were played.</param>
public sealed record ObligationStatusDto(
    string ObligationId, string Result, bool? Current, Guid? RunId, DateTimeOffset? At, string? Environment,
    ModelPin? ModelRevision, string? CommitSha, Guid? ScanId, string? Scope,
    IReadOnlyList<EvidenceItem> Evidence, IReadOnlyList<string> Limitations, string? Detail,
    RunInputs? Inputs, IReadOnlyList<string> Changed,
    IReadOnlyList<AssertionOutcome>? Assertions, IReadOnlyList<Fault>? EffectiveFaults);

/// <summary>
/// What the evidence of a revision establishes, and what it does not. It is computed on every
/// read and never stored. It has no single flag: the counts, the exact revisions and
/// environments and the list of gaps are the answer (SDD guarantees 8 and 11).
/// </summary>
public sealed record EvidenceStatusDto(
    EvidenceCountsDto Counts,
    IReadOnlyList<ObligationStatusDto> Obligations,
    IReadOnlyList<string> ModelRevisions,
    IReadOnlyList<string> Commits,
    IReadOnlyList<string> Environments,
    IReadOnlyList<string> Gaps);

public sealed record MissingLinkDto(string Kind, Guid Id);

/// <param name="Status"><c>draft</c> or <c>approved</c>.</param>
/// <param name="Retires">True when approving this revision retires the promise.</param>
/// <param name="RetiresRevision">For a revision that retires: the revision it retires. Its text is shown here; it holds none of its own.</param>
/// <param name="InForce">True for the newest approved revision.</param>
/// <param name="ContentHash">The hash of everything an approval of this revision binds. Send it with <c>approve</c>.</param>
/// <param name="ApprovedContentHash">The hash the approver sent. Null for a draft, and for an approval stored before the hash existed.</param>
/// <param name="LastChangedBy">
/// Who changed the content or the obligations of the revision last: <c>user:&lt;id&gt;</c> for
/// a person, <c>agent-token:&lt;id&gt;</c> for an agent token. A value that starts with
/// <c>agent-token:</c> on a draft says that an agent token changed the draft after the last
/// change by a person. Left out when the change was stored before events named their actor.
/// </param>
/// <param name="LastChangedAt">When <paramref name="LastChangedBy"/> made that change.</param>
public sealed record PromiseRevisionDto(
    int Revision, string Status, bool Retires, int? RetiresRevision, bool InForce,
    string Title, string Claim, string? Scope, string? Preconditions, string? ExpectedOutcome,
    string? ForbiddenOutcome, string? EvidenceCriteria,
    IReadOnlyList<ForbiddenEvent> ForbiddenEvents, RequiredPattern? RequiresPattern, PromiseLinks Links,
    IReadOnlyList<ObligationDto> Obligations,
    string CreatedBy, DateTimeOffset CreatedAt, string? ApprovedBy, DateTimeOffset? ApprovedAt, long? ApprovedModelVersion,
    string ContentHash, string? ApprovedContentHash,
    EvidenceStatusDto EvidenceStatus,
    // A member that is null is not written, so a bundle of a state that has none is the bytes it was.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? LastChangedBy = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? LastChangedAt = null);

/// <param name="State"><c>draft</c>, <c>approved</c> or <c>retired</c>.</param>
/// <param name="NeedsReview">True when a linked thing no longer exists. Computed on read.</param>
/// <param name="Current">The revision in force, or the draft when none was approved.</param>
/// <param name="Draft">The unapproved revision, when one exists beside <paramref name="Current"/>.</param>
public sealed record PromiseDto(
    Guid Id, Guid ProjectId, string RequirementId, string Origin, string State,
    int LatestRevision, int? InForceRevision, bool NeedsReview, IReadOnlyList<MissingLinkDto> MissingLinks,
    PromiseRevisionDto Current, PromiseRevisionDto? Draft, IReadOnlyList<PromiseRevisionDto> Revisions,
    string CreatedBy, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

/// <param name="Counts">The same counts as <c>current.evidenceStatus.counts</c> of the promise.</param>
/// <param name="HasGaps">True when <c>current.evidenceStatus.gaps</c> of the promise is not empty.</param>
/// <param name="DraftLastChangedBy">For a promise with a draft: who changed the draft last (see <see cref="PromiseRevisionDto"/>). Null without a draft.</param>
public sealed record PromiseSummaryDto(
    Guid Id, string RequirementId, string Title, string State, string Origin,
    int LatestRevision, int? InForceRevision, bool HasDraft, bool NeedsReview,
    EvidenceCountsDto Counts, bool HasGaps, DateTimeOffset UpdatedAt, string? DraftLastChangedBy = null);

public sealed record PrefixDto(string? Prefix, int? NextNumber);

public static class PromiseViews
{
    public static PromiseDto ToDto(Guid id, PromiseState state, IReadOnlyDictionary<string, VerificationRun> latestRuns, PromiseWorld world)
    {
        var inForce = state.InForce;
        var current = inForce ?? state.Latest!;
        var revisions = state.Revisions.Select(r => Revision(state, r, r == inForce, latestRuns, world)).ToList();
        var missing = Missing(current, state.Draft, world);

        return new PromiseDto(
            id, state.ProjectId!.Value, state.RequirementId!, state.Origin!, state.Lifecycle,
            state.Latest!.Revision, inForce?.Revision, missing.Count > 0, missing,
            revisions[state.Revisions.IndexOf(current)],
            state.Draft is { } draft && draft != current ? revisions[^1] : null,
            revisions, state.CreatedBy!, state.CreatedAt!.Value, state.UpdatedAt!.Value);
    }

    public static PromiseSummaryDto ToSummary(Guid id, PromiseState state, IReadOnlyDictionary<string, VerificationRun> latestRuns, PromiseWorld world)
    {
        var current = state.InForce ?? state.Latest!;
        var evidence = Evidence(state, current, latestRuns, world);
        return new PromiseSummaryDto(
            id, state.RequirementId!, current.Content.Title, state.Lifecycle, state.Origin!,
            state.Latest!.Revision, state.InForce?.Revision, state.Draft is not null,
            Missing(current, state.Draft, world).Count > 0,
            evidence.Counts, evidence.Gaps.Count > 0, state.UpdatedAt!.Value, state.Draft?.LastChangedBy);
    }

    private static List<MissingLinkDto> Missing(PromiseRevisionState current, PromiseRevisionState? draft, PromiseWorld world) =>
        current.Content.Links.All().Concat(draft?.Content.Links.All() ?? [])
            .Distinct()
            .Where(link => !world.Known.Has(link.Kind, link.Id))
            .Select(link => new MissingLinkDto(link.Kind, link.Id))
            .ToList();

    private static PromiseRevisionDto Revision(
        PromiseState state, PromiseRevisionState revision, bool inForce, IReadOnlyDictionary<string, VerificationRun> latestRuns, PromiseWorld world)
    {
        // A revision that retires holds no text of its own: the reader sees what it retires.
        var content = state.Shown(revision).Content;
        return new PromiseRevisionDto(
            revision.Revision, revision.Status, revision.Retires, revision.RetiresRevision, inForce,
            content.Title, content.Claim, content.Scope, content.Preconditions, content.ExpectedOutcome,
            content.ForbiddenOutcome, content.EvidenceCriteria,
            content.ForbiddenEvents, content.RequiresPattern, content.Links,
            revision.Obligations.Select(o => ObligationDto.From(o, revision.ActorsOf(o.Id))).ToList(),
            revision.CreatedBy, revision.CreatedAt, revision.ApprovedBy, revision.ApprovedAt, revision.ApprovedModelVersion,
            PromiseContentHash.Of(state.RequirementId, revision), revision.ApprovedContentHash,
            Evidence(state, revision, latestRuns, world), revision.LastChangedBy, revision.LastChangedAt);
    }

    /// <summary>The evidence status of one revision. Every sentence in <c>Gaps</c> names what is not established.</summary>
    public static EvidenceStatusDto Evidence(
        PromiseState state, PromiseRevisionState revision, IReadOnlyDictionary<string, VerificationRun> latestRuns, PromiseWorld world)
    {
        var gaps = new List<string>();
        if (revision.IsDraft)
            gaps.Add(string.Create(CultureInfo.InvariantCulture, $"Revision {revision.Revision} is a draft. It is not approved and is not in force."));
        else if (revision.ApprovedContentHash is { } approved
                 && (revision.ApprovalIntact == false || approved != PromiseContentHash.Of(state.RequirementId, revision)))
        {
            gaps.Add(string.Create(CultureInfo.InvariantCulture,
                $"The content of revision {revision.Revision} is not the content that was approved: the approval names the hash {approved}. Review the promise and approve a new revision."));
        }
        if (revision.Obligations.Count == 0 && !revision.Retires)
            gaps.Add("No proof obligation exists for this revision.");

        var statuses = revision.Obligations.Select(o => Status(o, revision, latestRuns, world, gaps)).ToList();

        foreach (var (kind, id) in revision.Content.Links.All())
        {
            if (!world.Known.Has(kind, id))
                gaps.Add($"The linked {kind} {id} no longer exists.");
            else if (revision.ApprovedLinks is { } approvedLinks
                     && approvedLinks.TryGetValue(PromiseWorld.LinkKey(kind, id), out var then)
                     && world.LinkHash(kind, id) is { } now && now != then)
                gaps.Add($"The linked {world.Describe(kind, id)} changed after this revision was approved.");
        }

        var runs = statuses.Where(s => s.RunId is not null).ToList();
        return new EvidenceStatusDto(
            Counts(statuses), statuses,
            [.. runs.Select(s => s.ModelRevision).OfType<ModelPin>().Select(Describe).Distinct().Order(StringComparer.Ordinal)],
            [.. runs.Select(s => s.CommitSha).OfType<string>().Distinct().Order(StringComparer.Ordinal)],
            [.. runs.Select(s => s.Environment).OfType<string>().Distinct().Order(StringComparer.Ordinal)],
            gaps);
    }

    public static string Describe(ModelPin pin) => pin.RevisionId is { } revisionId
        ? string.Create(CultureInfo.InvariantCulture, $"revision '{pin.RevisionName}' ({revisionId}): stream {pin.StreamId} at version {pin.Version}")
        : string.Create(CultureInfo.InvariantCulture, $"stream {pin.StreamId} at version {pin.Version}");

    /// <summary>A result counts as passed or failed only while it is current. A result on inputs that moved is stale and nothing else.</summary>
    private static EvidenceCountsDto Counts(IEnumerable<ObligationStatusDto> statuses)
    {
        int passed = 0, failed = 0, notRun = 0, unsupported = 0, stale = 0;
        foreach (var status in statuses)
        {
            switch (status.Result)
            {
                case RunResult.Passed or RunResult.Failed when status.Current != true: stale++; break;
                case RunResult.Passed: passed++; break;
                case RunResult.Failed: failed++; break;
                case RunResult.Unsupported: unsupported++; break;
                default: notRun++; break;
            }
        }
        return new EvidenceCountsDto(passed, failed, notRun, unsupported, stale);
    }

    private static ObligationStatusDto Status(
        Obligation obligation, PromiseRevisionState revision, IReadOnlyDictionary<string, VerificationRun> latestRuns, PromiseWorld world, List<string> gaps)
    {
        ObligationStatusDto Without(string result, string detail) =>
            new(obligation.Id, result, null, null, null, null, null, null, null, null, [], [], detail, null, [], null, null);

        // A kind Vellum does not know is unsupported whatever was recorded for it. It never passes.
        if (!ObligationKind.IsDeterministic(obligation.Kind) && !ObligationKind.IsPlan(obligation.Kind))
        {
            var detail = $"Vellum cannot check an obligation of kind '{obligation.Kind}'.";
            gaps.Add($"{obligation.Id}: {detail} It is unsupported and does not count as evidence.");
            return Without(RunResult.Unsupported, detail);
        }

        latestRuns.TryGetValue(PromiseState.RunKey(revision.Revision, obligation.Id), out var run);
        // A run recorded before inputs were bound has a hash of another form; it is shown below as not current.
        if (run is { Inputs: not null } && run.ObligationHash != ObligationHash(obligation))
        {
            gaps.Add($"{obligation.Id} was changed after its last run. That run does not apply to it.");
            run = null;
        }
        if (run is { Draft: true } && !revision.IsDraft)
        {
            gaps.Add(string.Create(CultureInfo.InvariantCulture,
                $"{obligation.Id} was last run while revision {revision.Revision} was a draft. A run of a draft does not count for an approved revision; run it again."));
            run = null;
        }

        if (run is null)
        {
            var noSource = obligation.Recipe.Plan is { EvidenceSource: null };
            var detail = ObligationKind.IsPlan(obligation.Kind)
                ? noSource
                    ? "A test plan with no evidence source. Vellum cannot execute it, and it can receive no result until a revision names the workflow whose run is its evidence."
                    : "A test plan. Vellum cannot execute it, and no external evidence is recorded."
                : "Not run.";
            gaps.Add(ObligationKind.IsPlan(obligation.Kind)
                ? noSource
                    ? $"{obligation.Id} is a test plan with no evidence source. No result can be recorded for it."
                    : $"{obligation.Id} is a test plan with no external evidence. A generated plan alone establishes nothing."
                : $"{obligation.Id} has not been run.");
            return Without(RunResult.NotRun, detail);
        }

        var changed = new List<string>();
        switch (run.Result)
        {
            case RunResult.NotRun:
                gaps.Add(run.Evidence.FirstOrDefault(e => e.Identity == EvidenceIdentity.Claimed) is { } claim
                    ? $"{obligation.Id} has only a claim of '{claim.ClaimedResult}' that Vellum could not check ({claim.IdentityReason}). It counts as not run."
                    : $"{obligation.Id} was not run: {run.Detail}");
                break;
            case RunResult.Unsupported:
                gaps.Add($"{obligation.Id} is unsupported for the input it was given: {run.Detail}");
                break;
            default:
                changed = Changes(obligation, run, world);
                foreach (var change in changed)
                    gaps.Add($"{obligation.Id} {run.Result} (stale): {change}");
                foreach (var assertion in (run.Assertions ?? []).Where(a => a.Vacuous))
                    gaps.Add($"{obligation.Id}: assertion '{assertion.Id}' is vacuous. {assertion.VacuousReason}");
                break;
        }

        return new ObligationStatusDto(
            obligation.Id, run.Result, run.Result is RunResult.Passed or RunResult.Failed ? changed.Count == 0 : null,
            run.Id, run.At, run.Environment, run.ModelRevision, run.CommitSha, run.ScanId,
            run.Scope, run.Evidence, run.Limitations, run.Detail, run.Inputs, changed, run.Assertions, run.EffectiveFaults);
    }

    /// <summary>
    /// Every input of a run that is not the current one, as a sentence. A result is evidence
    /// only for exactly what it read, so any difference makes it stale: the model stream and
    /// its version, the scenario version and content, each schema version, and the commit.
    /// </summary>
    private static List<string> Changes(Obligation obligation, VerificationRun run, PromiseWorld world)
    {
        var changes = new List<string>();
        if (run.Inputs is not { } inputs)
        {
            changes.Add("the run was recorded before results were bound to their inputs, so nothing shows what it read. Run it again.");
            return changes;
        }

        if (inputs.ModelStreamId is { } streamId)
        {
            if (streamId != world.MainStreamId)
                changes.Add($"it ran on model stream {streamId}, not on stream {world.MainStreamId} that this view is for.");
            else if (inputs.ModelVersion != world.ModelVersion)
            {
                changes.Add(string.Create(CultureInfo.InvariantCulture,
                    $"it ran on model version {inputs.ModelVersion}. The model is now at version {world.ModelVersion}."));
            }
        }

        if (inputs.ScenarioId is { } scenarioId)
        {
            if (!world.Scenarios.TryGetValue(scenarioId, out var scenario))
                changes.Add($"the scenario {scenarioId} it played no longer exists.");
            else if (inputs.ScenarioVersion != scenario.Version || inputs.ScenarioHash != scenario.Hash)
            {
                changes.Add(string.Create(CultureInfo.InvariantCulture,
                    $"it played scenario '{scenario.Document.Name}' ({scenarioId}) at version {inputs.ScenarioVersion}. The scenario is now at version {scenario.Version}{(inputs.ScenarioHash == scenario.Hash ? " with the same content" : " and its content changed")}."));
            }
        }

        foreach (var schema in inputs.Schemas ?? [])
        {
            if (!world.SchemaVersions.TryGetValue(schema.Id, out var version))
                changes.Add($"the schema {schema.Id} it validated against no longer exists.");
            else if (version != schema.Version)
            {
                changes.Add(string.Create(CultureInfo.InvariantCulture,
                    $"it validated against schema {schema.Id} at version {schema.Version}. The schema is now at version {version}."));
            }
        }

        if (inputs is { Repository: { } repository, CommitSha: { } commit } && world.Followed(repository) is { } followed && followed.Commit != commit)
        {
            changes.Add(followed.Accepted
                ? $"evidence is for {commit}; the model follows {followed.Commit}."
                : $"evidence is for {commit}; the newest scan of {repository} is at {followed.Commit}.");
        }

        return changes;
    }

    /// <summary>What a run is bound to: the kind and the recipe. A new title does not unbind a run.</summary>
    public static string ObligationHash(Obligation obligation) =>
        PromiseJson.StableHash(new { obligation.Kind, obligation.Recipe });
}
