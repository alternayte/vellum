using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Vellum.Modules.Workspaces;
using Vellum.Shared;
using static Vellum.Shared.ResultExtensions;

namespace Vellum.Modules.Views;

public sealed record CreateViewRequest(Guid Id, string Name, Guid? RootElementId);

public sealed record UpdateViewRequest(
    string? Name, Guid? RootElementId, Guid[]? VisibleElementIds,
    string? ActiveLens, DateTimeOffset? UpdatedAt,
    bool SetRootElementId = false, bool SetActiveLens = false, Guid[]? HiddenElementIds = null);

public sealed record SaveLayoutPosition(Guid ElementId, double X, double Y, double? Width = null, double? Height = null);
/// <param name="RoutePoints">A route from the auto layout (<see cref="LayoutRoute"/>), or null.</param>
public sealed record SaveLayoutEdge(Guid RelationshipId, JsonDocument? RoutePoints, string? SourceSide = null, string? TargetSide = null);

/// <summary>The route of the derived line from one card to another in a view (<see cref="LayoutRoute"/>).</summary>
public sealed record SaveLayoutLine(Guid FromId, Guid ToId, JsonDocument Route);

/// <summary>The sides of one relationship in a view: top, right, bottom or left, or null for auto.</summary>
public sealed record SaveEdgeSidesRequest(string? SourceSide, string? TargetSide);
/// <param name="Edges">The rows of the relationships: routes and sides. Null keeps the saved ones.</param>
/// <param name="Lines">The routes of the derived lines. Null keeps the saved ones.</param>
public sealed record SaveLayoutRequest(
    IReadOnlyList<SaveLayoutPosition> Positions,
    IReadOnlyList<SaveLayoutEdge>? Edges,
    IReadOnlyList<SaveLayoutLine>? Lines = null);

public sealed record ViewDto(
    Guid Id, Guid ProjectId, string Name, Guid? RootElementId,
    Guid[] VisibleElementIds, string? ActiveLens, Guid? ActiveFlowId,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, Guid[]? HiddenElementIds = null);

public sealed record LayoutPositionDto(Guid ElementId, double X, double Y, double? Width = null, double? Height = null);
public sealed record LayoutEdgeDto(Guid RelationshipId, object? RoutePoints, string? SourceSide = null, string? TargetSide = null);
public sealed record LayoutLineDto(Guid FromId, Guid ToId, object Route);

/// <summary>A derived line hidden in a view, by its two cards.</summary>
public sealed record HiddenDerivedLineDto(Guid FromId, Guid ToId);

/// <summary>
/// Which lines a view draws (<see cref="LineVisibility"/>). Each switch is true when its lines
/// are drawn. The hidden relationships and derived lines are hidden one at a time.
/// </summary>
public sealed record LineVisibilityDto(
    bool DerivedLines, bool Structural, bool Behavioural, bool CrossLevel,
    IReadOnlyList<Guid> HiddenRelationshipIds, IReadOnlyList<HiddenDerivedLineDto> HiddenDerivedLines)
{
    public static LineVisibilityDto Of(LineVisibility v) => new(
        v.DerivedLines, v.Structural, v.Behavioural, v.CrossLevel,
        [.. v.HiddenRelationshipIds], [.. v.HiddenDerivedLines.Select(l => new HiddenDerivedLineDto(l.FromId, l.ToId))]);
}

