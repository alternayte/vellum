using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Vellum.Modules.Views.Entities;
using Vellum.Modules.Workspaces.Authorization;
using Vellum.Shared;

namespace Vellum.Modules.Views;

public sealed record CreateViewRequest(Guid Id, string Name, Guid? RootElementId);

public sealed record UpdateViewRequest(
    string? Name, Guid? RootElementId, Guid[]? VisibleElementIds,
    string? ActiveLens, DateTimeOffset? UpdatedAt,
    bool SetRootElementId = false, bool SetActiveLens = false);

public sealed record SaveLayoutPosition(Guid ElementId, double X, double Y, double? Width = null, double? Height = null);
public sealed record SaveLayoutEdge(Guid RelationshipId, JsonDocument? RoutePoints);
public sealed record SaveLayoutRequest(
    IReadOnlyList<SaveLayoutPosition> Positions,
    IReadOnlyList<SaveLayoutEdge>? Edges);

public sealed record ViewDto(
    Guid Id, Guid ProjectId, string Name, Guid? RootElementId,
    Guid[] VisibleElementIds, string? ActiveLens, Guid? ActiveFlowId,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public sealed record LayoutPositionDto(Guid ElementId, double X, double Y, double? Width = null, double? Height = null);
public sealed record LayoutEdgeDto(Guid RelationshipId, object? RoutePoints);

public sealed record ViewDetailDto(
    Guid Id, Guid ProjectId, string Name, Guid? RootElementId,
    Guid[] VisibleElementIds, string? ActiveLens, Guid? ActiveFlowId,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    IReadOnlyList<LayoutPositionDto> Positions,
    IReadOnlyList<LayoutEdgeDto> Edges);

/// <summary>
/// The saved views of a project and their canvas layouts. A layout is presentation data: the
/// positions of elements and the routes of edges, never architecture.
/// </summary>
public static class ViewEndpoints
{
    public static WebApplication MapViewEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/projects/{projectId}/views")
            .RequireAuthorization()
            .WithTags("Views");

        group.MapPost("/", Create);
        group.MapGet("/", List);
        group.MapGet("/{viewId}", Get);
        group.MapPatch("/{viewId}", Update);
        group.MapDelete("/{viewId}", Delete);
        // A layout holds one position for each element on the canvas.
        group.MapPut("/{viewId}/layout", SaveLayout).WithBodyLimit(RequestBodyLimit.Document);

        return app;
    }

    private static async Task<IResult> Create(
        Guid projectId,
        CreateViewRequest request,
        ClaimsPrincipal user,
        AppDbContext db,
        WorkspaceAuthorizationService auth,
        CancellationToken ct)
    {
        await auth.RequireProjectRoleAsync(projectId, user.UserId(), WorkspaceRole.Editor, ct);

        if (ResultExtensions.Refusal(ResultExtensions.Required("name", request.Name), TextRule.Line("name", request.Name)) is { } refusal)
            return refusal;

        // A retry in the same project gets the view back. A view of another project is not
        // shown: the caller learns only that the ID is taken.
        var existing = await db.Views.FindAsync([request.Id], ct);
        if (existing is not null)
            return existing.ProjectId == projectId ? Results.Ok(ToDto(existing)) : IdInUse.Conflict();

        var view = new ViewEntity
        {
            Id = request.Id,
            ProjectId = projectId,
            Name = request.Name,
            RootElementId = request.RootElementId
        };
        db.Views.Add(view);
        await db.SaveChangesAsync(ct);

        return Results.Created($"/api/projects/{projectId}/views/{view.Id}", ToDto(view));
    }

    private static async Task<IResult> List(
        Guid projectId,
        ClaimsPrincipal user,
        AppDbContext db,
        WorkspaceAuthorizationService auth,
        CancellationToken ct)
    {
        await auth.RequireProjectRoleAsync(projectId, user.UserId(), WorkspaceRole.Viewer, ct);

        var views = await db.Views.AsNoTracking()
            .Where(v => v.ProjectId == projectId)
            .OrderBy(v => v.Name)
            .Select(v => new ViewDto(v.Id, v.ProjectId, v.Name, v.RootElementId,
                v.VisibleElementIds, v.ActiveLens, v.ActiveFlowId, v.CreatedAt, v.UpdatedAt))
            .ToListAsync(ct);

        return Results.Ok(views);
    }

    private static async Task<IResult> Get(
        Guid projectId, Guid viewId,
        ClaimsPrincipal user,
        AppDbContext db,
        WorkspaceAuthorizationService auth,
        CancellationToken ct)
    {
        await auth.RequireProjectRoleAsync(projectId, user.UserId(), WorkspaceRole.Viewer, ct);

        var view = await db.Views.AsNoTracking()
            .FirstOrDefaultAsync(v => v.Id == viewId && v.ProjectId == projectId, ct);
        if (view is null) return NotFound();

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

    private static async Task<IResult> Update(
        Guid projectId, Guid viewId,
        UpdateViewRequest request,
        ClaimsPrincipal user,
        AppDbContext db,
        WorkspaceAuthorizationService auth,
        TimeProvider time,
        CancellationToken ct)
    {
        await auth.RequireProjectRoleAsync(projectId, user.UserId(), WorkspaceRole.Editor, ct);

        if (ResultExtensions.Refusal(
                request.Name is null ? null : ResultExtensions.Required("name", request.Name),
                TextRule.Line("name", request.Name),
                TextRule.Line("activeLens", request.ActiveLens, TextRule.MaxIdentifier)) is { } refusal)
            return refusal;

        var view = await db.Views.FindAsync([viewId], ct);
        if (view is null || view.ProjectId != projectId)
            return NotFound();

        // Optimistic concurrency: reject if client's updated_at doesn't match
        if (request.UpdatedAt.HasValue && request.UpdatedAt.Value != view.UpdatedAt)
            return Results.Conflict(new ErrorResponse("conflict", "View was modified by another user"));

        if (request.Name is not null) view.Name = request.Name;
        if (request.SetRootElementId || request.RootElementId.HasValue) view.RootElementId = request.RootElementId;
        if (request.VisibleElementIds is not null) view.VisibleElementIds = request.VisibleElementIds;
        if (request.SetActiveLens || request.ActiveLens is not null) view.ActiveLens = request.ActiveLens;
        view.UpdatedAt = time.GetUtcNow();

        await db.SaveChangesAsync(ct);
        return Results.Ok(ToDto(view));
    }

    private static async Task<IResult> Delete(
        Guid projectId, Guid viewId,
        ClaimsPrincipal user,
        AppDbContext db,
        WorkspaceAuthorizationService auth,
        CancellationToken ct)
    {
        await auth.RequireProjectRoleAsync(projectId, user.UserId(), WorkspaceRole.Editor, ct);

        var view = await db.Views.FindAsync([viewId], ct);
        if (view is null || view.ProjectId != projectId)
            return NotFound();

        db.Views.Remove(view);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    /// <summary>Replaces the positions, and the edge routes when the request has them.</summary>
    private static async Task<IResult> SaveLayout(
        Guid projectId, Guid viewId,
        SaveLayoutRequest request,
        ClaimsPrincipal user,
        AppDbContext db,
        WorkspaceAuthorizationService auth,
        TimeProvider time,
        CancellationToken ct)
    {
        await auth.RequireProjectRoleAsync(projectId, user.UserId(), WorkspaceRole.Editor, ct);

        var view = await db.Views.FindAsync([viewId], ct);
        if (view is null || view.ProjectId != projectId)
            return NotFound();

        var existingPositions = await db.LayoutPositions
            .Where(p => p.ViewId == viewId).ToListAsync(ct);
        db.LayoutPositions.RemoveRange(existingPositions);

        db.LayoutPositions.AddRange(request.Positions.Select(p => new LayoutPositionEntity
        {
            Id = Guid.NewGuid(),
            ViewId = viewId,
            ElementId = p.ElementId,
            X = p.X,
            Y = p.Y,
            Width = p.Width,
            Height = p.Height
        }));

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

        view.UpdatedAt = time.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return Results.Ok();
    }

    private static IResult NotFound() => Results.NotFound(new ErrorResponse("not_found", "View not found"));

    private static ViewDto ToDto(ViewEntity v) =>
        new(v.Id, v.ProjectId, v.Name, v.RootElementId, v.VisibleElementIds,
            v.ActiveLens, v.ActiveFlowId, v.CreatedAt, v.UpdatedAt);
}
