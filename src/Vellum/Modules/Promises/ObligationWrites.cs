using System.Globalization;
using Deedbox;
using Microsoft.EntityFrameworkCore;
using Vellum.Modules.GitHub;
using Vellum.Modules.Identity.AgentTokens;
using Vellum.Modules.Import;
using Vellum.Modules.Promises.Checks;
using Vellum.Modules.Schemas;
using Vellum.Modules.Settings.Document;
using Vellum.Modules.Workspaces;
using Vellum.Shared;
using static Vellum.Shared.ResultExtensions;

namespace Vellum.Modules.Promises;

public sealed record ObligationRequest(string Kind, string Title, ObligationRecipe Recipe);

/// <param name="Promise">The promise after the generation.</param>
/// <param name="Added">The IDs of the obligations this call added, in order.</param>
/// <param name="Skipped">The links that gave no obligation, with the reason.</param>
public sealed record GenerateObligationsResponse(
    PromiseDto Promise, IReadOnlyList<string> Added, IReadOnlyList<ObligationGenerator.Skipped> Skipped);

/// <param name="ModelRevisionId">For a scenario-check: the named model revision to play on.</param>
/// <param name="BranchId">For a scenario-check: a branch to play on at its current version. Default: the main model.</param>
/// <param name="ScanId">For a source-pattern-check: the completed scan whose snapshot is looked at.</param>
/// <param name="Revision">
/// The promise revision whose obligation runs. Default: the approved revision in force. A
/// draft runs only when it is named here; such a run is labelled <c>draft</c> and never
/// counts for an approved revision.
/// </param>
public sealed record RunObligationRequest(Guid? ModelRevisionId = null, Guid? BranchId = null, Guid? ScanId = null, int? Revision = null);

/// <param name="RequirementId">The promise of the check.</param>
/// <param name="Result">What the run showed against the scan: <c>passed</c>, <c>failed</c> or <c>unsupported</c>, or the refusal code when no run was recorded.</param>
public sealed record ScanCheckRunDto(Guid PromiseId, string RequirementId, int Revision, string ObligationId, string Key, string Result, Guid? RunId, string? Detail);

/// <param name="Runs">One entry for each source pattern check of an approved promise that names the repository of the scan, by requirement ID.</param>
/// <param name="Skipped">Checks over <see cref="ObligationWrites.MaxScanChecks"/> that were not run.</param>
public sealed record ScanChecksResponse(Guid ScanId, IReadOnlyList<ScanCheckRunDto> Runs, int Skipped);

/// <summary>
/// Obligations are edited only inside a draft revision; after approval they are fixed. A run
/// or a piece of external evidence adds a <see cref="VerificationRun"/> and changes nothing
/// else: it never edits or approves a promise.
/// </summary>
public static class ObligationWrites
{
    /// <summary>The most checks one call of <see cref="RunForScan"/> runs.</summary>
    public const int MaxScanChecks = 200;

    /// <summary>
    /// Runs every source pattern check of every approved promise that names the repository of
    /// the scan, against the snapshot of that scan, and records each run with the scan and its
    /// commit. A run against a comparison scan is for that scan only (see
    /// <see cref="PromiseState.CountsAsLatest"/>). An agent token with <c>propose</c> runs them
    /// for any scan; a token with only <c>scan-pull-request</c> runs them for a scan it started.
    /// A check that already ran against a comparison scan with the same obligation is not run
    /// again: the call returns that run, so a repeated call adds nothing. Each new run takes one
    /// unit of the caller's run rate; when the rate is used up the call stops with 429, and a
    /// later call goes on where it stopped.
    /// </summary>
    public static async Task<IResult> RunForScan(
        Guid projectId, Guid importId, HttpContext http,
        WorkspaceAuthorizationService auth, IEventStore store, AppDbContext db,
        PromiseLimits limits, PromiseRunRate rate, TimeProvider time, CancellationToken ct)
    {
        var userId = http.User.UserId();
        var actor = PromiseAccess.Actor(http.User);
        await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Editor, ct);
        var scan = await db.Scans.AsNoTracking().FirstOrDefaultAsync(s => s.Id == importId && s.ProjectId == projectId, ct);
        if (scan is null)
            return Results.NotFound(new ErrorResponse("not_found", "Import not found"));
        if (AgentPrincipal.IsAgent(http.User) && !AgentPrincipal.HasScope(http.User, AgentScope.Propose)
            && scan.RequestedBy != AgentPrincipal.Actor(http.User))
        {
            return Results.Json(new ErrorResponse(AgentBoundary.AgentNotAllowedError, "This agent token cannot do this",
                "A token with the scope scan-pull-request runs the checks only against a scan that it started."),
                statusCode: StatusCodes.Status403Forbidden);
        }
        if (scan.Status != Import.Scans.ScanStatus.Completed)
            return Results.Conflict(new ErrorResponse("import_not_completed", "Only a completed scan can be checked."));

