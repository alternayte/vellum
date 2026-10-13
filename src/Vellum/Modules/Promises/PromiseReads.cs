using System.Globalization;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Vellum.Modules.Workspaces.Authorization;
using Vellum.Shared;

namespace Vellum.Modules.Promises;

/// <summary>
/// The promises of a project as other modules and the HTTP reads see them: each with its
/// revisions and the evidence status computed against the project as it is now.
/// </summary>
public sealed class PromiseReader(IServiceProvider services, PromisesDbContext db)
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

public static class PromiseReads
{
    private const int MaxObligationFilterChars = 100;

    public static async Task<IResult> List(
        Guid projectId, string? cursor, int? limit,
        ClaimsPrincipal user, WorkspaceAuthorizationService auth, PromisesDbContext db, IServiceProvider services, CancellationToken ct)
    {
        await auth.RequireProjectRoleAsync(projectId, PromiseAccess.UserId(user), WorkspaceRole.Viewer, ct);

        var pageSize = Math.Clamp(limit ?? 50, 1, 200);
        var query = db.Promises.AsNoTracking().Where(p => p.ProjectId == projectId);
        if (CursorEncoder.Decode(cursor) is { } decoded
            && int.TryParse(decoded.SortKey, NumberStyles.None, CultureInfo.InvariantCulture, out var afterNumber))
            query = query.Where(p => p.Number > afterNumber);

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
        await auth.RequireProjectRoleAsync(projectId, PromiseAccess.UserId(user), WorkspaceRole.Viewer, ct);
        return await reader.GetAsync(projectId, promiseId, ct) is { } promise ? Results.Ok(promise) : PromiseAccess.NotFound();
    }

    /// <summary>The runs of a promise, newest first. Runs are appended and never changed.</summary>
    public static async Task<IResult> Runs(
        Guid projectId, Guid promiseId, string? cursor, int? limit, string? obligation,
        ClaimsPrincipal user, WorkspaceAuthorizationService auth, PromisesDbContext db, CancellationToken ct)
    {
        await auth.RequireProjectRoleAsync(projectId, PromiseAccess.UserId(user), WorkspaceRole.Viewer, ct);
        if (!await db.Promises.AsNoTracking().AnyAsync(p => p.Id == promiseId && p.ProjectId == projectId, ct))
            return PromiseAccess.NotFound();

        if (obligation is { Length: > MaxObligationFilterChars })
            return PromiseAccess.Invalid("obligation", $"An obligation is named by its ID or its key, at most {MaxObligationFilterChars} characters");

        var pageSize = Math.Clamp(limit ?? 50, 1, 200);
        var query = db.Runs.AsNoTracking().Where(r => r.PromiseId == promiseId);
        if (!string.IsNullOrEmpty(obligation))
            query = query.Where(r => r.ObligationId == obligation || r.ObligationId.EndsWith("/" + obligation));
        if (CursorEncoder.Decode(cursor) is { } decoded
            && long.TryParse(decoded.SortKey, NumberStyles.None, CultureInfo.InvariantCulture, out var beforeVersion))
            query = query.Where(r => r.Version < beforeVersion);

        var rows = await query.OrderByDescending(r => r.Version).Take(pageSize + 1)
            .Select(r => new { r.Id, r.Version, r.Document })
            .ToListAsync(ct);
        string? next = null;
        if (rows.Count > pageSize)
        {
            rows = rows[..pageSize];
            next = CursorEncoder.Encode(rows[^1].Version.ToString(CultureInfo.InvariantCulture), rows[^1].Id);
        }

        return Results.Ok(new Page<VerificationRun>(rows.Select(r => PromiseJson.Read<VerificationRun>(r.Document)).ToList(), next));
    }

    public static async Task<IResult> Prefix(
        Guid projectId, ClaimsPrincipal user, WorkspaceAuthorizationService auth, PromisesDbContext db, CancellationToken ct)
    {
        await auth.RequireProjectRoleAsync(projectId, PromiseAccess.UserId(user), WorkspaceRole.Viewer, ct);
        var numbering = await db.Numbering.AsNoTracking().FirstOrDefaultAsync(n => n.ProjectId == projectId, ct);
        return Results.Ok(new PrefixDto(numbering?.Prefix, numbering?.NextNumber));
    }
}
