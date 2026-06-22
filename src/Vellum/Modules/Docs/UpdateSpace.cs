// src/Vellum/Modules/Docs/UpdateSpace.cs
using System.Security.Claims;
using Vellum.Modules.Workspaces.Authorization;
using Vellum.Shared;

namespace Vellum.Modules.Docs;

public sealed record UpdateSpaceRequest(string? Name);

public static class UpdateSpace
{
    public static async Task<IResult> Handle(
        Guid projectId, Guid spaceId,
        UpdateSpaceRequest request,
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

        if (request.Name is not null) space.Name = request.Name;
        space.UpdatedAt = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(ct);
        return Results.Ok(new SpaceDto(space.Id, space.ProjectId, space.Name, space.CreatedAt, space.UpdatedAt));
    }
}