        var rows = await db.Promises.AsNoTracking()
            .Where(p => p.ProjectId == projectId && p.Lifecycle == PromiseLifecycle.Approved)
            .OrderBy(p => p.Number)
            .Select(p => new { p.Id, p.RequirementId, p.Document })
            .ToListAsync(ct);
        var checks = rows
            .Select(row => (row.Id, row.RequirementId, Revision: PromiseJson.Read<PromiseState>(row.Document).InForce))
            .Where(p => p.Revision is not null)
            .SelectMany(p => p.Revision!.Obligations
                .Where(o => o.Kind == ObligationKind.SourcePatternCheck && o.Recipe.SourcePattern?.Repository == scan.Repository)
                .Select(o => (p.Id, p.RequirementId, Revision: p.Revision!.Revision, Obligation: o)))
            .ToList();

        // The snapshot, its facts and its files are read once for all checks.
        var inputs = await SourcePatternCheck.ScanInputs.LoadAsync(db, projectId, scan, ct);
        var runs = new List<ScanCheckRunDto>();
        var batch = checks.Take(MaxScanChecks).ToList();
        foreach (var (promiseId, requirementId, revision, obligation) in batch)
        {
            var hash = PromiseViews.ObligationHash(obligation);
            if (inputs.IsComparison && await ExistingComparisonRunAsync(db, promiseId, revision, draft: false, obligation.Id, scan.Id, hash, ct) is { } existing)
            {
                runs.Add(new ScanCheckRunDto(promiseId, requirementId, revision, obligation.Id, obligation.Key, existing.Result, existing.Id, existing.Detail));
                continue;
            }
            if (!rate.TryTake(http, out var seconds))
            {
                return CallerRate.Refusal(http, seconds, string.Create(CultureInfo.InvariantCulture,
                    $"{runs.Count} of {batch.Count} checks ran before the run rate of this caller was used up. Call again in {seconds} seconds: the checks that ran against this scan are not run again."));
            }
            var outcome = await SourcePatternCheck.RunAsync(db, projectId, promiseId, obligation.Id, hash, obligation.Recipe.SourcePattern!, inputs, ct);
            var (run, refusal) = await RecordRunAsync(store, projectId, promiseId, revision, obligation, hash, outcome, userId, actor, limits, time, ct);
            runs.Add(new ScanCheckRunDto(promiseId, requirementId, revision, obligation.Id, obligation.Key, run?.Result ?? "not-recorded", run?.Id,
                run?.Detail ?? refusal));
        }
        return Results.Ok(new ScanChecksResponse(scan.Id, runs, Math.Max(0, checks.Count - MaxScanChecks)));
    }

    /// <summary>
    /// The run of this obligation of this revision against this comparison scan, with the same
    /// obligation hash and the same draft label, when one was recorded. Such a run is not
    /// recorded twice: the snapshot of a scan does not change.
    /// </summary>
    private static async Task<VerificationRun?> ExistingComparisonRunAsync(
        AppDbContext db, Guid promiseId, int revision, bool draft, string obligationId, Guid scanId, string obligationHash, CancellationToken ct)
    {
        var probe = System.Text.Json.JsonSerializer.Serialize(new { scanId, obligationHash, inputs = new { scanKind = Import.Scans.ScanKind.Comparison } });
        var documents = await db.PromiseRuns.AsNoTracking()
            .Where(r => r.PromiseId == promiseId && r.PromiseRevision == revision && r.ObligationId == obligationId && EF.Functions.JsonContains(r.Document, probe))
            .OrderByDescending(r => r.Version)
            .Select(r => r.Document)
            .ToListAsync(ct);
        return documents.Select(PromiseJson.Read<VerificationRun>).FirstOrDefault(r => (r.Draft == true) == draft);
    }

    public static async Task<IResult> Add(
        Guid projectId, Guid promiseId, HttpContext http,
        WorkspaceAuthorizationService auth, IEventStore store, PromiseReader reader, TimeProvider time, CancellationToken ct)
    {
        var userId = http.User.UserId();
        await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Editor, ct);
        var (request, error) = await PromiseAccess.ReadAsync<ObligationRequest>(http);
        if (request is null)
            return error!;

        var world = await WorldForAsync(http.RequestServices, projectId, request.Recipe, ct);
        var now = time.GetUtcNow();
        var result = await PromiseAccess.DecideAsync(store, promiseId,
            state => PromiseDecider.AddObligation(
                state, projectId, new(request.Kind, request.Title, request.Recipe), world.Known, userId, PromiseAccess.Actor(http.User), now), ct);
        return result.Succeeded
            ? Results.Created($"/api/projects/{projectId}/promises/{promiseId}", await reader.GetAsync(projectId, promiseId, ct))
            : result.Refusal();
    }

    /// <summary>
    /// What a recipe is checked against: the scenario it plays, so that its faults can be
    /// compared with the scenario's own, and the workspace's GitHub host when it names an
    /// evidence source.
    /// </summary>
    private static Task<PromiseWorld> WorldForAsync(IServiceProvider services, Guid projectId, ObligationRecipe? recipe, CancellationToken ct) =>
        PromiseWorld.LoadAsync(
            services, projectId, recipe?.ScenarioCheck is { } check ? [check.ScenarioId] : [], ct,
            withGitHubHost: recipe?.Plan?.EvidenceSource is not null);

    public static async Task<IResult> Change(
        Guid projectId, Guid promiseId, string obligationKey, HttpContext http,
        WorkspaceAuthorizationService auth, IEventStore store, PromiseReader reader, TimeProvider time, CancellationToken ct)
    {
        var userId = http.User.UserId();
        await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Editor, ct);
        var (request, error) = await PromiseAccess.ReadAsync<ObligationRequest>(http);
        if (request is null)
            return error!;

        var world = await WorldForAsync(http.RequestServices, projectId, request.Recipe, ct);
        var now = time.GetUtcNow();
        var (actor, agent) = (PromiseAccess.Actor(http.User), AgentPrincipal.IsAgent(http.User));
        var humanOnly = false;
        var result = await PromiseAccess.DecideAsync(store, promiseId, state =>
        {
            humanOnly = agent && NotOfAgentsAlone(state, projectId, obligationKey);
            return humanOnly
                ? new CommandResult<IReadOnlyList<object>>.Conflict(HumanObligation)
                : PromiseDecider.ChangeObligation(
                    state, projectId, obligationKey, new(request.Kind, request.Title, request.Recipe), world.Known, userId, actor, now);
        }, ct);
        return humanOnly
            ? await PromiseAccess.HumanOnlyAsync(http, HumanObligation)
            : await PromiseWrites.RespondAsync(result, reader, projectId, promiseId, ct);
    }

    private const string HumanObligation =
        "A person wrote, changed or approved this obligation. An agent token changes or removes only an obligation that an agent token added to the draft and that no person changed since. Ask a person to do it in the app.";

    /// <summary>
    /// True for an obligation of the draft that exists and is not of agent tokens alone. An
    /// obligation or a promise that does not exist gets the answer of the decision (404).
    /// </summary>
    private static bool NotOfAgentsAlone(PromiseState state, Guid projectId, string obligationKey) =>
        state.ExistsIn(projectId) && state.Draft is { Retires: false } draft && draft.Find(obligationKey) is not null
        && !PromiseDecider.AgentAlone(state, obligationKey);

    public static async Task<IResult> Remove(
        Guid projectId, Guid promiseId, string obligationKey, HttpContext http,
        WorkspaceAuthorizationService auth, IEventStore store, PromiseReader reader, TimeProvider time, CancellationToken ct)
    {
        var userId = http.User.UserId();
        await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Editor, ct);

        var now = time.GetUtcNow();
        var (actor, agent) = (PromiseAccess.Actor(http.User), AgentPrincipal.IsAgent(http.User));
        var humanOnly = false;
        var result = await PromiseAccess.DecideAsync(store, promiseId, state =>
        {
            humanOnly = agent && NotOfAgentsAlone(state, projectId, obligationKey);
            return humanOnly
                ? new CommandResult<IReadOnlyList<object>>.Conflict(HumanObligation)
                : PromiseDecider.RemoveObligation(state, projectId, obligationKey, userId, actor, now);
        }, ct);
        return humanOnly
            ? await PromiseAccess.HumanOnlyAsync(http, HumanObligation)
            : await PromiseWrites.RespondAsync(result, reader, projectId, promiseId, ct);
    }

    /// <summary>Adds the default obligations of the draft (see <see cref="ObligationGenerator"/>). No AI is called.</summary>
    public static async Task<IResult> Generate(
        Guid projectId, Guid promiseId, HttpContext http,
        WorkspaceAuthorizationService auth, IEventStore store, AppDbContext db, PromiseReader reader, TimeProvider time, CancellationToken ct)
    {
        var userId = http.User.UserId();
        await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Editor, ct);

        var row = await db.Promises.AsNoTracking().FirstOrDefaultAsync(p => p.Id == promiseId && p.ProjectId == projectId, ct);
        if (row is null)
            return NotFound("Promise");
        var before = PromiseJson.Read<PromiseState>(row.Document);
        var world = await PromiseWorld.LoadAsync(http.RequestServices, projectId, before.Latest!.Content.Links.ScenarioIds, ct);

        IReadOnlyList<ObligationGenerator.Skipped> skipped = [];
        var now = time.GetUtcNow();
        var result = await PromiseAccess.DecideAsync(store, promiseId,
            state => PromiseDecider.AddGenerated(state, projectId, draft =>
            {
                var (candidates, notMade) = ObligationGenerator.Generate(
                    draft, world.Model, world.Scenarios.ToDictionary(s => s.Key, s => s.Value.Document));
                skipped = notMade;
                return candidates;
            }, userId, PromiseAccess.Actor(http.User), now), ct);
        if (!result.Succeeded || result.Result is not CommandResult<PromiseState>.Success success)
            return result.Refusal();

        var known = before.Latest.Obligations.Select(o => o.Id).ToHashSet(StringComparer.Ordinal);
        return Results.Ok(new GenerateObligationsResponse(
            (await reader.GetAsync(projectId, promiseId, ct))!,
            [.. success.Value.Latest!.Obligations.Select(o => o.Id).Where(id => !known.Contains(id))],
            skipped));
    }

    /// <summary>
    /// Runs a deterministic obligation and stores the run. The same obligation on the same
    /// inputs gives the same result and the same evidence hash.
    /// </summary>
    public static async Task<IResult> Run(
        Guid projectId, Guid promiseId, string obligationKey, HttpContext http,
        WorkspaceAuthorizationService auth, IEventStore store, AppDbContext db,
        PromiseLimits limits, TimeProvider time, CancellationToken ct)
    {
        var userId = http.User.UserId();
        var actor = PromiseAccess.Actor(http.User);
        await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Editor, ct);
        var (body, error) = await PromiseAccess.ReadAsync<RunObligationRequest>(http, allowEmpty: true);
        if (error is not null)
            return error;
        var request = body ?? new RunObligationRequest();

        var (found, refusal) = await FindAsync(db, projectId, promiseId, obligationKey, request.Revision, ct);
        if (found is not { } located)
            return refusal!;
        var (revision, obligation, isDraft) = located;
        var obligationHash = PromiseViews.ObligationHash(obligation);

        CheckOutcome outcome;
        switch (obligation.Kind)
        {
            case ObligationKind.ScenarioCheck:
                var (pinned, pinRefusal) = await PinnedModel.ResolveAsync(http.RequestServices, projectId, request.ModelRevisionId, request.BranchId, ct);
                if (pinned is null)
                    return pinRefusal!;
                outcome = await ScenarioCheck.RunAsync(store, db, projectId, obligation.Recipe.ScenarioCheck!, pinned, obligationHash, ct);
                break;

            case ObligationKind.SourcePatternCheck:
                var recipe = obligation.Recipe.SourcePattern!;
                if (request.ScanId is not { } scanId)
                {
                    outcome = SourcePatternCheck.NoSnapshotGiven(recipe, obligationHash);
                    break;
                }
                var (scan, scanRefusal) = await SourcePatternCheck.FindScanAsync(db, projectId, recipe, scanId, ct);
                if (scan is null)
                    return Invalid("scanId", scanRefusal!);
                var inputs = await SourcePatternCheck.ScanInputs.LoadAsync(db, projectId, scan, ct);
                if (inputs.IsComparison
                    && await ExistingComparisonRunAsync(db, promiseId, revision, isDraft, obligation.Id, scan.Id, obligationHash, ct) is { } existing)
                    return Results.Ok(existing);
                outcome = await SourcePatternCheck.RunAsync(db, projectId, promiseId, obligation.Id, obligationHash, recipe, inputs, ct);
                break;

            default:
                return Results.Conflict(new ErrorResponse("not_runnable",
                    ObligationKind.IsPlan(obligation.Kind)
                        ? "Vellum cannot execute a test plan. Run the test outside Vellum and record its result as evidence."
                        : $"Vellum cannot check an obligation of kind '{obligation.Kind}'. It is unsupported."));
        }

        return await RecordAsync(store, projectId, promiseId, revision, obligation, obligationHash, outcome, userId, actor, limits, time, ct);
    }

    /// <summary>
    /// Records the result of a test that ran outside Vellum, for a test-plan obligation. The
    /// result counts only when Vellum reads from the system that the run is the one the
    /// obligation's evidence source names; a plan without an evidence source gets no result.
    /// </summary>
    public static async Task<IResult> Evidence(
        Guid projectId, Guid promiseId, string obligationKey, HttpContext http,
        WorkspaceAuthorizationService auth, IEventStore store, AppDbContext db,
        GitHubReader gitHub, SettingsResolver settings, PromiseLimits limits, TimeProvider time, CancellationToken ct)
    {
        var userId = http.User.UserId();
        var actor = PromiseAccess.Actor(http.User);
        await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Editor, ct);
        var (request, error) = await PromiseAccess.ReadAsync<RecordEvidenceRequest>(http);
        if (request is null)
            return error!;
        if (ExternalEvidence.Check(request) is { Count: > 0 } errors)
        {
            return Results.BadRequest(new ErrorResponse("validation_error", "Validation failed",
                Errors: errors.Select(e => new FieldError(e.Field, e.Message)).ToList()));
        }

        var (found, refusal) = await FindAsync(db, projectId, promiseId, obligationKey, request.Revision, ct);
        if (found is not { } located)
            return refusal!;
        var (revision, obligation, _) = located;
        if (!ObligationKind.IsPlan(obligation.Kind))
        {
            return Results.Conflict(new ErrorResponse("not_a_test_plan",
                ObligationKind.IsDeterministic(obligation.Kind)
                    ? "Vellum runs this obligation itself. External evidence is for test plans."
                    : $"An obligation of kind '{obligation.Kind}' is unsupported. No evidence makes it pass."));
        }

        var workspaceId = await auth.GetProjectWorkspaceIdAsync(projectId, ct);
        var obligationHash = PromiseViews.ObligationHash(obligation);
        var outcome = await ExternalEvidence.AssessAsync(
            request, obligation.Recipe.Plan?.EvidenceSource, workspaceId, projectId, obligationHash, gitHub, settings, db, ct);
        return await RecordAsync(store, projectId, promiseId, revision, obligation, obligationHash, outcome, userId, actor, limits, time, ct);
    }

    private static async Task<((int Revision, Obligation Obligation, bool IsDraft)? Found, IResult? Refusal)> FindAsync(
        AppDbContext db, Guid projectId, Guid promiseId, string obligationKey, int? revisionNumber, CancellationToken ct)
    {
        var row = await db.Promises.AsNoTracking().FirstOrDefaultAsync(p => p.Id == promiseId && p.ProjectId == projectId, ct);
        if (row is null)
            return (null, NotFound("Promise"));

        // With no revision named, the run is of the approved revision in force. A draft is
        // checked only when the caller names it.
        var state = PromiseJson.Read<PromiseState>(row.Document);
        if (revisionNumber is null && state.InForce is null)
        {
            return (null, Results.Conflict(new ErrorResponse("no_approved_revision",
                "The promise has no approved revision",
                $"To check the draft, name it: send revision {state.Latest!.Revision}. A run of a draft is labelled draft and does not count after approval.")));
        }
        var revision = revisionNumber is { } number ? state.Revision(number) : state.InForce;
        if (revision is null)
            return (null, Results.NotFound(new ErrorResponse("not_found", "Revision not found")));
        return revision.Find(obligationKey) is { } obligation
            ? ((revision.Revision, obligation, revision.IsDraft), null)
            : (null, Results.NotFound(new ErrorResponse("not_found", "Obligation not found")));
    }

    private static async Task<IResult> RecordAsync(
        IEventStore store, Guid projectId, Guid promiseId, int revision, Obligation obligation, string obligationHash, CheckOutcome outcome,
        string userId, string actor, PromiseLimits limits, TimeProvider time, CancellationToken ct)
    {
        var (recorded, _, refusal) = await RecordCoreAsync(store, projectId, promiseId, revision, obligation, obligationHash, outcome, userId, actor, limits, time, ct);
        return recorded is not null ? Results.Ok(recorded) : refusal!;
    }

    /// <summary>The recorded run, or a sentence that says why none was recorded.</summary>
    private static async Task<(VerificationRun? Run, string? Refusal)> RecordRunAsync(
        IEventStore store, Guid projectId, Guid promiseId, int revision, Obligation obligation, string obligationHash, CheckOutcome outcome,
        string userId, string actor, PromiseLimits limits, TimeProvider time, CancellationToken ct)
    {
        var (recorded, why, _) = await RecordCoreAsync(store, projectId, promiseId, revision, obligation, obligationHash, outcome, userId, actor, limits, time, ct);
        return (recorded, recorded is null ? why ?? "The run was not recorded." : null);
    }

    private static async Task<(VerificationRun? Run, string? Why, IResult? Refusal)> RecordCoreAsync(
        IEventStore store, Guid projectId, Guid promiseId, int revision, Obligation obligation, string obligationHash, CheckOutcome outcome,
        string userId, string actor, PromiseLimits limits, TimeProvider time, CancellationToken ct)
    {
        var run = new VerificationRun(
            Guid.NewGuid(), obligation.Id, revision, obligationHash, userId, time.GetUtcNow(),
            outcome.ModelRevision, outcome.CommitSha, outcome.ScanId, outcome.Environment, outcome.Result, outcome.Scope,
            outcome.Evidence, outcome.Limitations, outcome.Detail,
            outcome.Inputs ?? new RunInputs(obligationHash), outcome.Assertions, outcome.EffectiveFaults, Actor: actor);

        // The decision sets the draft label from the stream, so the stored run is read back from the state.
        VerificationRun? recorded = null;
        var result = await PromiseAccess.DecideAsync(store, promiseId, state =>
        {
            var decision = PromiseDecider.RecordRun(state, projectId, run, limits.MaxRunsPerObligation, limits.MaxComparisonRunsPerObligation);
            recorded = decision is CommandResult<IReadOnlyList<object>>.Success { Value: [PromiseEvent.VerificationRunRecorded stored] } ? stored.Run : null;
            return decision;
        }, ct);
        return result.Succeeded && recorded is not null
            ? (recorded, null, null)
            : (null, result.Result is CommandResult<PromiseState>.Conflict conflict ? conflict.Message : null, result.Refusal());
    }
}
