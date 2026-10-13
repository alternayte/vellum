using System.Security.Claims;
using Deedbox;
using Microsoft.EntityFrameworkCore;
using Vellum.Modules.Workspaces.Authorization;
using Vellum.Shared;

namespace Vellum.Modules.Promises;

/// <param name="Origin">On create only: <c>human</c> (the default), <c>ai-suggested</c> or <c>agent-suggested</c>.</param>
public sealed record PromiseRequest(
    string Title, string Claim, string? Scope, string? Preconditions, string? ExpectedOutcome,
    string? ForbiddenOutcome, string? EvidenceCriteria,
    IReadOnlyList<ForbiddenEvent>? ForbiddenEvents, RequiredPattern? RequiresPattern, PromiseLinks? Links,
    string? Origin = null)
{
    public PromiseContent ToContent() => new(
        Title, Claim, Scope, Preconditions, ExpectedOutcome, ForbiddenOutcome, EvidenceCriteria,
        ForbiddenEvents ?? [], RequiresPattern, Links ?? PromiseLinks.None);
}

/// <param name="Retire">True for a revision that retires the promise when it is approved.</param>
public sealed record ReviseRequest(bool Retire = false);

/// <param name="Revision">The draft revision the approver read.</param>
public sealed record ApproveRequest(int Revision);

/// <param name="StartAt">The first number, for a project that already issued lower ones elsewhere. Default 1.</param>
public sealed record SetPrefixRequest(string Prefix, int? StartAt = null);

public static class PromiseWrites
{
    /// <summary>
    /// Sets the requirement ID prefix of a project, once. Issued IDs hold the prefix and never
    /// change, so there is no second set.
    /// </summary>
    public static async Task<IResult> SetPrefix(
        Guid projectId, HttpContext http, WorkspaceAuthorizationService auth, PromisesDbContext db, CancellationToken ct)
    {
        await auth.RequireProjectRoleAsync(projectId, PromiseAccess.UserId(http.User), WorkspaceRole.Owner, ct);
        var (request, error) = await PromiseAccess.ReadAsync<SetPrefixRequest>(http);
        if (request is null)
            return error!;
        if (!PromiseRules.IsPrefix(request.Prefix))
            return PromiseAccess.Invalid("prefix", "A prefix is 2 to 6 upper-case letters, for example ORD");
        if (request.StartAt is < 1 or > 100_000)
            return PromiseAccess.Invalid("startAt", "The first number is between 1 and 100000");

        var startAt = request.StartAt ?? 1;
        var inserted = await db.Database.ExecuteSqlAsync(
            $"INSERT INTO promises.numbering (project_id, prefix, next_number) VALUES ({projectId}, {request.Prefix}, {startAt}) ON CONFLICT (project_id) DO NOTHING",
            ct);
        return inserted == 1
            ? Results.Ok(new PrefixDto(request.Prefix, startAt))
            : Results.Conflict(new ErrorResponse("conflict", "The prefix of this project is already set and cannot be changed"));
    }

    public static async Task<IResult> Create(
        Guid projectId, HttpContext http, WorkspaceAuthorizationService auth, PromiseReader reader, TimeProvider time, CancellationToken ct)
    {
        var userId = PromiseAccess.UserId(http.User);
        await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Editor, ct);
        var (request, error) = await PromiseAccess.ReadAsync<PromiseRequest>(http);
        if (request is null)
            return error!;

        // A caller that is not a person at a screen cannot say that a person wrote the promise.
        var origin = request.Origin ?? PromiseOrigin.Human;
        if (origin == PromiseOrigin.Human && !PromiseAccess.IsInteractive(http.User))
            return PromiseAccess.Invalid("origin", "Only a signed-in person creates a promise with the origin human");

        var content = request.ToContent();
        var promiseId = Guid.NewGuid();
        var now = time.GetUtcNow();
        var world = await PromiseWorld.LoadAsync(http.RequestServices, projectId, [], ct);

        // The number and the promise commit together. The UPDATE locks the project's numbering
        // row until the commit, so creates of one project take turns and each gets its own
        // number. The lock is taken before the stream lock and nothing asks for it later.
        var result = await SharedTransaction.RunAsync(http.RequestServices, async services =>
        {
            var db = services.GetRequiredService<PromisesDbContext>();
            await using var transaction = await db.Database.BeginTransactionAsync(ct);

            var allocated = await db.Database
                .SqlQuery<AllocatedNumber>($"UPDATE promises.numbering SET next_number = next_number + 1 WHERE project_id = {projectId} RETURNING prefix, next_number - 1 AS number")
                .ToListAsync(ct);
            if (allocated is not [var numbering])
            {
                return new CommandResult<PromiseState>.Conflict(
                    "The project has no requirement ID prefix yet. An owner sets it once with PUT promises/prefix.");
            }
            if (await db.Promises.CountAsync(p => p.ProjectId == projectId, ct) >= PromiseRules.MaxPerProject)
                return new CommandResult<PromiseState>.Conflict($"A project holds at most {PromiseRules.MaxPerProject} promises.");

            var created = await services.GetRequiredService<IEventStore>().UseDbContext(db)
                .DecideInTransaction<PromiseState, PromiseState>(
                    promiseId,
                    state => PromiseDecider.Create(
                        state, projectId, PromiseRules.RequirementId(numbering.Prefix, numbering.Number), numbering.Number,
                        origin, content, world.Known, userId, now),
                    state => state,
                    ct);

            // A refused create rolls back, and the number goes back with it: it was never issued.
            if (created is CommandResult<PromiseState>.Success)
                await transaction.CommitAsync(ct);
            return created;
        }, ct);

