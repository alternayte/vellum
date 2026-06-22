using System.Security.Claims;
using Vellum.Modules.Workspaces.Authorization;
using Vellum.Shared;

namespace Vellum.Modules.Views;

public sealed record UpdateViewRequest(
    string? Name, Guid? RootElementId, Guid[]? VisibleElementIds,
    string? ActiveLens, DateTimeOffset? UpdatedAt,
    bool SetRootElementId = false, bool SetActiveLens = false);

public static class UpdateView
{
    public static async Task<IResult> Handle(
        Guid projectId, Guid viewId,
        UpdateViewRequest request,
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

        // Optimistic concurrency: reject if client's updated_at doesn't match
        if (request.UpdatedAt.HasValue && request.UpdatedAt.Value != view.UpdatedAt)
            return Results.Conflict(new ErrorResponse("conflict", "View was modified by another user"));

        if (request.Name is not null) view.Name = request.Name;
        if (request.SetRootElementId || request.RootElementId.HasValue) view.RootElementId = request.RootElementId;
        if (request.VisibleElementIds is not null) view.VisibleElementIds = request.VisibleElementIds;
        if (request.SetActiveLens || request.ActiveLens is not null) view.ActiveLens = request.ActiveLens;
        view.UpdatedAt = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(ct);
        return Results.Ok(new ViewDto(view.Id, view.ProjectId, view.Name, view.RootElementId,
            view.VisibleElementIds, view.ActiveLens, view.ActiveFlowId, view.CreatedAt, view.UpdatedAt));
    }
}
