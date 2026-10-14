using System.Security.Claims;
using Deedbox;
using Microsoft.EntityFrameworkCore;
using Vellum.Modules.Modelling.Model;
using Vellum.Modules.Trace.Engine;
using Vellum.Modules.Workspaces.Authorization;
using Vellum.Shared;

namespace Vellum.Modules.Trace.Scenarios;

public sealed record ScenarioSummaryDto(Guid Id, string Name);

/// <summary>
/// A scenario document as a read gives it: the document's own members and where it came from.
/// </summary>
/// <param name="Origin">Null for a scenario that only a person wrote. Else the accepted AI flow proposal.</param>
public sealed record ScenarioDto(
    ScenarioId Id, string Name, IReadOnlyList<Step> Steps, IReadOnlyList<Fault> Faults, IReadOnlyList<Assertion> Assertions,
    ScenarioOrigin? Origin)
{
    public static ScenarioDto From(Scenario document, ScenarioOrigin? origin) =>
        new(document.Id, document.Name, document.Steps, document.Faults, document.Assertions, origin);
}

public static class ReadScenarios
{
    public static async Task<IResult> List(
        Guid projectId,
        string? cursor, int? limit,
        ClaimsPrincipal user,
        WorkspaceAuthorizationService auth,
        AppDbContext db,
        CancellationToken ct)
    {
        var userId = user.UserId();
        await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Viewer, ct);

        var pageSize = Math.Clamp(limit ?? 50, 1, 200);
        var query = db.Scenarios.AsNoTracking().Where(s => s.ProjectId == projectId);

        if (CursorEncoder.Decode(cursor) is { } decoded)
        {
            var (sortKey, afterId) = decoded;
            query = query.Where(s => string.Compare(s.Name, sortKey) > 0
                || (s.Name == sortKey && s.Id.CompareTo(afterId) > 0));
        }

        var items = await query
            .OrderBy(s => s.Name).ThenBy(s => s.Id)
            .Take(pageSize + 1)
            .Select(s => new ScenarioSummaryDto(s.Id, s.Name))
            .ToListAsync(ct);

        string? nextCursor = null;
        if (items.Count > pageSize)
        {
            items = items[..pageSize];
            nextCursor = CursorEncoder.Encode(items[^1].Name, items[^1].Id);
        }

        return Results.Ok(new Page<ScenarioSummaryDto>(items, nextCursor));
    }

    /// <summary>A scenario of another project reads as not found.</summary>
    public static async Task<IResult> Get(
        Guid projectId, Guid scenarioId,
        ClaimsPrincipal user,
        WorkspaceAuthorizationService auth,
        IEventStore store,
        CancellationToken ct)
    {
        var userId = user.UserId();
        await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Viewer, ct);

        var state = await ScenarioStreams.LoadAsync(store, scenarioId, ct);
        return state.DocumentIn(projectId) is { } document
            ? Results.Ok(ScenarioDto.From(document, state.Origin))
            : Results.NotFound(new ErrorResponse("not_found", "Scenario not found"));
    }
}
