using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Vellum.Modules.Settings.Audit;
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

        // The removal and its audit entry commit together.
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        db.Memberships.Remove(membership);
        await db.SaveChangesAsync(ct);
        await AuditLog.AppendAsync(db, workspaceId, userId, AuditLog.MemberRemoved,
            $"user:{membership.UserId} role:{membership.Role.ToLowerInvariant()}", ct);
        await transaction.CommitAsync(ct);
        return Results.NoContent();
    }
}
