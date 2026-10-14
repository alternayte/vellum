using System.Net.Mail;
using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Vellum.Modules.Identity;
using Vellum.Modules.Settings.Audit;
using Vellum.Modules.Workspaces.Authorization;
using Vellum.Modules.Workspaces.Entities;
using Vellum.Shared;

namespace Vellum.Modules.Workspaces;

public sealed record CreateInvitationRequest(string Email, string Role);

/// <summary>An invitation as an owner of its workspace reads it.</summary>
public sealed record InvitationDto(Guid Id, Guid WorkspaceId, string Email, string Role, string Status, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt);

/// <summary>An invitation as the invited person reads it: the workspace, the role and who sent it.</summary>
public sealed record MyInvitationDto(Guid Id, Guid WorkspaceId, string WorkspaceName, string Role, string? InvitedBy, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt);

/// <summary>The rate of invitations for one caller. Tests replace it.</summary>
public sealed record InvitationLimits(int PerMinute = 10)
{
    public static readonly InvitationLimits Default = new();
}

/// <summary>
/// The window of invitation creation. Each invitation is a row and an audit entry, so one
/// caller makes a bounded number of them in a minute.
/// </summary>
internal sealed class InvitationRate(InvitationLimits limits) : CallerRate(limits.PerMinute);

public static class InvitationRules
{
    public const int MaxEmailLength = 256;
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(14);
    /// <summary>Open invitations of one workspace. A workspace with more revokes some or waits for them to expire.</summary>
    public const int MaxPendingPerWorkspace = 100;

    internal static string StatusAt(InvitationEntity invitation, DateTimeOffset now) =>
        invitation.Status == InvitationStatus.Pending && invitation.ExpiresAt <= now ? InvitationStatus.Expired : invitation.Status;

    internal static InvitationDto ToDto(InvitationEntity i, DateTimeOffset now) =>
        new(i.Id, i.WorkspaceId, i.Email, MemberRoles.Canonical(i.Role), StatusAt(i, now), i.CreatedAt, i.ExpiresAt);

    /// <summary>The invitation row, locked for the open transaction.</summary>
    internal static async Task<InvitationEntity?> LockAsync(WorkspacesDbContext db, Guid invitationId, CancellationToken ct) =>
        (await db.Invitations
            .FromSql($"SELECT * FROM workspaces.invitations WHERE id = {invitationId} FOR UPDATE")
            .ToListAsync(ct))
        .FirstOrDefault();

    internal static IResult NotFound() => Results.NotFound(new ErrorResponse("not_found", "Invitation not found"));

    internal static IResult Closed(string status) => Results.Conflict(new ErrorResponse(
        "invitation_closed",
        $"The invitation is {status}",
        status == InvitationStatus.Expired
            ? "An owner of the workspace can invite you again."
            : "The invitation cannot change any more."));
}

/// <summary>
/// An owner invites an email address. No account is looked up for the answer: an address
/// with an account and one without get the same answer, so the endpoint tells nothing about
/// who has an account here. Each invitation is in the audit log by its ID and role; the
/// address stays in the invitation row, which only owners read.
/// </summary>
public static class CreateInvitation
{
    public static async Task<IResult> Handle(
        Guid workspaceId,
        CreateInvitationRequest request,
        ClaimsPrincipal user,
        WorkspacesDbContext db,
        WorkspaceAuthorizationService auth,
        AppIdentityDbContext identity,
        ILookupNormalizer normalizer,
        TimeProvider time,
        CancellationToken ct)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier)!;
        await auth.RequireRoleAsync(workspaceId, userId, WorkspaceRole.Owner, ct);

        if (MemberRoles.Parse(request.Role) is not { } role)
            return Results.BadRequest(new ErrorResponse("validation_error", "Invalid role",
                Errors: [new FieldError("role", "Must be Owner, Editor, or Viewer")]));

        var email = request.Email?.Trim();
        if (string.IsNullOrEmpty(email) || email.Length > InvitationRules.MaxEmailLength
            || TextRule.Line("email", email, InvitationRules.MaxEmailLength) is not null
            || !MailAddress.TryCreate(email, out var parsed) || parsed.Address != email)
            return Results.BadRequest(new ErrorResponse("validation_error", "Invalid email address",
                Errors: [new FieldError("email", $"An email address of at most {InvitationRules.MaxEmailLength} characters is required.")]));
        var normalized = normalizer.NormalizeEmail(email);

        // The owner reads the email address of each member in the member list, so this
        // answer tells the owner nothing new.
        var memberIds = await db.Memberships.Where(m => m.WorkspaceId == workspaceId).Select(m => m.UserId).ToListAsync(ct);
        if (await identity.Users.AnyAsync(u => memberIds.Contains(u.Id) && u.NormalizedEmail == normalized, ct))
            return Results.Conflict(new ErrorResponse("already_member", "This person is already a member",
                "Change the role in the member list."));

        var now = time.GetUtcNow();
        // The workspace row stays locked until the commit, so two invitations of one address
        // take turns, and the count of open invitations holds.
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.Database
            .SqlQuery<Guid>($"""SELECT id AS "Value" FROM workspaces.workspaces WHERE id = {workspaceId} FOR UPDATE""")
            .ToListAsync(ct);

        var invitation = await db.Invitations.FirstOrDefaultAsync(i =>
            i.WorkspaceId == workspaceId && i.NormalizedEmail == normalized && i.Status == InvitationStatus.Pending, ct);
        var renewed = invitation is not null;
        if (invitation is null)
        {
            var open = await db.Invitations.CountAsync(i =>
                i.WorkspaceId == workspaceId && i.Status == InvitationStatus.Pending && i.ExpiresAt > now, ct);
            if (open >= InvitationRules.MaxPendingPerWorkspace)
                return Results.Conflict(new ErrorResponse("too_many_invitations", "The workspace has too many open invitations",
                    $"A workspace holds at most {InvitationRules.MaxPendingPerWorkspace} open invitations. Revoke some first."));

            invitation = new InvitationEntity
            {
                Id = Guid.NewGuid(),
                WorkspaceId = workspaceId,
                NormalizedEmail = normalized,
                CreatedAt = now,
            };
            db.Invitations.Add(invitation);
        }

        // A second invitation of the same address renews the open one: the new role, a new expiry.
        invitation.Email = email;
        invitation.Role = role.ToString();
        invitation.InvitedBy = userId;
        invitation.ExpiresAt = now + InvitationRules.Lifetime;
        await db.SaveChangesAsync(ct);
        await AuditLog.AppendAsync(db, workspaceId, userId, AuditLog.MemberInvited,
            $"invitation:{invitation.Id} role:{invitation.Role.ToLowerInvariant()}", ct);
        await transaction.CommitAsync(ct);

        var dto = InvitationRules.ToDto(invitation, now);
        return renewed
            ? Results.Ok(dto)
            : Results.Created($"/api/workspaces/{workspaceId}/invitations/{invitation.Id}", dto);
    }
}

