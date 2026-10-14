using System.Security.Claims;
using Deedbox;
using Microsoft.EntityFrameworkCore;
using Vellum.Modules.Identity.AgentTokens;
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
/// <param name="ContentHash">The <c>contentHash</c> of that revision as the approver read it. The approval binds exactly that content.</param>
public sealed record ApproveRequest(int Revision, string ContentHash);

/// <summary>409 for an approval of a draft that is not the one the approver read. It holds the draft as it is now.</summary>
/// <param name="Type"><c>revision_changed</c>.</param>
/// <param name="Revision">The number of the draft as it is now.</param>
/// <param name="ContentHash">The content hash of the draft as it is now.</param>
public sealed record RevisionChangedResponse(string Type, string Title, string Detail, int Revision, string ContentHash);

/// <param name="StartAt">The first number, for a project that already issued lower ones elsewhere. Default 1.</param>
public sealed record SetPrefixRequest(string Prefix, int? StartAt = null);

public static class PromiseWrites
{
    /// <summary>
    /// Sets the requirement ID prefix of a project, once. Issued IDs hold the prefix and never
    /// change, so there is no second set.
    /// </summary>
    public static async Task<IResult> SetPrefix(
        Guid projectId, HttpContext http, WorkspaceAuthorizationService auth, AppDbContext db, CancellationToken ct)
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

        // The origin comes from who calls, not from what the body says.
        if (PromiseAccess.Origin(http.User, request.Origin) is not { } origin)
            return PromiseAccess.Invalid("origin", "The origin of a promise a person creates is human or ai-suggested");

        var content = request.ToContent();
        var promiseId = Guid.NewGuid();
        var now = time.GetUtcNow();
        var world = await PromiseWorld.LoadAsync(http.RequestServices, projectId, [], ct);

