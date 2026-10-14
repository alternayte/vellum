using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Vellum.Shared;

namespace Vellum.Modules.Workspaces;

public static class ListWorkspaces
{
    public static async Task<IResult> Handle(
        ClaimsPrincipal user,
        AppDbContext db,
        CancellationToken ct)
    {
        var userId = user.UserId();

        var workspaceIds = await db.Memberships
            .AsNoTracking()
            .Where(m => m.UserId == userId)
            .Select(m => m.WorkspaceId)
            .ToListAsync(ct);

        var workspaces = await db.Workspaces
            .AsNoTracking()
            .Where(w => workspaceIds.Contains(w.Id))
            .OrderBy(w => w.Name)
            .Select(w => new WorkspaceDto(w.Id, w.Name, w.CreatedBy, w.CreatedAt))
            .ToListAsync(ct);

        return Results.Ok(workspaces);
    }
}
