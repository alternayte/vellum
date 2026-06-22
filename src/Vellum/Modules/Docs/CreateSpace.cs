// src/Vellum/Modules/Docs/CreateSpace.cs
using System.Security.Claims;
using Vellum.Modules.Docs.Entities;
using Vellum.Modules.Workspaces.Authorization;

namespace Vellum.Modules.Docs;

public sealed record CreateSpaceRequest(Guid Id, string Name);

public static class CreateSpace
{
    public static async Task<IResult> Handle(
        Guid projectId,
        CreateSpaceRequest request,
        ClaimsPrincipal user,
        DocsDbContext db,
        WorkspaceAuthorizationService auth,
        CancellationToken ct)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier)!;
        await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Editor, ct);

        var existing = await db.Spaces.FindAsync([request.Id], ct);
        if (existing is not null)
            return Results.Ok(ToDto(existing));

        var space = new SpaceEntity
        {
            Id = request.Id,
            ProjectId = projectId,
            Name = request.Name
        };
        db.Spaces.Add(space);
        await db.SaveChangesAsync(ct);

        return Results.Created($"/api/projects/{projectId}/spaces/{space.Id}", ToDto(space));
    }

    private static SpaceDto ToDto(SpaceEntity s) =>
        new(s.Id, s.ProjectId, s.Name, s.CreatedAt, s.UpdatedAt);
}
