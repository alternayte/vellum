using System.Security.Claims;
using Vellum.Modules.Workspaces.Authorization;
using Vellum.Shared;

namespace Vellum.Modules.Workspaces;

public static class DeleteProject
{
    public static async Task<IResult> Handle(
        Guid projectId,
        ClaimsPrincipal user,
        WorkspacesDbContext db,
        WorkspaceAuthorizationService auth,
        CancellationToken ct)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier)!;
        await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Owner, ct);

        var project = await db.Projects.FindAsync([projectId], ct);
        if (project is null)
            return Results.NotFound(new ErrorResponse("not_found", "Project not found"));

        db.Projects.Remove(project);
        await db.SaveChangesAsync(ct);
        return Results.Ok();
    }
}
