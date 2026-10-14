using System.Buffers.Text;
using System.Net.Mail;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Vellum.Modules.Identity;
using Vellum.Modules.Settings;
using Vellum.Shared;
using static Vellum.Shared.ResultExtensions;

namespace Vellum.Modules.Workspaces;

public sealed record CreateInvitationRequest(string Email, string Role);

/// <summary>An invitation as an owner of its workspace reads it.</summary>
public sealed record InvitationDto(Guid Id, Guid WorkspaceId, string Email, string Role, string Status, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt);

/// <summary>
/// A new or renewed invitation, with its secret. This is the only answer that holds the
/// secret: the database keeps a hash of it. <paramref name="Path"/> is the link in the app,
/// with the secret in the fragment, so a browser that opens it never sends the secret to a
/// server.
/// </summary>
public sealed record IssuedInvitationDto(Guid Id, Guid WorkspaceId, string Email, string Role, string Status, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, string Secret, string Path);

/// <summary>
/// An invitation as the invited person reads it: the workspace, the role and who sent it.
/// <paramref name="InvitedBy"/> is the display name of the sender, or null. It is never the
/// sender's email address: anyone can register an account with the invited address.
/// </summary>
public sealed record MyInvitationDto(Guid Id, Guid WorkspaceId, string WorkspaceName, string Role, string? InvitedBy, string Status, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt);

/// <summary>The secret of the invitation link, in the body so it is in no URL and no request log.</summary>
public sealed record InvitationSecretRequest(string? Secret);

/// <summary>The rates of invitations and of answers to them for one caller. Tests replace it.</summary>
public sealed record InvitationLimits(int PerMinute = 10, int ResponsesPerMinute = 20)
{
    public static readonly InvitationLimits Default = new();
}

/// <summary>
/// The window of opening, accepting and declining with a secret. Each attempt is a guess of
/// the secret, so one caller makes a bounded number of them in a minute.
/// </summary>
internal sealed class InvitationResponseRate(InvitationLimits limits) : CallerRate(limits.ResponsesPerMinute);

/// <summary>
/// The window of invitation creation. Each invitation is a row and an audit entry, so one
/// caller makes a bounded number of them in a minute.
/// </summary>
internal sealed class InvitationRate(InvitationLimits limits) : CallerRate(limits.PerMinute);

public static class InvitationRules
{
    public const int MaxEmailLength = 256;
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(14);

    /// <summary>
    /// The current time at the precision Postgres stores (microseconds), so an answer built
    /// from a new row equals the answer read back from that row.
    /// </summary>
    public static DateTimeOffset Now(TimeProvider time)
    {
        var now = time.GetUtcNow();
        return now.AddTicks(-(now.Ticks % TimeSpan.TicksPerMicrosecond));
    }
    /// <summary>Open invitations of one workspace. A workspace with more revokes some or waits for them to expire.</summary>
    public const int MaxPendingPerWorkspace = 100;

    internal static InvitationStatus StatusAt(InvitationEntity invitation, DateTimeOffset now) =>
        invitation.Status == InvitationStatus.Pending && invitation.ExpiresAt <= now ? InvitationStatus.Expired : invitation.Status;

    internal static InvitationDto ToDto(InvitationEntity i, DateTimeOffset now) =>
        new(i.Id, i.WorkspaceId, i.Email, MemberRoles.Canonical(i.Role), EnumText.Of(StatusAt(i, now)), i.CreatedAt, i.ExpiresAt);

    /// <summary>The invitation row, locked for the open transaction.</summary>
    internal static async Task<InvitationEntity?> LockAsync(AppDbContext db, Guid invitationId, CancellationToken ct) =>
        (await db.Invitations
            .FromSql($"SELECT * FROM workspaces.invitations WHERE id = {invitationId} FOR UPDATE")
            .ToListAsync(ct))
        .FirstOrDefault();

    public const int MaxSecretLength = 128;

    /// <summary>256 bits from the system's random number generator, base64url without padding.</summary>
    internal static string NewSecret() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    internal static byte[] Hash(string secret) => SHA256.HashData(Encoding.UTF8.GetBytes(secret));

