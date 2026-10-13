using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Vellum.Modules.Workspaces.Authorization;
using Vellum.Shared;

namespace Vellum.Modules.Workspaces;

/// <param name="YourRole">The role of the caller in the workspace of the project.</param>
public sealed record ProjectDetailDto(
    Guid Id, Guid WorkspaceId, string Name, string? Description, Guid StreamId, DateTimeOffset CreatedAt, string YourRole);

public static class GetProject
{
    public static async Task<IResult> Handle(
        Guid projectId,
        ClaimsPrincipal user,
        WorkspacesDbContext db,
        WorkspaceAuthorizationService auth,
        CancellationToken ct)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var project = await db.Projects.AsNoTracking().FirstOrDefaultAsync(p => p.Id == projectId, ct);
        // A project of a workspace the caller is no member of reads as a project that does not exist.
        if (project is null || await auth.GetRoleAsync(project.WorkspaceId, userId, ct) is not { } role)
            return Results.NotFound(new ErrorResponse("not_found", "Project not found"));

        return Results.Ok(new ProjectDetailDto(
            project.Id, project.WorkspaceId, project.Name, project.Description, project.StreamId, project.CreatedAt, role.ToString()));
    }
}
