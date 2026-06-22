using System.Security.Claims;
using Vellum.Modules.Workspaces.Authorization;
using Vellum.Modules.Workspaces.Entities;

namespace Vellum.Modules.Workspaces;

public sealed record CreateProjectRequest(Guid Id, string Name, string? Description);
public sealed record ProjectDto(Guid Id, Guid WorkspaceId, string Name, string? Description, Guid StreamId, DateTimeOffset CreatedAt);

public static class CreateProject
{
    public static async Task<IResult> Handle(
        Guid workspaceId,
        CreateProjectRequest request,
        ClaimsPrincipal user,
        WorkspacesDbContext db,
        WorkspaceAuthorizationService auth,
        CancellationToken ct)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier)!;
        await auth.RequireRoleAsync(workspaceId, userId, WorkspaceRole.Editor, ct);

        var existing = await db.Projects.FindAsync([request.Id], ct);
        if (existing is not null)
            return Results.Ok(ToDto(existing));

        var project = new ProjectEntity
        {
            Id = request.Id,
            WorkspaceId = workspaceId,
            Name = request.Name,
            Description = request.Description,
            StreamId = Guid.NewGuid()
        };
        db.Projects.Add(project);
        await db.SaveChangesAsync(ct);

        return Results.Created($"/api/projects/{project.Id}", ToDto(project));
    }

    private static ProjectDto ToDto(ProjectEntity p) =>
        new(p.Id, p.WorkspaceId, p.Name, p.Description, p.StreamId, p.CreatedAt);
}
