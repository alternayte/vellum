using Deedbox;
using Microsoft.EntityFrameworkCore;
using Vellum.Modules.GitHub;
using Vellum.Modules.Identity.AgentTokens;
using Vellum.Modules.Import;
using Vellum.Modules.Promises.Checks;
using Vellum.Modules.Schemas;
using Vellum.Modules.Settings.Document;
using Vellum.Modules.Workspaces.Authorization;
using Vellum.Shared;

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

/// <summary>
/// Obligations are edited only inside a draft revision; after approval they are fixed. A run
/// or a piece of external evidence adds a <see cref="VerificationRun"/> and changes nothing
/// else: it never edits or approves a promise.
/// </summary>
public static class ObligationWrites
{
    public static async Task<IResult> Add(
        Guid projectId, Guid promiseId, HttpContext http,
        WorkspaceAuthorizationService auth, IEventStore store, PromiseReader reader, TimeProvider time, CancellationToken ct)
    {
        var userId = PromiseAccess.UserId(http.User);
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
        var userId = PromiseAccess.UserId(http.User);
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
        var userId = PromiseAccess.UserId(http.User);
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
        WorkspaceAuthorizationService auth, IEventStore store, PromisesDbContext db, PromiseReader reader, TimeProvider time, CancellationToken ct)
    {
        var userId = PromiseAccess.UserId(http.User);
        await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Editor, ct);

        var row = await db.Promises.AsNoTracking().FirstOrDefaultAsync(p => p.Id == promiseId && p.ProjectId == projectId, ct);
        if (row is null)
            return PromiseAccess.NotFound();
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
        WorkspaceAuthorizationService auth, IEventStore store, PromisesDbContext db, SchemasDbContext schemasDb, ImportDbContext importDb,
        PromiseLimits limits, TimeProvider time, CancellationToken ct)
    {
        var userId = PromiseAccess.UserId(http.User);
        await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Editor, ct);
        var (body, error) = await PromiseAccess.ReadAsync<RunObligationRequest>(http, allowEmpty: true);
        if (error is not null)
            return error;
        var request = body ?? new RunObligationRequest();

        var (found, refusal) = await FindAsync(db, projectId, promiseId, obligationKey, request.Revision, ct);
        if (found is not { } located)
            return refusal!;
        var (revision, obligation) = located;
        var obligationHash = PromiseViews.ObligationHash(obligation);

        CheckOutcome outcome;
        switch (obligation.Kind)
        {
            case ObligationKind.ScenarioCheck:
                var (pinned, pinRefusal) = await PinnedModel.ResolveAsync(http.RequestServices, projectId, request.ModelRevisionId, request.BranchId, ct);
                if (pinned is null)
                    return pinRefusal!;
                outcome = await ScenarioCheck.RunAsync(store, schemasDb, projectId, obligation.Recipe.ScenarioCheck!, pinned, obligationHash, ct);
                break;

            case ObligationKind.SourcePatternCheck:
                var recipe = obligation.Recipe.SourcePattern!;
                if (request.ScanId is not { } scanId)
                {
                    outcome = SourcePatternCheck.NoSnapshotGiven(recipe, obligationHash);
                    break;
                }
                var (scan, scanRefusal) = await SourcePatternCheck.FindScanAsync(importDb, projectId, recipe, scanId, ct);
                if (scan is null)
                    return PromiseAccess.Invalid("scanId", scanRefusal!);
                outcome = await SourcePatternCheck.RunAsync(importDb, db, projectId, promiseId, obligation.Id, obligationHash, recipe, scan, ct);
                break;

            default:
                return Results.Conflict(new ErrorResponse("not_runnable",
                    ObligationKind.IsPlan(obligation.Kind)
                        ? "Vellum cannot execute a test plan. Run the test outside Vellum and record its result as evidence."
                        : $"Vellum cannot check an obligation of kind '{obligation.Kind}'. It is unsupported."));
        }

        return await RecordAsync(store, projectId, promiseId, revision, obligation, obligationHash, outcome, userId, limits, time, ct);
    }

    /// <summary>
    /// Records the result of a test that ran outside Vellum, for a test-plan obligation. The
    /// result counts only when Vellum reads from the system that the run is the one the
    /// obligation's evidence source names; a plan without an evidence source gets no result.
    /// </summary>
    public static async Task<IResult> Evidence(
        Guid projectId, Guid promiseId, string obligationKey, HttpContext http,
        WorkspaceAuthorizationService auth, IEventStore store, PromisesDbContext db, ImportDbContext importDb,
        GitHubReader gitHub, SettingsResolver settings, PromiseLimits limits, TimeProvider time, CancellationToken ct)
    {
        var userId = PromiseAccess.UserId(http.User);
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
        var (revision, obligation) = located;
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
            request, obligation.Recipe.Plan?.EvidenceSource, workspaceId, projectId, obligationHash, gitHub, settings, importDb, ct);
        return await RecordAsync(store, projectId, promiseId, revision, obligation, obligationHash, outcome, userId, limits, time, ct);
    }

    private static async Task<((int Revision, Obligation Obligation)? Found, IResult? Refusal)> FindAsync(
        PromisesDbContext db, Guid projectId, Guid promiseId, string obligationKey, int? revisionNumber, CancellationToken ct)
    {
        var row = await db.Promises.AsNoTracking().FirstOrDefaultAsync(p => p.Id == promiseId && p.ProjectId == projectId, ct);
        if (row is null)
            return (null, PromiseAccess.NotFound());

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
            ? ((revision.Revision, obligation), null)
            : (null, Results.NotFound(new ErrorResponse("not_found", "Obligation not found")));
    }

    private static async Task<IResult> RecordAsync(
        IEventStore store, Guid projectId, Guid promiseId, int revision, Obligation obligation, string obligationHash, CheckOutcome outcome,
        string userId, PromiseLimits limits, TimeProvider time, CancellationToken ct)
    {
        var run = new VerificationRun(
            Guid.NewGuid(), obligation.Id, revision, obligationHash, userId, time.GetUtcNow(),
            outcome.ModelRevision, outcome.CommitSha, outcome.ScanId, outcome.Environment, outcome.Result, outcome.Scope,
            outcome.Evidence, outcome.Limitations, outcome.Detail,
            outcome.Inputs ?? new RunInputs(obligationHash), outcome.Assertions, outcome.EffectiveFaults);

        // The decision sets the draft label from the stream, so the stored run is read back from the state.
        VerificationRun? recorded = null;
        var result = await PromiseAccess.DecideAsync(store, promiseId, state =>
        {
            var decision = PromiseDecider.RecordRun(state, projectId, run, limits.MaxRunsPerObligation);
            recorded = decision is CommandResult<IReadOnlyList<object>>.Success { Value: [PromiseEvent.VerificationRunRecorded stored] } ? stored.Run : null;
            return decision;
        }, ct);
        return result.Succeeded ? Results.Ok(recorded) : result.Refusal();
    }
}
