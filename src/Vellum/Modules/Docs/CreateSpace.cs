// src/Vellum/Modules/Docs/CreateSpace.cs
using System.Security.Claims;
using Vellum.Modules.Docs.Entities;
using Vellum.Modules.Workspaces.Authorization;
using Vellum.Shared;

namespace Vellum.Modules.Docs;

public sealed record CreateSpaceRequest(Guid Id, string Name);

public static class CreateSpace
{
    public static async Task<IResult> Handle(
        Guid projectId,
        CreateSpaceRequest request,
        ClaimsPrincipal user,
        AppDbContext db,
        WorkspaceAuthorizationService auth,
        CancellationToken ct)
    {
        var userId = user.UserId();
        await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Editor, ct);

        if (ResultExtensions.Refusal(ResultExtensions.Required("name", request.Name), TextRule.Line("name", request.Name)) is { } refusal)
            return refusal;

        // A retry in the same project gets the space back. A space of another project is not
        // shown: the caller learns only that the ID is taken.
        var existing = await db.Spaces.FindAsync([request.Id], ct);
        if (existing is not null)
            return existing.ProjectId == projectId ? Results.Ok(ToDto(existing)) : IdInUse.Conflict();

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
