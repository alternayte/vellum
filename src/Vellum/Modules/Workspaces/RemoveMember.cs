using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Vellum.Modules.Workspaces.Authorization;
using Vellum.Shared;

namespace Vellum.Modules.Workspaces;

public static class RemoveMember
{
    public static async Task<IResult> Handle(
        Guid workspaceId,
        string memberUserId,
        ClaimsPrincipal user,
        WorkspacesDbContext db,
        WorkspaceAuthorizationService auth,
        CancellationToken ct)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier)!;
        await auth.RequireRoleAsync(workspaceId, userId, WorkspaceRole.Owner, ct);

        var membership = await db.Memberships
            .FirstOrDefaultAsync(m => m.WorkspaceId == workspaceId && m.UserId == memberUserId, ct);
        if (membership is null)
            return Results.NotFound(new ErrorResponse("not_found", "Membership not found"));

        db.Memberships.Remove(membership);
        await db.SaveChangesAsync(ct);
        return Results.Ok();
    }
}
