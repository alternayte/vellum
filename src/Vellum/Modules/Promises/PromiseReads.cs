using System.Globalization;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Vellum.Modules.Identity.AgentTokens;
using Vellum.Modules.Workspaces;
using Vellum.Shared;
using static Vellum.Shared.ResultExtensions;

namespace Vellum.Modules.Promises;

/// <summary>
/// The promises of a project as other modules and the HTTP reads see them: each with its
/// revisions and the evidence status computed against the project as it is now.
/// </summary>
public sealed class PromiseReader(IServiceProvider services, AppDbContext db)
{
    /// <summary>One promise, or null when the project has none with this ID.</summary>
    public async Task<PromiseDto?> GetAsync(Guid projectId, Guid promiseId, CancellationToken ct)
    {
        var row = await db.Promises.AsNoTracking().FirstOrDefaultAsync(p => p.Id == promiseId && p.ProjectId == projectId, ct);
        if (row is null)
            return null;

        var state = PromiseJson.Read<PromiseState>(row.Document);
        var world = await PromiseWorld.LoadAsync(services, projectId, PromiseWorld.ScenarioIds([state]), ct);
        return PromiseViews.ToDto(row.Id, state, Runs(row), world);
    }

    /// <summary>
    /// Every promise of the project in requirement-number order. A project holds at most
    /// <see cref="PromiseRules.MaxPerProject"/>. With <paramref name="pinned"/>, links and the
    /// age of evidence are read against that model and not against the main model as it is now,
    /// so an export of a pinned state does not change when the model moves on.
    /// </summary>
    public async Task<IReadOnlyList<PromiseDto>> AllAsync(Guid projectId, CancellationToken ct, PinnedModel? pinned = null)
    {
        var rows = await db.Promises.AsNoTracking()
            .Where(p => p.ProjectId == projectId)
            .OrderBy(p => p.Number).ThenBy(p => p.Id)
            .ToListAsync(ct);
        var states = rows.Select(r => PromiseJson.Read<PromiseState>(r.Document)).ToList();
        var world = await PromiseWorld.LoadAsync(services, projectId, PromiseWorld.ScenarioIds(states), ct, pinned);
        return rows.Select((row, i) => PromiseViews.ToDto(row.Id, states[i], Runs(row), world)).ToList();
    }

    internal static Dictionary<string, VerificationRun> Runs(PromiseEntity row) =>
        PromiseJson.Read<Dictionary<string, VerificationRun>>(row.LatestRuns);
}

/// <summary>Who or what started runs.</summary>
/// <param name="Kind">
/// <c>person</c>; <c>ci-token</c> for an agent token with the scope <c>scan-pull-request</c>,
/// which a CI job uses; <c>agent-token</c> for any other agent token.
/// </param>
/// <param name="Name">The display name or the email address of the person, or the name of the token. Null when the account or the token no longer exists.</param>
public sealed record RunStarter(string Kind, string? Name)
{
    public const string Person = "person";
    public const string AgentToken = "agent-token";
    public const string CiToken = "ci-token";
}

/// <summary>A page of runs, newest first.</summary>
/// <param name="StartedBy">For each actor of the page (<c>user:&lt;id&gt;</c> or <c>agent-token:&lt;id&gt;</c>), who or what it is. A run recorded before the actor was kept is under <c>user:&lt;startedBy&gt;</c>.</param>
public sealed record RunsPage(IReadOnlyList<VerificationRun> Items, string? Cursor, IReadOnlyDictionary<string, RunStarter> StartedBy);

public static class PromiseReads
{
    private const int MaxObligationFilterChars = 100;

    public static async Task<IResult> List(
        Guid projectId, string? cursor, int? limit,
        ClaimsPrincipal user, WorkspaceAuthorizationService auth, AppDbContext db, IServiceProvider services, CancellationToken ct)
    {
        await auth.RequireProjectRoleAsync(projectId, user.UserId(), WorkspaceRole.Viewer, ct);

        var pageSize = Math.Clamp(limit ?? 50, 1, 200);
        var query = db.Promises.AsNoTracking().Where(p => p.ProjectId == projectId);
        if (CursorEncoder.DecodeNumber(cursor) is { } after)
            query = query.Where(p => p.Number > after.Number);

        var rows = await query.OrderBy(p => p.Number).Take(pageSize + 1).ToListAsync(ct);
        string? next = null;
        if (rows.Count > pageSize)
        {
            rows = rows[..pageSize];
            next = CursorEncoder.Encode(rows[^1].Number.ToString(CultureInfo.InvariantCulture), rows[^1].Id);
        }

        // A summary holds the same counts as the promise itself, and whether it has a gap. So
        // it is computed from the same inputs: the scenarios the promises of the page name.
        var states = rows.Select(row => PromiseJson.Read<PromiseState>(row.Document)).ToList();
        var world = await PromiseWorld.LoadAsync(services, projectId, PromiseWorld.ScenarioIds(states), ct);
        var items = rows
            .Select((row, i) => PromiseViews.ToSummary(row.Id, states[i], PromiseReader.Runs(row), world))
            .ToList();
        return Results.Ok(new Page<PromiseSummaryDto>(items, next));
    }

