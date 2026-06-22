// src/Vellum/Modules/Docs/DeleteSpace.cs
using System.Security.Claims;
using Vellum.Modules.Workspaces.Authorization;
using Vellum.Shared;

namespace Vellum.Modules.Docs;

public static class DeleteSpace
{
    public static async Task<IResult> Handle(
        Guid projectId, Guid spaceId,
        ClaimsPrincipal user,
        DocsDbContext db,
        WorkspaceAuthorizationService auth,
        CancellationToken ct)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier)!;
        await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Editor, ct);

        var space = await db.Spaces.FindAsync([spaceId], ct);
        if (space is null || space.ProjectId != projectId)
            return Results.NotFound(new ErrorResponse("not_found", "Space not found"));

        db.Spaces.Remove(space);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }
}
