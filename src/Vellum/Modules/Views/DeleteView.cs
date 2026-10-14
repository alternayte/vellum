using System.Security.Claims;
using Vellum.Modules.Workspaces.Authorization;
using Vellum.Shared;

namespace Vellum.Modules.Views;

public static class DeleteView
{
    public static async Task<IResult> Handle(
        Guid projectId, Guid viewId,
        ClaimsPrincipal user,
        AppDbContext db,
        WorkspaceAuthorizationService auth,
        CancellationToken ct)
    {
        var userId = user.UserId();
        await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Editor, ct);

        var view = await db.Views.FindAsync([viewId], ct);
        if (view is null || view.ProjectId != projectId)
            return Results.NotFound(new ErrorResponse("not_found", "View not found"));

        db.Views.Remove(view);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }
}