    public static async Task<IResult> Get(
        Guid projectId, Guid promiseId,
        ClaimsPrincipal user, WorkspaceAuthorizationService auth, PromiseReader reader, CancellationToken ct)
    {
        await auth.RequireProjectRoleAsync(projectId, user.UserId(), WorkspaceRole.Viewer, ct);
        return await reader.GetAsync(projectId, promiseId, ct) is { } promise ? Results.Ok(promise) : NotFound("Promise");
    }

    /// <summary>The runs of a promise, newest first. Runs are appended and never changed.</summary>
    public static async Task<IResult> Runs(
        Guid projectId, Guid promiseId, string? cursor, int? limit, string? obligation,
        ClaimsPrincipal user, WorkspaceAuthorizationService auth, AppDbContext db, CancellationToken ct)
    {
        await auth.RequireProjectRoleAsync(projectId, user.UserId(), WorkspaceRole.Viewer, ct);
        if (!await db.Promises.AsNoTracking().AnyAsync(p => p.Id == promiseId && p.ProjectId == projectId, ct))
            return NotFound("Promise");

        if (obligation is { Length: > MaxObligationFilterChars })
            return Invalid("obligation", $"An obligation is named by its ID or its key, at most {MaxObligationFilterChars} characters");

        var pageSize = Math.Clamp(limit ?? 50, 1, 200);
        var query = db.PromiseRuns.AsNoTracking().Where(r => r.PromiseId == promiseId);
        if (!string.IsNullOrEmpty(obligation))
            query = query.Where(r => r.ObligationId == obligation || r.ObligationId.EndsWith("/" + obligation));
        if (CursorEncoder.DecodeNumber(cursor) is { } before)
            query = query.Where(r => r.Version < before.Number);

        var rows = await query.OrderByDescending(r => r.Version).Take(pageSize + 1)
            .Select(r => new { r.Id, r.Version, r.Document })
            .ToListAsync(ct);
        string? next = null;
        if (rows.Count > pageSize)
        {
            rows = rows[..pageSize];
            next = CursorEncoder.Encode(rows[^1].Version.ToString(CultureInfo.InvariantCulture), rows[^1].Id);
        }

        var runs = rows.Select(r => PromiseJson.Read<VerificationRun>(r.Document)).ToList();
        return Results.Ok(new RunsPage(runs, next, await StartersAsync(db, runs, ct)));
    }

    /// <summary>The actor of a run: the one it names, or the user of the call for a run recorded before the actor was kept.</summary>
    public static string ActorOf(VerificationRun run) => run.Actor ?? $"user:{run.StartedBy}";

    /// <summary>Who or what each actor of the runs is, by <see cref="ActorOf"/>.</summary>
    private static async Task<Dictionary<string, RunStarter>> StartersAsync(AppDbContext db, IReadOnlyList<VerificationRun> runs, CancellationToken ct)
    {
        var actors = runs.Select(ActorOf).Distinct(StringComparer.Ordinal).ToList();
        var userIds = actors.Where(a => a.StartsWith(UserActorPrefix, StringComparison.Ordinal)).Select(a => a[UserActorPrefix.Length..]).ToList();
        var tokenIds = actors
            .Where(AgentPrincipal.IsAgentActor)
            .Select(a => Guid.TryParse(a[AgentPrincipal.AgentActorPrefix.Length..], out var id) ? id : Guid.Empty)
            .Where(id => id != Guid.Empty)
            .ToList();

        var users = await db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id))
            .Select(u => new { u.Id, u.DisplayName, u.Email }).ToListAsync(ct);
        var tokens = await db.AgentTokens.AsNoTracking().Where(t => tokenIds.Contains(t.Id))
            .Select(t => new { t.Id, t.Name, t.Scopes }).ToListAsync(ct);

        var starters = new Dictionary<string, RunStarter>(StringComparer.Ordinal);
        foreach (var actor in actors)
        {
            if (AgentPrincipal.IsAgentActor(actor))
            {
                var token = tokens.FirstOrDefault(t => AgentPrincipal.AgentActorPrefix + t.Id == actor);
                var ci = token?.Scopes.Contains(AgentScope.ScanPullRequest) == true;
                starters[actor] = new RunStarter(ci ? RunStarter.CiToken : RunStarter.AgentToken, token?.Name);
            }
            else
            {
                var user = users.FirstOrDefault(u => UserActorPrefix + u.Id == actor);
                starters[actor] = new RunStarter(RunStarter.Person, user is null ? null : string.IsNullOrWhiteSpace(user.DisplayName) ? user.Email : user.DisplayName);
            }
        }
        return starters;
    }

    private const string UserActorPrefix = "user:";

    public static async Task<IResult> Prefix(
        Guid projectId, ClaimsPrincipal user, WorkspaceAuthorizationService auth, AppDbContext db, CancellationToken ct)
    {
        await auth.RequireProjectRoleAsync(projectId, user.UserId(), WorkspaceRole.Viewer, ct);
        var numbering = await db.Numbering.AsNoTracking().FirstOrDefaultAsync(n => n.ProjectId == projectId, ct);
        return Results.Ok(new PrefixDto(numbering?.Prefix, numbering?.NextNumber));
    }
}