        // The number and the promise commit together. The UPDATE locks the project's numbering
        // row until the commit, so creates of one project take turns and each gets its own
        // number. The lock is taken before the stream lock and nothing asks for it later.
        var tooLarge = false;
        var result = await SharedTransaction.RunAsync(http.RequestServices, async services =>
        {
            var db = services.GetRequiredService<AppDbContext>();
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
                    state =>
                    {
                        var decision = PromiseDecider.Create(
                            state, projectId, PromiseRules.RequirementId(numbering.Prefix, numbering.Number), numbering.Number,
                            origin, content, world.Known, userId, PromiseAccess.Actor(http.User), now);
                        tooLarge = decision is CommandResult<IReadOnlyList<object>>.Success success && PromiseAccess.PassesRevisionLimit(state, success.Value);
                        return tooLarge ? new CommandResult<IReadOnlyList<object>>.Conflict("The revision would pass its size limit.") : decision;
                    },
                    state => state,
                    ct);

            // A refused create rolls back, and the number goes back with it: it was never issued.
            if (created is CommandResult<PromiseState>.Success)
                await transaction.CommitAsync(ct);
            return created;
        }, ct);

        return result is CommandResult<PromiseState>.Success
            ? Results.Created($"/api/projects/{projectId}/promises/{promiseId}", await reader.GetAsync(projectId, promiseId, ct))
            : new PromiseWrite(result, tooLarge).Refusal();
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
            state => PromiseDecider.ChangeDraft(state, projectId, request.ToContent(), world.Known, userId, PromiseAccess.Actor(http.User), now), ct);
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
            state => PromiseDecider.Revise(state, projectId, request?.Retire ?? false, userId, PromiseAccess.Actor(http.User), now), ct);
        return await RespondAsync(result, reader, projectId, promiseId, ct);
    }

    /// <summary>
    /// Approval needs the Owner role and a person in an interactive session. No other call
    /// approves: not an import, not an AI call, not an agent and not a check run. The
    /// approval names the revision and its content hash, so it binds exactly the content the
    /// approver read: any other content is refused with 409 <c>revision_changed</c> and the
    /// hash of the draft as it is now. The stream is locked from the check to the append, so
    /// of an edit and an approval at the same time one is first, and the approval that comes
    /// after the edit gets the 409.
    /// </summary>
    public static async Task<IResult> Approve(
        Guid projectId, Guid promiseId, HttpContext http,
        WorkspaceAuthorizationService auth, IEventStore store, AppDbContext db, PromiseReader reader, TimeProvider time, CancellationToken ct)
    {
        var userId = PromiseAccess.UserId(http.User);
        await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Owner, ct);
        var (request, error) = await PromiseAccess.ReadAsync<ApproveRequest>(http);
        if (request is null)
            return error!;
        if (string.IsNullOrEmpty(request.ContentHash))
            return PromiseAccess.Invalid("contentHash", "The content hash of the revision is required. Read the promise and send the contentHash of the draft you approve.");

        var row = await db.Promises.AsNoTracking().FirstOrDefaultAsync(p => p.Id == promiseId && p.ProjectId == projectId, ct);
        if (row is null)
            return PromiseAccess.NotFound();
        var links = PromiseJson.Read<PromiseState>(row.Document).Latest!.Content.Links;

        // The hashes record what the approver saw. A link that changes later shows as a gap.
        var world = await PromiseWorld.LoadAsync(http.RequestServices, projectId, links.ScenarioIds, ct);
        var now = time.GetUtcNow();
        (int Revision, string ContentHash)? changed = null;
        var result = await PromiseAccess.DecideAsync(store, promiseId, state =>
        {
            changed = PromiseDecider.Changed(state, projectId, request.Revision, request.ContentHash);
            return PromiseDecider.Approve(
                state, projectId, request.Revision, request.ContentHash, world.Known, world.ModelVersion,
                world.LinkHashes(state.Draft?.Content.Links ?? PromiseLinks.None), userId, now);
        }, ct);
        if (!result.Succeeded && changed is { } current)
        {
            return Results.Conflict(new RevisionChangedResponse(
                "revision_changed", "The draft is not the one that was read",
                current.Revision == request.Revision
                    ? $"Revision {request.Revision} changed after it was read. Read the promise again and approve what you read."
                    : $"The draft is revision {current.Revision}, not {request.Revision}. Read the promise again and approve what you read.",
                current.Revision, current.ContentHash));
        }
        return await RespondAsync(result, reader, projectId, promiseId, ct);
    }

    /// <summary>
    /// Deletes a promise that was never approved. An agent token deletes only a promise that
    /// agent tokens alone wrote: one that a person wrote or changed gets 403 <c>human_only</c>.
    /// The stream is locked from the check to the append, so a change by a person that comes
    /// first is seen.
    /// </summary>
    public static async Task<IResult> Delete(
        Guid projectId, Guid promiseId, HttpContext http,
        WorkspaceAuthorizationService auth, IEventStore store, TimeProvider time, CancellationToken ct)
    {
        var userId = PromiseAccess.UserId(http.User);
        await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Editor, ct);

        var now = time.GetUtcNow();
        var (actor, agent) = (PromiseAccess.Actor(http.User), AgentPrincipal.IsAgent(http.User));
        var humanOnly = false;
        var result = await PromiseAccess.DecideAsync(store, promiseId, state =>
        {
            humanOnly = agent && state.ExistsIn(projectId) && !PromiseDecider.AgentAlone(state);
            return humanOnly
                ? new CommandResult<IReadOnlyList<object>>.Conflict("A person wrote or changed this promise.")
                : PromiseDecider.Delete(state, projectId, userId, actor, now);
        }, ct);
        if (humanOnly)
        {
            return await PromiseAccess.HumanOnlyAsync(http,
                "A person wrote, changed or approved this promise. An agent token deletes only a draft promise that agent tokens alone wrote. Ask a person to delete it in the app.");
        }
        return result.Succeeded ? Results.NoContent() : result.Refusal();
    }

    internal static async Task<IResult> RespondAsync(
        PromiseWrite result, PromiseReader reader, Guid projectId, Guid promiseId, CancellationToken ct) =>
        result.Succeeded
            ? Results.Ok(await reader.GetAsync(projectId, promiseId, ct))
            : result.Refusal();

    private sealed class AllocatedNumber
    {
        public string Prefix { get; set; } = "";
        public int Number { get; set; }
    }
}