public sealed record ViewDetailDto(
    Guid Id, Guid ProjectId, string Name, Guid? RootElementId,
    Guid[] VisibleElementIds, string? ActiveLens, Guid? ActiveFlowId,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    IReadOnlyList<LayoutPositionDto> Positions,
    IReadOnlyList<LayoutEdgeDto> Edges,
    IReadOnlyList<LayoutLineDto> Lines,
    LineVisibilityDto LineVisibility,
    Guid[]? HiddenElementIds = null);

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
        // The sides of one relationship in this view. The relationship's ends in the model stay.
        group.MapPut("/{viewId}/layout/edges/{relationshipId}", SaveEdgeSides);
        // Which lines the view draws. The model keeps every relationship.
        group.MapPut("/{viewId}/layout/visibility", SaveLineVisibility);

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
                v.VisibleElementIds, v.ActiveLens, v.ActiveFlowId, v.CreatedAt, v.UpdatedAt, v.HiddenElementIds))
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
        if (view is null) return NotFound("View");

        var positions = await db.LayoutPositions.AsNoTracking()
            .Where(p => p.ViewId == viewId)
            .Select(p => new LayoutPositionDto(p.ElementId, p.X, p.Y, p.Width, p.Height))
            .ToListAsync(ct);

        var rawEdges = await db.LayoutEdges.AsNoTracking()
            .Where(e => e.ViewId == viewId)
            .ToListAsync(ct);
        var edges = rawEdges.Select(ToDto).ToList();
        var lines = (await db.LayoutLines.AsNoTracking().Where(l => l.ViewId == viewId).ToListAsync(ct))
            .OrderBy(l => l.FromId).ThenBy(l => l.ToId)
            .Select(l => new LayoutLineDto(l.FromId, l.ToId, JsonSerializer.Deserialize<object>(l.Route.RootElement.GetRawText())!))
            .ToList();
        var visibility = LineVisibility.Of(await db.LineVisibilities.AsNoTracking().FirstOrDefaultAsync(l => l.ViewId == viewId, ct));

        return Results.Ok(new ViewDetailDto(
            view.Id, view.ProjectId, view.Name, view.RootElementId,
            view.VisibleElementIds, view.ActiveLens, view.ActiveFlowId,
            view.CreatedAt, view.UpdatedAt, positions, edges, lines, LineVisibilityDto.Of(visibility), view.HiddenElementIds));
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
            return NotFound("View");

        // Optimistic concurrency: reject if client's updated_at doesn't match
        if (request.UpdatedAt.HasValue && request.UpdatedAt.Value != view.UpdatedAt)
            return Results.Conflict(new ErrorResponse("conflict", "View was modified by another user"));

        if (request.Name is not null) view.Name = request.Name;
        if (request.SetRootElementId || request.RootElementId.HasValue) view.RootElementId = request.RootElementId;
        if (request.VisibleElementIds is not null) view.VisibleElementIds = request.VisibleElementIds;
        // A hidden card is view data: the list replaces the saved one, without repeats.
        if (request.HiddenElementIds is not null) view.HiddenElementIds = [.. request.HiddenElementIds.Distinct()];
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
            return NotFound("View");

        db.Views.Remove(view);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    /// <summary>Replaces the positions, the rows of the relationships when the request has them, and the routes of the derived lines when the request has them.</summary>
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

        if (request.Edges is not null
            && Refusal([.. request.Edges.SelectMany((e, i) => new[]
            {
                SideError($"edges[{i}].sourceSide", e.SourceSide),
                SideError($"edges[{i}].targetSide", e.TargetSide),
                LayoutRoute.IsAbsent(e.RoutePoints) || LayoutRoute.Read(e.RoutePoints) is not null ? null : new ValidationError($"edges[{i}].routePoints", LayoutRoute.Shape),
            })]) is { } refusal)
            return refusal;

        if (request.Lines is not null
            && Refusal([.. request.Lines.Select((l, i) =>
                l.Route is null || LayoutRoute.Read(l.Route) is null ? new ValidationError($"lines[{i}].route", LayoutRoute.Shape)
                : l.FromId == l.ToId ? new ValidationError($"lines[{i}].toId", "must name a card other than fromId")
                : request.Lines.Take(i).Any(o => o.FromId == l.FromId && o.ToId == l.ToId) ? new ValidationError($"lines[{i}]", "names a line from the same card to the same card twice")
                : null)]) is { } lineRefusal)
            return lineRefusal;

        var view = await db.Views.FindAsync([viewId], ct);
        if (view is null || view.ProjectId != projectId)
            return NotFound("View");

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
                RoutePoints = LayoutRoute.IsAbsent(e.RoutePoints) ? null : e.RoutePoints,
                SourceSide = e.SourceSide,
                TargetSide = e.TargetSide,
            }));
        }

        if (request.Lines is not null)
        {
            var existingLines = await db.LayoutLines.Where(l => l.ViewId == viewId).ToListAsync(ct);
            db.LayoutLines.RemoveRange(existingLines);
            db.LayoutLines.AddRange(request.Lines.Select(l => new LayoutLineEntity
            {
                Id = Guid.NewGuid(),
                ViewId = viewId,
                FromId = l.FromId,
                ToId = l.ToId,
                Route = l.Route,
            }));
        }

        view.UpdatedAt = time.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return Results.Ok();
    }

    /// <summary>
    /// Sets the sides of one relationship in the view and keeps its route. Null is auto. The
    /// relationship's source and target in the model do not change: a side is presentation.
    /// </summary>
    private static async Task<IResult> SaveEdgeSides(
        Guid projectId, Guid viewId, Guid relationshipId,
        SaveEdgeSidesRequest request,
        ClaimsPrincipal user,
        AppDbContext db,
        WorkspaceAuthorizationService auth,
        TimeProvider time,
        CancellationToken ct)
    {
        await auth.RequireProjectRoleAsync(projectId, user.UserId(), WorkspaceRole.Editor, ct);

        if (Refusal(SideError("sourceSide", request.SourceSide), SideError("targetSide", request.TargetSide)) is { } refusal)
            return refusal;

        var view = await db.Views.FindAsync([viewId], ct);
        if (view is null || view.ProjectId != projectId)
            return NotFound("View");

        var edge = await db.LayoutEdges.FirstOrDefaultAsync(e => e.ViewId == viewId && e.RelationshipId == relationshipId, ct);
        if (edge is null)
        {
            edge = new LayoutEdgeEntity { Id = Guid.NewGuid(), ViewId = viewId, RelationshipId = relationshipId };
            db.LayoutEdges.Add(edge);
        }
        edge.SourceSide = request.SourceSide;
        edge.TargetSide = request.TargetSide;
        // A row with no route and no side says nothing.
        if (edge.RoutePoints is null && edge.SourceSide is null && edge.TargetSide is null)
            db.LayoutEdges.Remove(edge);

        view.UpdatedAt = time.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return Results.Ok(ToDto(edge));
    }

    /// <summary>
    /// Replaces which lines the view draws. A visibility that hides nothing removes the saved row,
    /// so the view is as one that never hid a line. The same relationship or derived line named
    /// twice is kept once.
    /// </summary>
    private static async Task<IResult> SaveLineVisibility(
        Guid projectId, Guid viewId,
        LineVisibilityDto request,
        ClaimsPrincipal user,
        AppDbContext db,
        WorkspaceAuthorizationService auth,
        TimeProvider time,
        CancellationToken ct)
    {
        await auth.RequireProjectRoleAsync(projectId, user.UserId(), WorkspaceRole.Editor, ct);

        var relationships = request.HiddenRelationshipIds ?? [];
        var lines = request.HiddenDerivedLines ?? [];
        if (Refusal(
                relationships.Count > LineVisibility.MaxHidden ? new ValidationError("hiddenRelationshipIds", $"must hold at most {LineVisibility.MaxHidden} relationships") : null,
                lines.Count > LineVisibility.MaxHidden ? new ValidationError("hiddenDerivedLines", $"must hold at most {LineVisibility.MaxHidden} lines") : null,
                lines.Select((l, i) => l is null || l.FromId == l.ToId ? new ValidationError($"hiddenDerivedLines[{i}]", "must name two different cards") : null)
                    .FirstOrDefault(e => e is not null)) is { } refusal)
            return refusal;

        var view = await db.Views.FindAsync([viewId], ct);
        if (view is null || view.ProjectId != projectId)
            return NotFound("View");

        var visibility = new LineVisibility(
            request.DerivedLines, request.Structural, request.Behavioural, request.CrossLevel,
            [.. relationships.Distinct()], [.. lines.Select(l => new HiddenDerivedLine(l.FromId, l.ToId)).Distinct()]);

        var row = await db.LineVisibilities.FirstOrDefaultAsync(l => l.ViewId == viewId, ct);
        if (!visibility.HidesAnything)
        {
            if (row is not null) db.LineVisibilities.Remove(row);
        }
        else
        {
            if (row is null)
            {
                row = new LineVisibilityEntity { ViewId = viewId };
                db.LineVisibilities.Add(row);
            }
            row.DerivedLines = visibility.DerivedLines;
            row.Structural = visibility.Structural;
            row.Behavioural = visibility.Behavioural;
            row.CrossLevel = visibility.CrossLevel;
            row.HiddenRelationshipIds = [.. visibility.HiddenRelationshipIds];
            row.HiddenDerivedLines = LineVisibility.WritePairs(visibility.HiddenDerivedLines);
        }

        view.UpdatedAt = time.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return Results.Ok(LineVisibilityDto.Of(visibility));
    }

    private static ValidationError? SideError(string field, string? side) =>
        EdgeSides.IsValid(side) ? null : new ValidationError(field, "must be top, right, bottom, left or null (auto)");

    private static LayoutEdgeDto ToDto(LayoutEdgeEntity e) =>
        new(e.RelationshipId,
            e.RoutePoints != null ? JsonSerializer.Deserialize<object>(e.RoutePoints.RootElement.GetRawText()) : null,
            e.SourceSide, e.TargetSide);

    private static ViewDto ToDto(ViewEntity v) =>
        new(v.Id, v.ProjectId, v.Name, v.RootElementId, v.VisibleElementIds,
            v.ActiveLens, v.ActiveFlowId, v.CreatedAt, v.UpdatedAt, v.HiddenElementIds);
}