    /// <summary>
    /// Whether <paramref name="secret"/> is the secret of the invitation. The hashes are
    /// compared in constant time. The secret has 256 bits, so a plain hash is enough: no
    /// guess finds it, and the hash in the database gives nothing to a reader of it.
    /// </summary>
    internal static bool SecretMatches(InvitationEntity invitation, string? secret)
    {
        var usable = secret is { Length: > 0 and <= MaxSecretLength };
        var given = Hash(usable ? secret! : "");
        var stored = invitation.SecretHash is { Length: 32 } hash ? hash : new byte[32];
        return CryptographicOperations.FixedTimeEquals(stored, given) & usable & invitation.SecretHash is { Length: 32 };
    }

    internal static IssuedInvitationDto Issued(InvitationEntity i, string secret, DateTimeOffset now) =>
        new(i.Id, i.WorkspaceId, i.Email, MemberRoles.Canonical(i.Role), EnumText.Of(StatusAt(i, now)), i.CreatedAt, i.ExpiresAt,
            secret, $"/invitations/{i.Id}#secret={secret}");

    /// <summary>
    /// Revokes the open invitations that <paramref name="issuerId"/> issued in the workspace,
    /// because the issuer stops being an owner. An invitation carries the authority of the
    /// owner who issued it, so it does not outlive that authority. The rows are locked: an
    /// accept that holds one finishes first, and its invitation is then no longer pending.
    /// The caller has the transaction open, so the revocations and their audit entries commit
    /// with the membership change.
    /// </summary>
    internal static async Task RevokeIssuedByAsync(
        AppDbContext db, Guid workspaceId, string issuerId, string actorId, string reason, DateTimeOffset now, CancellationToken ct)
    {
        var pending = InvitationStatus.Pending;
        var invitations = await db.Invitations
            .FromSql($"SELECT * FROM workspaces.invitations WHERE workspace_id = {workspaceId} AND invited_by = {issuerId} AND status = {pending} ORDER BY id FOR UPDATE")
            .ToListAsync(ct);
        foreach (var invitation in invitations)
        {
            invitation.Status = InvitationStatus.Revoked;
            invitation.RespondedBy = actorId;
            invitation.RespondedAt = now;
        }
        await db.SaveChangesAsync(ct);
        foreach (var invitation in invitations)
            await AuditLog.AppendAsync(db, workspaceId, actorId, AuditLog.InvitationRevoked,
                $"invitation:{invitation.Id} role:{invitation.Role.ToString().ToLowerInvariant()} issuer:{issuerId} reason:{reason}", ct);
    }

    internal static IResult Closed(InvitationStatus status) => Results.Conflict(new ErrorResponse(
        "invitation_closed",
        $"The invitation is {EnumText.Of(status)}",
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
        AppDbContext db,
        WorkspaceAuthorizationService auth,
        ILookupNormalizer normalizer,
        TimeProvider time,
        CancellationToken ct)
    {
        var userId = user.UserId();
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
        if (await db.Users.AnyAsync(u => memberIds.Contains(u.Id) && u.NormalizedEmail == normalized, ct))
            return Results.Conflict(new ErrorResponse("already_member", "This person is already a member",
                "Change the role in the member list."));

        var now = InvitationRules.Now(time);
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

        // A second invitation of the same address renews the open one: the new role, a new
        // expiry and a new secret. The link with the old secret stops working.
        var secret = InvitationRules.NewSecret();
        invitation.SecretHash = InvitationRules.Hash(secret);
        invitation.Email = email;
        invitation.Role = role;
        invitation.InvitedBy = userId;
        invitation.ExpiresAt = now + InvitationRules.Lifetime;
        await db.SaveChangesAsync(ct);
        await AuditLog.AppendAsync(db, workspaceId, userId, AuditLog.MemberInvited,
            $"invitation:{invitation.Id} role:{invitation.Role.ToString().ToLowerInvariant()}", ct);
        await transaction.CommitAsync(ct);

        var dto = InvitationRules.Issued(invitation, secret, now);
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
        AppDbContext db,
        WorkspaceAuthorizationService auth,
        TimeProvider time,
        CancellationToken ct)
    {
        var userId = user.UserId();
        await auth.RequireRoleAsync(workspaceId, userId, WorkspaceRole.Owner, ct);

        var now = InvitationRules.Now(time);
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
        AppDbContext db,
        WorkspaceAuthorizationService auth,
        TimeProvider time,
        CancellationToken ct)
    {
        var userId = user.UserId();
        await auth.RequireRoleAsync(workspaceId, userId, WorkspaceRole.Owner, ct);

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var invitation = await InvitationRules.LockAsync(db, invitationId, ct);
        // An invitation of another workspace is no invitation of this one.
        if (invitation is null || invitation.WorkspaceId != workspaceId)
            return NotFound("Invitation");

        var now = InvitationRules.Now(time);
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
            $"invitation:{invitation.Id} role:{invitation.Role.ToString().ToLowerInvariant()}", ct);
        await transaction.CommitAsync(ct);
        return Results.NoContent();
    }
}

