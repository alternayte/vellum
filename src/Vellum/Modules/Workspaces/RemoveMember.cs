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

        if (membership.Role == nameof(WorkspaceRole.Owner))
        {
            var ownerCount = await db.Memberships
                .CountAsync(m => m.WorkspaceId == workspaceId && m.Role == nameof(WorkspaceRole.Owner), ct);
            if (ownerCount <= 1)
                return Results.Conflict(new ErrorResponse(
                    "last_owner",
                    "Cannot remove the last owner",
                    "A workspace must have at least one owner."));
        }

        db.Memberships.Remove(membership);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }
}
