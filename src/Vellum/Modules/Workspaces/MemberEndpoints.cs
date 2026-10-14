using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Vellum.Modules.Identity;
using Vellum.Modules.Settings.Audit;
using Vellum.Shared;

namespace Vellum.Modules.Workspaces;

/// <param name="Email">Null when the account of the member does not exist any more.</param>
/// <param name="CreatedAt">When the person became a member. Null for a membership that is older than this record.</param>
public sealed record MemberDto(string UserId, string? Email, string? DisplayName, string Role, DateTimeOffset? CreatedAt);

/// <param name="YourRole">The role of the caller, so a screen offers only what the caller can do.</param>
public sealed record MembersResponse(string YourRole, IReadOnlyList<MemberDto> Members);

public sealed record ChangeMemberRoleRequest(string Role);

public sealed record MembershipDto(Guid Id, Guid WorkspaceId, string UserId, string Role, DateTimeOffset CreatedAt);

public static class ListMembers
{
    /// <summary>Every member reads the list: a member sees who else can read and change the workspace.</summary>
    public static async Task<IResult> Handle(
        Guid workspaceId,
        ClaimsPrincipal user,
        AppDbContext db,
        WorkspaceAuthorizationService auth,
        CancellationToken ct)
    {
        var userId = user.UserId();
        await auth.RequireRoleAsync(workspaceId, userId, WorkspaceRole.Viewer, ct);

        var memberships = await db.Memberships.AsNoTracking()
            .Where(m => m.WorkspaceId == workspaceId)
            .OrderBy(m => m.CreatedAt).ThenBy(m => m.UserId)
            .ToListAsync(ct);
        var ids = memberships.Select(m => m.UserId).ToList();
        var accounts = await db.Users.AsNoTracking()
            .Where(u => ids.Contains(u.Id))
            .Select(u => new { u.Id, u.Email, u.DisplayName })
            .ToDictionaryAsync(u => u.Id, ct);

        var members = memberships
            .Select(m => new MemberDto(
                m.UserId, accounts.GetValueOrDefault(m.UserId)?.Email, accounts.GetValueOrDefault(m.UserId)?.DisplayName,
                MemberRoles.Canonical(m.Role), m.CreatedAt == default ? null : m.CreatedAt))
            .ToList();
        return Results.Ok(new MembersResponse(members.First(m => m.UserId == userId).Role, members));
    }
}

public static class ChangeMemberRole
{
    public static async Task<IResult> Handle(
        Guid workspaceId,
        string memberUserId,
        ChangeMemberRoleRequest request,
        ClaimsPrincipal user,
        AppDbContext db,
        WorkspaceAuthorizationService auth,
        TimeProvider time,
        CancellationToken ct)
    {
        var userId = user.UserId();
        await auth.RequireRoleAsync(workspaceId, userId, WorkspaceRole.Owner, ct);

        if (MemberRoles.Parse(request.Role) is not { } role)
            return Results.BadRequest(new ErrorResponse("validation_error", "Invalid role",
                Errors: [new FieldError("role", "Must be Owner, Editor, or Viewer")]));

        // The owner rows stay locked until the commit, so two changes that each leave one
        // owner take turns, and the workspace keeps an owner.
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var owners = await MemberRoles.LockOwnersAsync(db, workspaceId, ct);
        // The caller can have stopped being an owner while this call waited for the lock.
        await auth.RequireRoleAsync(workspaceId, userId, WorkspaceRole.Owner, ct);

        var membership = await db.Memberships
            .FirstOrDefaultAsync(m => m.WorkspaceId == workspaceId && m.UserId == memberUserId, ct);
        if (membership is null)
            return Results.NotFound(new ErrorResponse("not_found", "Membership not found"));

        var before = MemberRoles.Canonical(membership.Role);
        if (before == role.ToString())
            return Results.Ok(ToDto(membership));

        if (before == nameof(WorkspaceRole.Owner) && owners <= 1)
            return Results.Conflict(new ErrorResponse(
                "last_owner",
                "Cannot change the role of the last owner",
                "A workspace must have at least one owner. Make another member an owner first."));

        membership.Role = role.ToString();
        await db.SaveChangesAsync(ct);
        await AuditLog.AppendAsync(db, workspaceId, userId, AuditLog.MemberRoleChanged,
            $"user:{membership.UserId} role:{before.ToLowerInvariant()}->{membership.Role.ToLowerInvariant()}", ct);
        // A demoted owner's open invitations are revoked in the same transaction.
        if (before == nameof(WorkspaceRole.Owner))
            await InvitationRules.RevokeIssuedByAsync(db, workspaceId, membership.UserId, userId, "issuer-demoted", time.GetUtcNow(), ct);
        await transaction.CommitAsync(ct);
        return Results.Ok(ToDto(membership));
    }

    private static MembershipDto ToDto(MembershipEntity m) =>
        new(m.Id, m.WorkspaceId, m.UserId, MemberRoles.Canonical(m.Role), m.CreatedAt);
}

internal static class MemberRoles
{
    /// <summary>A role by its name. A number is no role name.</summary>
    public static WorkspaceRole? Parse(string? text) =>
        text is not null && !int.TryParse(text, out _)
        && Enum.TryParse<WorkspaceRole>(text, ignoreCase: true, out var role) && Enum.IsDefined(role)
            ? role
            : null;

    /// <summary>The role as the enum writes it. A stored role can differ in case.</summary>
    public static string Canonical(string role) => Enum.Parse<WorkspaceRole>(role, ignoreCase: true).ToString();

    /// <summary>Locks the owner rows of the workspace for the open transaction and returns how many there are.</summary>
    public static async Task<int> LockOwnersAsync(AppDbContext db, Guid workspaceId, CancellationToken ct)
    {
        var owners = await db.Database
            .SqlQuery<Guid>($"""
                SELECT id AS "Value" FROM workspaces.memberships
                WHERE workspace_id = {workspaceId} AND lower(role) = 'owner'
                ORDER BY id
                FOR UPDATE
                """)
            .ToListAsync(ct);
        return owners.Count;
    }
}

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
        var userId = user.UserId();
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