        return result is CommandResult<PromiseState>.Success
            ? Results.Created($"/api/projects/{projectId}/promises/{promiseId}", await reader.GetAsync(projectId, promiseId, ct))
            : result.ToHttpResult();
    }

    public static async Task<IResult> Update(
        Guid projectId, Guid promiseId, HttpContext http,
        WorkspaceAuthorizationService auth, IEventStore store, PromiseReader reader, TimeProvider time, CancellationToken ct)
    {
        var userId = PromiseAccess.UserId(http.User);
        await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Editor, ct);
        var (request, error) = await PromiseAccess.ReadAsync<PromiseRequest>(http);
        if (request is null)
            return error!;

        var world = await PromiseWorld.LoadAsync(http.RequestServices, projectId, [], ct);
        var now = time.GetUtcNow();
        var result = await PromiseAccess.DecideAsync(store, promiseId,
            state => PromiseDecider.ChangeDraft(state, projectId, request.ToContent(), world.Known, userId, now), ct);
        return await RespondAsync(result, reader, projectId, promiseId, ct);
    }

    public static async Task<IResult> Revise(
        Guid projectId, Guid promiseId, HttpContext http,
        WorkspaceAuthorizationService auth, IEventStore store, PromiseReader reader, TimeProvider time, CancellationToken ct)
    {
        var userId = PromiseAccess.UserId(http.User);
        await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Editor, ct);
        var (request, error) = await PromiseAccess.ReadAsync<ReviseRequest>(http, allowEmpty: true);
        if (error is not null)
            return error;

        var now = time.GetUtcNow();
        var result = await PromiseAccess.DecideAsync(store, promiseId,
            state => PromiseDecider.Revise(state, projectId, request?.Retire ?? false, userId, now), ct);
        return await RespondAsync(result, reader, projectId, promiseId, ct);
    }

    /// <summary>
    /// Approval needs the Owner role and a person in an interactive session. No other call
    /// approves: not an import, not an AI call, not an agent and not a check run.
    /// </summary>
    public static async Task<IResult> Approve(
        Guid projectId, Guid promiseId, HttpContext http,
        WorkspaceAuthorizationService auth, IEventStore store, PromisesDbContext db, PromiseReader reader, TimeProvider time, CancellationToken ct)
    {
        var userId = PromiseAccess.UserId(http.User);
        if (!PromiseAccess.IsInteractive(http.User))
            return PromiseAccess.InteractiveSessionRequired();
        await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Owner, ct);
        var (request, error) = await PromiseAccess.ReadAsync<ApproveRequest>(http);
        if (request is null)
            return error!;

        var row = await db.Promises.AsNoTracking().FirstOrDefaultAsync(p => p.Id == promiseId && p.ProjectId == projectId, ct);
        if (row is null)
            return PromiseAccess.NotFound();
        var links = PromiseJson.Read<PromiseState>(row.Document).Latest!.Content.Links;

        // The hashes record what the approver saw. A link that changes later shows as a gap.
        var world = await PromiseWorld.LoadAsync(http.RequestServices, projectId, links.ScenarioIds, ct);
        var now = time.GetUtcNow();
        var result = await PromiseAccess.DecideAsync(store, promiseId,
            state => PromiseDecider.Approve(
                state, projectId, request.Revision, world.Known, world.ModelVersion,
                world.LinkHashes(state.Draft?.Content.Links ?? PromiseLinks.None), userId, now), ct);
        return await RespondAsync(result, reader, projectId, promiseId, ct);
    }

    public static async Task<IResult> Delete(
        Guid projectId, Guid promiseId, ClaimsPrincipal user,
        WorkspaceAuthorizationService auth, IEventStore store, TimeProvider time, CancellationToken ct)
    {
        var userId = PromiseAccess.UserId(user);
        await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Editor, ct);

        var now = time.GetUtcNow();
        var result = await PromiseAccess.DecideAsync(store, promiseId, state => PromiseDecider.Delete(state, projectId, userId, now), ct);
        return result is CommandResult<PromiseState>.Success ? Results.NoContent() : result.ToHttpResult();
    }

    internal static async Task<IResult> RespondAsync(
        CommandResult<PromiseState> result, PromiseReader reader, Guid projectId, Guid promiseId, CancellationToken ct) =>
        result is CommandResult<PromiseState>.Success
            ? Results.Ok(await reader.GetAsync(projectId, promiseId, ct))
            : result.ToHttpResult();

    private sealed class AllocatedNumber
    {
        public string Prefix { get; set; } = "";
        public int Number { get; set; }
    }
}