/// <summary>
/// The invitations of the signed-in person: those for the email address of the account.
/// Vellum sends no email, so the owner sends the link with its secret. The list shows that
/// an invitation exists; opening, accepting and declining need the secret of the link and
/// the signed-in account of the invited address. A wrong or missing secret, another
/// account and an unknown ID all get the same 404, so no answer tells which part was wrong.
/// </summary>
public static class MyInvitations
{
    public static async Task<IResult> List(
        ClaimsPrincipal user,
        AppDbContext db,
        TimeProvider time,
        CancellationToken ct)
    {
        var email = await EmailOfAsync(user, db, ct);
        if (email is null)
            return Results.Ok(Array.Empty<MyInvitationDto>());

        var now = InvitationRules.Now(time);
        var rows = await db.Invitations.AsNoTracking()
            .Where(i => i.NormalizedEmail == email && i.Status == InvitationStatus.Pending && i.ExpiresAt > now)
            .Join(db.Workspaces, i => i.WorkspaceId, w => w.Id, (i, w) => new { Invitation = i, WorkspaceName = w.Name })
            .OrderBy(r => r.Invitation.CreatedAt)
            .ToListAsync(ct);
        return Results.Ok(await ToDtosAsync(rows.Select(r => (r.Invitation, r.WorkspaceName)).ToList(), db, now, ct));
    }

    /// <summary>The invitation of a link, for the screen that offers Accept and Decline. It changes nothing.</summary>
    public static async Task<IResult> Open(
        Guid invitationId,
        InvitationSecretRequest? request,
        ClaimsPrincipal user,
        AppDbContext db,
        TimeProvider time,
        CancellationToken ct)
    {
        var email = await EmailOfAsync(user, db, ct);
        var invitation = await db.Invitations.AsNoTracking().FirstOrDefaultAsync(i => i.Id == invitationId, ct);
        if (invitation is null || !IsFor(invitation, email, request?.Secret))
            return NotFound("Invitation");

        var workspaceName = await db.Workspaces.Where(w => w.Id == invitation.WorkspaceId).Select(w => w.Name).FirstAsync(ct);
        var dtos = await ToDtosAsync([(invitation, workspaceName)], db, time.GetUtcNow(), ct);
        return Results.Ok(dtos[0]);
    }

