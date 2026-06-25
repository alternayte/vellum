using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Vellum.Modules.Workspaces.Authorization;
using Vellum.Shared;

namespace Vellum.Modules.Views;

public static class GetView
{
    public static async Task<IResult> Handle(
        Guid projectId, Guid viewId,
        ClaimsPrincipal user,
        ViewsDbContext db,
        WorkspaceAuthorizationService auth,
        CancellationToken ct)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier)!;
        await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Viewer, ct);

        var view = await db.Views.AsNoTracking()
            .FirstOrDefaultAsync(v => v.Id == viewId && v.ProjectId == projectId, ct);
        if (view is null) return Results.NotFound(new ErrorResponse("not_found", "View not found"));

        var positions = await db.LayoutPositions.AsNoTracking()
            .Where(p => p.ViewId == viewId)
            .Select(p => new LayoutPositionDto(p.ElementId, p.X, p.Y, p.Width, p.Height))
            .ToListAsync(ct);

        var rawEdges = await db.LayoutEdges.AsNoTracking()
            .Where(e => e.ViewId == viewId)
            .ToListAsync(ct);
        var edges = rawEdges.Select(e => new LayoutEdgeDto(e.RelationshipId,
            e.RoutePoints != null ? JsonSerializer.Deserialize<object>(e.RoutePoints.RootElement.GetRawText()) : null))
            .ToList();

        return Results.Ok(new ViewDetailDto(
            view.Id, view.ProjectId, view.Name, view.RootElementId,
            view.VisibleElementIds, view.ActiveLens, view.ActiveFlowId,
            view.CreatedAt, view.UpdatedAt, positions, edges));
    }
}
