using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Vellum.Modules.Settings.Audit;
using Vellum.Modules.Workspaces.Authorization;
using Vellum.Modules.Workspaces.Entities;
using Vellum.Shared;

namespace Vellum.Modules.Workspaces;

public sealed record InviteMemberRequest(string UserId, string Role);
public sealed record MembershipDto(Guid Id, Guid WorkspaceId, string UserId, string Role, DateTimeOffset CreatedAt);

public static class InviteMember
{
    public static async Task<IResult> Handle(
        Guid workspaceId,
        InviteMemberRequest request,
        ClaimsPrincipal user,
        WorkspacesDbContext db,
        WorkspaceAuthorizationService auth,
        CancellationToken ct)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier)!;
        await auth.RequireRoleAsync(workspaceId, userId, WorkspaceRole.Owner, ct);

        if (string.IsNullOrWhiteSpace(request.UserId) || TextRule.Line("userId", request.UserId, TextRule.MaxIdentifier) is not null)
            return Results.BadRequest(new ErrorResponse("validation_error", "Invalid user",
                Errors: [new FieldError("userId", "A user ID of at most 100 characters with no control characters is required")]));

        if (!Enum.TryParse<WorkspaceRole>(request.Role, ignoreCase: true, out _))
            return Results.BadRequest(new ErrorResponse("validation_error", "Invalid role",
                Errors: [new FieldError("role", "Must be Owner, Editor, or Viewer")]));

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
