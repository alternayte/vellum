using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Vellum.Modules.Identity;
using Vellum.Modules.Settings.Audit;
using Vellum.Modules.Workspaces.Authorization;
using Vellum.Modules.Workspaces.Entities;
using Vellum.Shared;

namespace Vellum.Modules.Workspaces;

/// <summary>
/// Names the account by <paramref name="Email"/> or by <paramref name="UserId"/>. The account
/// must exist on this instance: Vellum sends no invitation.
/// </summary>
public sealed record InviteMemberRequest(string? UserId, string Role, string? Email = null);
public sealed record MembershipDto(Guid Id, Guid WorkspaceId, string UserId, string Role, DateTimeOffset CreatedAt);

public static class InviteMember
{
    public const int MaxUserIdLength = 64;
    public const int MaxEmailLength = 256;

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

        if (MemberRoles.Parse(request.Role) is not { } role)
            return Results.BadRequest(new ErrorResponse("validation_error", "Invalid role",
                Errors: [new FieldError("role", "Must be Owner, Editor, or Viewer")]));

        // The user ID goes into the membership and into the audit log, so it must be the ID
        // of an account and not text that the caller chose.
        string? invitedId;
        if (!string.IsNullOrWhiteSpace(request.Email))
        {
            if (!string.IsNullOrWhiteSpace(request.UserId))
                return Results.BadRequest(new ErrorResponse("validation_error", "Invalid user",
                    Errors: [new FieldError("email", "Give an email address or a user ID, not both.")]));

            // Identity stores the address in upper case for the lookup. Only an owner asks,
            // and the answer says no more than an attempt to register with the address says.
            var normalized = request.Email.Trim().ToUpperInvariant();
            invitedId = normalized.Length <= MaxEmailLength
                ? await identity.Users.Where(u => u.NormalizedEmail == normalized).Select(u => u.Id).FirstOrDefaultAsync(ct)
                : null;
            if (invitedId is null)
                return Results.BadRequest(new ErrorResponse("validation_error", "Invalid user",
                    Errors: [new FieldError("email", "No account on this instance has this email address. The person must create an account first.")]));
        }
        else
        {
            if (string.IsNullOrWhiteSpace(request.UserId) || TextRule.Line("userId", request.UserId, TextRule.MaxIdentifier) is not null)
                return Results.BadRequest(new ErrorResponse("validation_error", "Invalid user",
                    Errors: [new FieldError("userId", "A user ID of at most 100 characters with no control characters is required")]));

            invitedId = request.UserId;
            if (invitedId.Length > MaxUserIdLength || !await identity.Users.AnyAsync(u => u.Id == invitedId, ct))
                return Results.BadRequest(new ErrorResponse("validation_error", "Invalid user",
                    Errors: [new FieldError("userId", "Must be the ID of an existing user.")]));
        }

        var existing = await db.Memberships
            .FirstOrDefaultAsync(m => m.WorkspaceId == workspaceId && m.UserId == invitedId, ct);
        if (existing is not null)
            return Results.Conflict(new ErrorResponse("conflict", "User is already a member"));

        var membership = new MembershipEntity
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            UserId = invitedId,
            Role = role.ToString()
        };
        // The membership and its audit entry commit together.
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        db.Memberships.Add(membership);
        await db.SaveChangesAsync(ct);
        await AuditLog.AppendAsync(db, workspaceId, userId, AuditLog.MemberAdded,
            $"user:{membership.UserId} role:{membership.Role.ToLowerInvariant()}", ct);
        await transaction.CommitAsync(ct);

        return Results.Created($"/api/workspaces/{workspaceId}/members/{invitedId}",
            new MembershipDto(membership.Id, membership.WorkspaceId, membership.UserId, membership.Role, membership.CreatedAt));
    }
}
