using System.Globalization;

namespace Vellum.Modules.Promises;

public sealed record EvidenceCountsDto(int Passed, int Failed, int NotRun, int Unsupported);

/// <param name="Key">The last part of the ID (<c>O2</c>): what a URL names the obligation by.</param>
/// <param name="Supported">True for the kinds Vellum checks by itself. Derived from the kind.</param>
/// <param name="Execution"><c>deterministic</c>, <c>external-evidence</c> or <c>none</c>.</param>
public sealed record ObligationDto(
    string Id, string Key, int Number, string Kind, bool Supported, string Execution,
    string Title, ObligationRecipe Recipe, string Source)
{
    public static ObligationDto From(Obligation obligation) => new(
        obligation.Id, obligation.Key, obligation.Number, obligation.Kind,
        ObligationKind.IsDeterministic(obligation.Kind), ObligationKind.Execution(obligation.Kind),
        obligation.Title, obligation.Recipe, obligation.Source);
}

/// <summary>
/// The result of one obligation: that of its newest run, with what exactly was checked. An
/// obligation with no run that applies is <c>not-run</c>, and one of a kind Vellum does not
/// know is <c>unsupported</c>.
/// </summary>
public sealed record ObligationStatusDto(
    string ObligationId, string Result, Guid? RunId, DateTimeOffset? At, string? Environment,
    ModelPin? ModelRevision, string? CommitSha, Guid? ScanId, string? Scope,
    IReadOnlyList<EvidenceItem> Evidence, IReadOnlyList<string> Limitations, string? Detail);

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
/// <param name="InForce">True for the newest approved revision.</param>
public sealed record PromiseRevisionDto(
    int Revision, string Status, bool Retires, bool InForce,
    string Title, string Claim, string? Scope, string? Preconditions, string? ExpectedOutcome,
    string? ForbiddenOutcome, string? EvidenceCriteria,
    IReadOnlyList<ForbiddenEvent> ForbiddenEvents, RequiredPattern? RequiresPattern, PromiseLinks Links,
    IReadOnlyList<ObligationDto> Obligations,
    string CreatedBy, DateTimeOffset CreatedAt, string? ApprovedBy, DateTimeOffset? ApprovedAt, long? ApprovedModelVersion,
    EvidenceStatusDto EvidenceStatus);

