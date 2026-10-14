using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Vellum.Modules.Workspaces.Authorization;
using Vellum.Shared;

namespace Vellum.Modules.Views;

public static class ListViews
{
    public static async Task<IResult> Handle(
        Guid projectId,
        ClaimsPrincipal user,
        AppDbContext db,
        WorkspaceAuthorizationService auth,
        CancellationToken ct)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier)!;
        await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Viewer, ct);

        var views = await db.Views.AsNoTracking()
            .Where(v => v.ProjectId == projectId)
            .OrderBy(v => v.Name)
            .Select(v => new ViewDto(v.Id, v.ProjectId, v.Name, v.RootElementId,
                v.VisibleElementIds, v.ActiveLens, v.ActiveFlowId, v.CreatedAt, v.UpdatedAt))
            .ToListAsync(ct);

        return Results.Ok(views);
    }
}
