// src/Vellum/Modules/Docs/ListSpaces.cs
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Vellum.Modules.Workspaces.Authorization;

namespace Vellum.Modules.Docs;

public static class ListSpaces
{
    public static async Task<IResult> Handle(
        Guid projectId,
        ClaimsPrincipal user,
        DocsDbContext db,
        WorkspaceAuthorizationService auth,
        CancellationToken ct)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier)!;
        await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Viewer, ct);

        var spaces = await db.Spaces.AsNoTracking()
            .Where(s => s.ProjectId == projectId)
            .OrderBy(s => s.Name)
            .Select(s => new SpaceDto(s.Id, s.ProjectId, s.Name, s.CreatedAt, s.UpdatedAt))
            .ToListAsync(ct);

        return Results.Ok(spaces);
    }
}
