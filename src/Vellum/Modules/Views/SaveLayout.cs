using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Vellum.Modules.Views.Entities;
using Vellum.Modules.Workspaces.Authorization;
using Vellum.Shared;

namespace Vellum.Modules.Views;

public sealed record SaveLayoutPosition(Guid ElementId, double X, double Y);
public sealed record SaveLayoutEdge(Guid RelationshipId, JsonDocument? RoutePoints);
public sealed record SaveLayoutRequest(
    IReadOnlyList<SaveLayoutPosition> Positions,
    IReadOnlyList<SaveLayoutEdge>? Edges);

public static class SaveLayout
{
    public static async Task<IResult> Handle(
        Guid projectId, Guid viewId,
        SaveLayoutRequest request,
        ClaimsPrincipal user,
        ViewsDbContext db,
        WorkspaceAuthorizationService auth,
        CancellationToken ct)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier)!;
        await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Editor, ct);

        var view = await db.Views.FindAsync([viewId], ct);
        if (view is null || view.ProjectId != projectId)
            return Results.NotFound(new ErrorResponse("not_found", "View not found"));

        // Replace all positions
        var existingPositions = await db.LayoutPositions
            .Where(p => p.ViewId == viewId).ToListAsync(ct);
        db.LayoutPositions.RemoveRange(existingPositions);

        db.LayoutPositions.AddRange(request.Positions.Select(p => new LayoutPositionEntity
        {
            Id = Guid.NewGuid(),
            ViewId = viewId,
            ElementId = p.ElementId,
            X = p.X,
            Y = p.Y
        }));

        // Replace all edges if provided
        if (request.Edges is not null)
        {
            var existingEdges = await db.LayoutEdges
                .Where(e => e.ViewId == viewId).ToListAsync(ct);
            db.LayoutEdges.RemoveRange(existingEdges);

            db.LayoutEdges.AddRange(request.Edges.Select(e => new LayoutEdgeEntity
            {
                Id = Guid.NewGuid(),
                ViewId = viewId,
                RelationshipId = e.RelationshipId,
                RoutePoints = e.RoutePoints
            }));
        }

        view.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return Results.Ok();
    }
}