/// <param name="State"><c>draft</c>, <c>approved</c> or <c>retired</c>.</param>
/// <param name="NeedsReview">True when a linked thing no longer exists. Computed on read.</param>
/// <param name="Current">The revision in force, or the draft when none was approved.</param>
/// <param name="Draft">The unapproved revision, when one exists beside <paramref name="Current"/>.</param>
public sealed record PromiseDto(
    Guid Id, Guid ProjectId, string RequirementId, string Origin, string State,
    int LatestRevision, int? InForceRevision, bool NeedsReview, IReadOnlyList<MissingLinkDto> MissingLinks,
    PromiseRevisionDto Current, PromiseRevisionDto? Draft, IReadOnlyList<PromiseRevisionDto> Revisions,
    string CreatedBy, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public sealed record PromiseSummaryDto(
    Guid Id, string RequirementId, string Title, string State, string Origin,
    int LatestRevision, int? InForceRevision, bool HasDraft, bool NeedsReview,
    EvidenceCountsDto Counts, DateTimeOffset UpdatedAt);

public sealed record PrefixDto(string? Prefix, int? NextNumber);

public static class PromiseViews
{
    public static PromiseDto ToDto(Guid id, PromiseState state, IReadOnlyDictionary<string, VerificationRun> latestRuns, PromiseWorld world)
    {
        var inForce = state.InForce;
        var current = inForce ?? state.Latest!;
        var revisions = state.Revisions.Select(r => Revision(r, r == inForce, latestRuns, world)).ToList();
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
        return new PromiseSummaryDto(
            id, state.RequirementId!, current.Content.Title, state.Lifecycle, state.Origin!,
            state.Latest!.Revision, state.InForce?.Revision, state.Draft is not null,
            Missing(current, state.Draft, world).Count > 0,
            Counts(current.Obligations.Select(o => Status(o, current.Revision, latestRuns, world, null))),
            state.UpdatedAt!.Value);
    }

    private static List<MissingLinkDto> Missing(PromiseRevisionState current, PromiseRevisionState? draft, PromiseWorld world) =>
        current.Content.Links.All().Concat(draft?.Content.Links.All() ?? [])
            .Distinct()
            .Where(link => !world.Known.Has(link.Kind, link.Id))
            .Select(link => new MissingLinkDto(link.Kind, link.Id))
            .ToList();

    private static PromiseRevisionDto Revision(
        PromiseRevisionState revision, bool inForce, IReadOnlyDictionary<string, VerificationRun> latestRuns, PromiseWorld world)
    {
        var content = revision.Content;
        return new PromiseRevisionDto(
            revision.Revision, revision.Status, revision.Retires, inForce,
            content.Title, content.Claim, content.Scope, content.Preconditions, content.ExpectedOutcome,
            content.ForbiddenOutcome, content.EvidenceCriteria,
            content.ForbiddenEvents, content.RequiresPattern, content.Links,
            revision.Obligations.Select(ObligationDto.From).ToList(),
            revision.CreatedBy, revision.CreatedAt, revision.ApprovedBy, revision.ApprovedAt, revision.ApprovedModelVersion,
            Evidence(revision, latestRuns, world));
    }

    /// <summary>The evidence status of one revision. Every sentence in <c>Gaps</c> names what is not established.</summary>
    public static EvidenceStatusDto Evidence(
        PromiseRevisionState revision, IReadOnlyDictionary<string, VerificationRun> latestRuns, PromiseWorld world)
    {
        var gaps = new List<string>();
        if (revision.IsDraft)
            gaps.Add(string.Create(CultureInfo.InvariantCulture, $"Revision {revision.Revision} is a draft. It is not approved and is not in force."));
        if (revision.Obligations.Count == 0)
            gaps.Add("No proof obligation exists for this revision.");

        var statuses = revision.Obligations.Select(o => Status(o, revision.Revision, latestRuns, world, gaps)).ToList();

        foreach (var (kind, id) in revision.Content.Links.All())
        {
            if (!world.Known.Has(kind, id))
                gaps.Add($"The linked {kind} {id} no longer exists.");
            else if (revision.ApprovedLinks is { } approved
                     && approved.TryGetValue(PromiseWorld.LinkKey(kind, id), out var then)
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

    private static EvidenceCountsDto Counts(IEnumerable<ObligationStatusDto> statuses)
    {
        int passed = 0, failed = 0, notRun = 0, unsupported = 0;
        foreach (var status in statuses)
        {
            switch (status.Result)
            {
                case RunResult.Passed: passed++; break;
                case RunResult.Failed: failed++; break;
                case RunResult.Unsupported: unsupported++; break;
                default: notRun++; break;
            }
        }
        return new EvidenceCountsDto(passed, failed, notRun, unsupported);
    }

    private static ObligationStatusDto Status(
        Obligation obligation, int revision, IReadOnlyDictionary<string, VerificationRun> latestRuns, PromiseWorld world, List<string>? gaps)
    {
        ObligationStatusDto Without(string result, string detail) =>
            new(obligation.Id, result, null, null, null, null, null, null, null, [], [], detail);

        // A kind Vellum does not know is unsupported whatever was recorded for it. It never passes.
        if (!ObligationKind.IsDeterministic(obligation.Kind) && !ObligationKind.IsPlan(obligation.Kind))
        {
            var detail = $"Vellum cannot check an obligation of kind '{obligation.Kind}'.";
            gaps?.Add($"{obligation.Id}: {detail} It is unsupported and does not count as evidence.");
            return Without(RunResult.Unsupported, detail);
        }

        latestRuns.TryGetValue(PromiseProjection.LatestRunKey(revision, obligation.Id), out var run);
        if (run is not null && run.ObligationHash != ObligationHash(obligation))
        {
            gaps?.Add($"{obligation.Id} was changed after its last run. That run does not apply to it.");
            run = null;
        }

        if (run is null)
        {
            var detail = ObligationKind.IsPlan(obligation.Kind)
                ? "A test plan. Vellum cannot execute it, and no external evidence is recorded."
                : "Not run.";
            gaps?.Add(ObligationKind.IsPlan(obligation.Kind)
                ? $"{obligation.Id} is a test plan with no external evidence. A generated plan alone establishes nothing."
                : $"{obligation.Id} has not been run.");
            return Without(RunResult.NotRun, detail);
        }

        switch (run.Result)
        {
            case RunResult.NotRun:
                gaps?.Add(run.Evidence.FirstOrDefault(e => e.Identity == EvidenceIdentity.Claimed) is { } claim
                    ? $"{obligation.Id} has only a claim of '{claim.ClaimedResult}' that Vellum could not check ({claim.IdentityReason}). It counts as not run."
                    : $"{obligation.Id} was not run: {run.Detail}");
                break;
            case RunResult.Unsupported:
                gaps?.Add($"{obligation.Id} is unsupported for the input it was given: {run.Detail}");
                break;
            default:
                if (run.ModelRevision is { } pin)
                {
                    if (pin.StreamId != world.MainStreamId)
                        gaps?.Add($"{obligation.Id} {run.Result} on model stream {pin.StreamId}, not on stream {world.MainStreamId} that this view is for.");
                    else if (pin.Version < world.ModelVersion)
                        gaps?.Add(string.Create(CultureInfo.InvariantCulture,
                            $"{obligation.Id} {run.Result} on model version {pin.Version}. The model is now at version {world.ModelVersion}."));
                    else if (pin.Version > world.ModelVersion)
                        gaps?.Add(string.Create(CultureInfo.InvariantCulture,
                            $"{obligation.Id} {run.Result} on model version {pin.Version}, which is later than version {world.ModelVersion} that this view is pinned to."));
                }
                if (run.CommitSha is { } commit && obligation.Recipe.SourcePattern is { } source
                    && world.LatestCommits.TryGetValue(source.Repository, out var latest) && latest != commit)
                    gaps?.Add($"{obligation.Id} {run.Result} at commit {commit}. The newest scan of {source.Repository} is at commit {latest}.");
                break;
        }

        return new ObligationStatusDto(
            obligation.Id, run.Result, run.Id, run.At, run.Environment, run.ModelRevision, run.CommitSha, run.ScanId,
            run.Scope, run.Evidence, run.Limitations, run.Detail);
    }

    /// <summary>What a run is bound to: the kind and the recipe. A new title does not unbind a run.</summary>
    public static string ObligationHash(Obligation obligation) =>
        PromiseJson.Hash(new { obligation.Kind, obligation.Recipe });
}
