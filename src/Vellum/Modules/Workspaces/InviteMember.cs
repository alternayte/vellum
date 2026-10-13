using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Vellum.Modules.Identity;
using Vellum.Modules.Settings.Audit;
using Vellum.Modules.Workspaces.Authorization;
using Vellum.Modules.Workspaces.Entities;
using Vellum.Shared;

namespace Vellum.Modules.Workspaces;

public sealed record InviteMemberRequest(string UserId, string Role);
public sealed record MembershipDto(Guid Id, Guid WorkspaceId, string UserId, string Role, DateTimeOffset CreatedAt);

public static class InviteMember
{
    public const int MaxUserIdLength = 64;

    public static async Task<IResult> Handle(
        Guid workspaceId,
        InviteMemberRequest request,
        ClaimsPrincipal user,
        WorkspacesDbContext db,
        WorkspaceAuthorizationService auth,
        AppIdentityDbContext identity,
        CancellationToken ct)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier)!;
        await auth.RequireRoleAsync(workspaceId, userId, WorkspaceRole.Owner, ct);

        if (!Enum.TryParse<WorkspaceRole>(request.Role, ignoreCase: true, out _))
            return Results.BadRequest(new ErrorResponse("validation_error", "Invalid role",
                Errors: [new FieldError("role", "Must be Owner, Editor, or Viewer")]));

        // The user ID goes into the membership and into the audit log, so it must be the ID
        // of an account and not text that the caller chose.
        if (request.UserId is not { Length: >= 1 and <= MaxUserIdLength } invitedId
            || !await identity.Users.AnyAsync(u => u.Id == invitedId, ct))
            return Results.BadRequest(new ErrorResponse("validation_error", "Invalid user",
                Errors: [new FieldError("userId", "Must be the ID of an existing user.")]));

        var existing = await db.Memberships
            .FirstOrDefaultAsync(m => m.WorkspaceId == workspaceId && m.UserId == request.UserId, ct);
        if (existing is not null)
            return Results.Conflict(new ErrorResponse("conflict", "User is already a member"));

        var membership = new MembershipEntity
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            UserId = request.UserId,
            Role = request.Role
        };
        // The membership and its audit entry commit together.
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        db.Memberships.Add(membership);
        await db.SaveChangesAsync(ct);
        await AuditLog.AppendAsync(db, workspaceId, userId, AuditLog.MemberAdded,
            $"user:{membership.UserId} role:{membership.Role.ToLowerInvariant()}", ct);
        await transaction.CommitAsync(ct);

        return Results.Created($"/api/workspaces/{workspaceId}/members/{request.UserId}",
            new MembershipDto(membership.Id, membership.WorkspaceId, membership.UserId, membership.Role, membership.CreatedAt));
    }
}
