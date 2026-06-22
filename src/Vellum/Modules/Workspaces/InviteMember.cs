using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
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
        db.Memberships.Add(membership);
        await db.SaveChangesAsync(ct);

        return Results.Created($"/api/workspaces/{workspaceId}/members/{request.UserId}",
            new MembershipDto(membership.Id, membership.WorkspaceId, membership.UserId, membership.Role, membership.CreatedAt));
    }
}