    /// <summary>
    /// Makes the membership with the invited role. A second accept of the same invitation by
    /// the same person answers the same membership. A person who is a member already keeps
    /// the role they have: an owner changes a role in the member list.
    /// </summary>
    public static async Task<IResult> Accept(
        Guid invitationId,
        InvitationSecretRequest? request,
        ClaimsPrincipal user,
        AppDbContext db,
        TimeProvider time,
        CancellationToken ct)
    {
        var userId = user.UserId();
        var email = await EmailOfAsync(user, db, ct);

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var invitation = await InvitationRules.LockAsync(db, invitationId, ct);
        if (invitation is null || !IsFor(invitation, email, request?.Secret))
            return NotFound("Invitation");

        var membership = await db.Memberships
            .FirstOrDefaultAsync(m => m.WorkspaceId == invitation.WorkspaceId && m.UserId == userId, ct);
        var now = InvitationRules.Now(time);
        var status = InvitationRules.StatusAt(invitation, now);
        if (status == InvitationStatus.Accepted && invitation.RespondedBy == userId && membership is not null)
            return Results.Ok(ToDto(membership));
        if (status != InvitationStatus.Pending)
            return InvitationRules.Closed(status);
        // An owner who is demoted or removed revokes what they issued (RevokeIssuedByAsync).
        // This check holds when the membership changed by another path.
        var issuerRole = await db.Memberships
            .Where(m => m.WorkspaceId == invitation.WorkspaceId && m.UserId == invitation.InvitedBy)
            .Select(m => (WorkspaceRole?)m.Role).FirstOrDefaultAsync(ct);
        if (issuerRole != WorkspaceRole.Owner)
            return InvitationRules.Closed(InvitationStatus.Revoked);

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

    /// <summary>
    /// Closes the invitation. It needs the secret as an accept does: an account that only has
    /// the address (one that registered with an address it does not own) can then neither
    /// accept nor close the invitation of the person who got the link.
    /// </summary>
    public static async Task<IResult> Decline(
        Guid invitationId,
        InvitationSecretRequest? request,
        ClaimsPrincipal user,
        AppDbContext db,
        TimeProvider time,
        CancellationToken ct)
    {
        var userId = user.UserId();
        var email = await EmailOfAsync(user, db, ct);

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var invitation = await InvitationRules.LockAsync(db, invitationId, ct);
        if (invitation is null || !IsFor(invitation, email, request?.Secret))
            return NotFound("Invitation");

        var now = InvitationRules.Now(time);
        var status = InvitationRules.StatusAt(invitation, now);
        if (status == InvitationStatus.Declined)
            return Results.NoContent();
        if (status != InvitationStatus.Pending)
            return InvitationRules.Closed(status);

        invitation.Status = InvitationStatus.Declined;
        invitation.RespondedBy = userId;
        invitation.RespondedAt = now;
        await db.SaveChangesAsync(ct);
        await AuditLog.AppendAsync(db, invitation.WorkspaceId, userId, AuditLog.InvitationDeclined,
            $"invitation:{invitation.Id} role:{invitation.Role.ToString().ToLowerInvariant()}", ct);
        await transaction.CommitAsync(ct);
        return Results.NoContent();
    }

    /// <summary>Both checks run whatever the other gives, so the time of the answer does not tell which one failed.</summary>
    private static bool IsFor(InvitationEntity invitation, string? email, string? secret) =>
        InvitationRules.SecretMatches(invitation, secret) & (email is not null && invitation.NormalizedEmail == email);

    private static async Task<List<MyInvitationDto>> ToDtosAsync(
        List<(InvitationEntity Invitation, string WorkspaceName)> rows, AppDbContext identity, DateTimeOffset now, CancellationToken ct)
    {
        var inviterIds = rows.Select(r => r.Invitation.InvitedBy).Distinct().ToList();
        var inviters = await identity.Users.AsNoTracking()
            .Where(u => inviterIds.Contains(u.Id))
            .Select(u => new { u.Id, Name = u.DisplayName })
            .ToDictionaryAsync(u => u.Id, u => u.Name, ct);
        return rows.Select(r => new MyInvitationDto(
            r.Invitation.Id, r.Invitation.WorkspaceId, r.WorkspaceName, MemberRoles.Canonical(r.Invitation.Role),
            inviters.GetValueOrDefault(r.Invitation.InvitedBy), EnumText.Of(InvitationRules.StatusAt(r.Invitation, now)),
            r.Invitation.CreatedAt, r.Invitation.ExpiresAt)).ToList();
    }

    /// <summary>The normalized email address of the signed-in account, read from the account and not from a claim.</summary>
    private static async Task<string?> EmailOfAsync(ClaimsPrincipal user, AppDbContext identity, CancellationToken ct)
    {
        var userId = user.UserId();
        return await identity.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => u.NormalizedEmail).FirstOrDefaultAsync(ct);
    }

    private static MembershipDto ToDto(MembershipEntity m) =>
        new(m.Id, m.WorkspaceId, m.UserId, MemberRoles.Canonical(m.Role), m.CreatedAt);
}
