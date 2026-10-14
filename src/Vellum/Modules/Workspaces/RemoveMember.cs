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
        AppDbContext db,
        WorkspaceAuthorizationService auth,
        TimeProvider time,
        CancellationToken ct)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier)!;
        await auth.RequireRoleAsync(workspaceId, userId, WorkspaceRole.Owner, ct);

        // The owner rows stay locked until the commit, so a removal and a role change that
        // each leave one owner take turns, and the workspace keeps an owner.
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var owners = await MemberRoles.LockOwnersAsync(db, workspaceId, ct);
        // The caller can have stopped being an owner while this call waited for the lock.
        await auth.RequireRoleAsync(workspaceId, userId, WorkspaceRole.Owner, ct);

        var membership = await db.Memberships
            .FirstOrDefaultAsync(m => m.WorkspaceId == workspaceId && m.UserId == memberUserId, ct);
        if (membership is null)
            return Results.NotFound(new ErrorResponse("not_found", "Membership not found"));

        var role = MemberRoles.Canonical(membership.Role);
        if (role == nameof(WorkspaceRole.Owner) && owners <= 1)
            return Results.Conflict(new ErrorResponse(
                "last_owner",
                "Cannot remove the last owner",
                "A workspace must have at least one owner."));

        // The removal, the revoked invitations of a removed owner and the audit entries commit together.
        db.Memberships.Remove(membership);
        await db.SaveChangesAsync(ct);
        await AuditLog.AppendAsync(db, workspaceId, userId, AuditLog.MemberRemoved,
            $"user:{membership.UserId} role:{role.ToLowerInvariant()}", ct);
        if (role == nameof(WorkspaceRole.Owner))
            await InvitationRules.RevokeIssuedByAsync(db, workspaceId, membership.UserId, userId, "issuer-removed", time.GetUtcNow(), ct);
        await transaction.CommitAsync(ct);
        return Results.NoContent();
    }
}