public static class ListInvitations
{
    /// <summary>The open invitations of the workspace. Only an owner reads them: they hold email addresses.</summary>
    public static async Task<IResult> Handle(
        Guid workspaceId,
        ClaimsPrincipal user,
        WorkspacesDbContext db,
        WorkspaceAuthorizationService auth,
        TimeProvider time,
        CancellationToken ct)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier)!;
        await auth.RequireRoleAsync(workspaceId, userId, WorkspaceRole.Owner, ct);

        var now = time.GetUtcNow();
        var invitations = await db.Invitations.AsNoTracking()
            .Where(i => i.WorkspaceId == workspaceId && i.Status == InvitationStatus.Pending && i.ExpiresAt > now)
            .OrderBy(i => i.CreatedAt).ThenBy(i => i.Id)
            .ToListAsync(ct);
        return Results.Ok(invitations.Select(i => InvitationRules.ToDto(i, now)).ToList());
    }
}

public static class RevokeInvitation
{
    public static async Task<IResult> Handle(
        Guid workspaceId,
        Guid invitationId,
        ClaimsPrincipal user,
        WorkspacesDbContext db,
        WorkspaceAuthorizationService auth,
        TimeProvider time,
        CancellationToken ct)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier)!;
        await auth.RequireRoleAsync(workspaceId, userId, WorkspaceRole.Owner, ct);

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var invitation = await InvitationRules.LockAsync(db, invitationId, ct);
        // An invitation of another workspace is no invitation of this one.
        if (invitation is null || invitation.WorkspaceId != workspaceId)
            return InvitationRules.NotFound();

        var now = time.GetUtcNow();
        switch (invitation.Status)
        {
            case InvitationStatus.Revoked:
                return Results.NoContent();
            case InvitationStatus.Accepted or InvitationStatus.Declined:
                return InvitationRules.Closed(invitation.Status);
        }

        invitation.Status = InvitationStatus.Revoked;
        invitation.RespondedBy = userId;
        invitation.RespondedAt = now;
        await db.SaveChangesAsync(ct);
        await AuditLog.AppendAsync(db, workspaceId, userId, AuditLog.InvitationRevoked,
            $"invitation:{invitation.Id} role:{invitation.Role.ToLowerInvariant()}", ct);
        await transaction.CommitAsync(ct);
        return Results.NoContent();
    }
}

/// <summary>
/// The invitations of the signed-in person: those for the email address of the account.
/// Vellum sends no email, so the address on the account is what an invitation is matched
/// to. An invitation for any other address is not found for this person, whatever its state.
/// </summary>
public static class MyInvitations
{
    public static async Task<IResult> List(
        ClaimsPrincipal user,
        WorkspacesDbContext db,
        AppIdentityDbContext identity,
        TimeProvider time,
        CancellationToken ct)
    {
        var email = await EmailOfAsync(user, identity, ct);
        if (email is null)
            return Results.Ok(Array.Empty<MyInvitationDto>());

        var now = time.GetUtcNow();
        var rows = await db.Invitations.AsNoTracking()
            .Where(i => i.NormalizedEmail == email && i.Status == InvitationStatus.Pending && i.ExpiresAt > now)
            .Join(db.Workspaces, i => i.WorkspaceId, w => w.Id, (i, w) => new { Invitation = i, WorkspaceName = w.Name })
            .OrderBy(r => r.Invitation.CreatedAt)
            .ToListAsync(ct);
        var inviterIds = rows.Select(r => r.Invitation.InvitedBy).Distinct().ToList();
        var inviters = await identity.Users.AsNoTracking()
            .Where(u => inviterIds.Contains(u.Id))
            .Select(u => new { u.Id, Name = u.DisplayName ?? u.Email })
            .ToDictionaryAsync(u => u.Id, u => u.Name, ct);

        return Results.Ok(rows.Select(r => new MyInvitationDto(
            r.Invitation.Id, r.Invitation.WorkspaceId, r.WorkspaceName, MemberRoles.Canonical(r.Invitation.Role),
            inviters.GetValueOrDefault(r.Invitation.InvitedBy), r.Invitation.CreatedAt, r.Invitation.ExpiresAt)).ToList());
    }

    /// <summary>
    /// Makes the membership with the invited role. A second accept of the same invitation by
    /// the same person answers the same membership. A person who is a member already keeps
    /// the role they have: an owner changes a role in the member list.
    /// </summary>
    public static async Task<IResult> Accept(
        Guid invitationId,
        ClaimsPrincipal user,
        WorkspacesDbContext db,
        AppIdentityDbContext identity,
        TimeProvider time,
        CancellationToken ct)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var email = await EmailOfAsync(user, identity, ct);

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var invitation = await InvitationRules.LockAsync(db, invitationId, ct);
        if (invitation is null || email is null || invitation.NormalizedEmail != email)
            return InvitationRules.NotFound();

        var membership = await db.Memberships
            .FirstOrDefaultAsync(m => m.WorkspaceId == invitation.WorkspaceId && m.UserId == userId, ct);
        var now = time.GetUtcNow();
        var status = InvitationRules.StatusAt(invitation, now);
        if (status == InvitationStatus.Accepted && invitation.RespondedBy == userId && membership is not null)
            return Results.Ok(ToDto(membership));
        if (status != InvitationStatus.Pending)
            return InvitationRules.Closed(status);

        var added = membership is null;
        if (membership is null)
        {
            membership = new MembershipEntity
            {
                Id = Guid.NewGuid(),
                WorkspaceId = invitation.WorkspaceId,
                UserId = userId,
                Role = invitation.Role,
                CreatedAt = now,
            };
            db.Memberships.Add(membership);
        }
        invitation.Status = InvitationStatus.Accepted;
        invitation.RespondedBy = userId;
        invitation.RespondedAt = now;
        await db.SaveChangesAsync(ct);
        // The membership, the closed invitation and the audit entry commit together.
        await AuditLog.AppendAsync(db, invitation.WorkspaceId, userId, added ? AuditLog.MemberAdded : AuditLog.InvitationAccepted,
            $"user:{userId} role:{MemberRoles.Canonical(membership.Role).ToLowerInvariant()} invitation:{invitation.Id}", ct);
        await transaction.CommitAsync(ct);
        return Results.Ok(ToDto(membership));
    }

    public static async Task<IResult> Decline(
        Guid invitationId,
        ClaimsPrincipal user,
        WorkspacesDbContext db,
        AppIdentityDbContext identity,
        TimeProvider time,
        CancellationToken ct)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var email = await EmailOfAsync(user, identity, ct);

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var invitation = await InvitationRules.LockAsync(db, invitationId, ct);
        if (invitation is null || email is null || invitation.NormalizedEmail != email)
            return InvitationRules.NotFound();

        var status = InvitationRules.StatusAt(invitation, time.GetUtcNow());
        if (status == InvitationStatus.Declined)
            return Results.NoContent();
        if (status != InvitationStatus.Pending)
            return InvitationRules.Closed(status);

        invitation.Status = InvitationStatus.Declined;
        invitation.RespondedBy = userId;
        invitation.RespondedAt = time.GetUtcNow();
        await db.SaveChangesAsync(ct);
        await AuditLog.AppendAsync(db, invitation.WorkspaceId, userId, AuditLog.InvitationDeclined,
            $"invitation:{invitation.Id} role:{invitation.Role.ToLowerInvariant()}", ct);
        await transaction.CommitAsync(ct);
        return Results.NoContent();
    }

    /// <summary>The normalized email address of the signed-in account, read from the account and not from a claim.</summary>
    private static async Task<string?> EmailOfAsync(ClaimsPrincipal user, AppIdentityDbContext identity, CancellationToken ct)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier)!;
        return await identity.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => u.NormalizedEmail).FirstOrDefaultAsync(ct);
    }

    private static MembershipDto ToDto(MembershipEntity m) =>
        new(m.Id, m.WorkspaceId, m.UserId, MemberRoles.Canonical(m.Role), m.CreatedAt);
}
