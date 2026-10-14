using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Vellum.Modules.Settings.Instance;
using Vellum.Modules.Workspaces.Authorization;
using Vellum.Shared;

namespace Vellum.Modules.Settings.Audit;

/// <summary>
/// The record of who changed a sensitive setting. An entry names the thing that changed and
/// never holds a value, with two exceptions: a changed URL setting records its new origin,
/// and a price book entry records its price, which is no secret (see <c>PriceAudit</c>).
/// A null workspace marks an instance-level entry.
/// </summary>
public static class AuditLog
{
    public const string SettingsUpdated = "settings.updated";
    public const string InstanceSettingsUpdated = "instance.settings.updated";
    public const string InstanceSetup = "instance.setup";
    public const string SecretSet = "secret.set";
    public const string SecretDeleted = "secret.deleted";
    public const string KeysRotated = "keys.rotated";
    public const string MemberAdded = "member.added";
    public const string MemberRemoved = "member.removed";
    public const string MemberRoleChanged = "member.role-changed";
    /// <summary>The target names the invitation and the role, and never the email address.</summary>
    public const string MemberInvited = "member.invited";
    public const string InvitationRevoked = "member.invitation-revoked";
    /// <summary>An accept by a person who is a member already: the role stays as it is.</summary>
    public const string InvitationAccepted = "member.invitation-accepted";
    public const string InvitationDeclined = "member.invitation-declined";

    private const int MaxFieldsInTarget = 40;

    /// <summary>
    /// Writes on the connection of <paramref name="db"/>. A caller that opened a transaction
    /// on that context gets the entry in the same transaction as the change it records.
    /// </summary>
    public static Task AppendAsync(
        DbContext db, Guid? workspaceId, string actorId, string action, string target, CancellationToken ct) =>
        db.Database.ExecuteSqlAsync(
            $"INSERT INTO settings.audit_entries (workspace_id, actor_id, action, target) VALUES ({workspaceId}, {actorId}, {action}, {target})",
            ct);

    /// <summary>
    /// The target of a settings change: the document and the paths that changed, for example
    /// <c>workspace-settings fields:github.apiBaseUrl=https://ghe.example.com,import.maxFiles</c>.
    /// </summary>
    public static string ChangeTarget(string document, IReadOnlyList<string> changedPaths)
    {
        if (changedPaths.Count == 0)
            return $"{document} fields:none";
        var listed = string.Join(",", changedPaths.Take(MaxFieldsInTarget));
        return changedPaths.Count > MaxFieldsInTarget
            ? $"{document} fields:{listed},+{changedPaths.Count - MaxFieldsInTarget} more"
            : $"{document} fields:{listed}";
    }
}

public sealed record AuditEntryDto(DateTimeOffset At, string ActorId, string Action, string Target);

public static class ListAudit
{
    public const int DefaultLimit = 50;
    public const int MaxLimit = 200;

    public static async Task<IResult> Handle(
        Guid workspaceId,
        int? limit,
        ClaimsPrincipal user,
        AppDbContext db,
        WorkspaceAuthorizationService auth,
        CancellationToken ct)
    {
        var userId = user.UserId();
        await auth.RequireRoleAsync(workspaceId, userId, WorkspaceRole.Owner, ct);

        // A key rotation touches every workspace's secrets, so each workspace sees that it
        // happened. Who ran it and which master key it used are instance matters: a workspace
        // owner sees neither.
        var entries = await db.AuditEntries.AsNoTracking()
            .Where(a => a.WorkspaceId == workspaceId || (a.WorkspaceId == null && a.Action == AuditLog.KeysRotated))
            .OrderByDescending(a => a.Id)
            .Take(Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit))
            .Select(a => a.WorkspaceId == null
                ? new AuditEntryDto(a.At, "instance-admin", a.Action, "master-key")
                : new AuditEntryDto(a.At, a.ActorId, a.Action, a.Target))
            .ToListAsync(ct);

        return Results.Ok(entries);
    }
}

/// <summary>The instance-level entries in full, for the instance admin.</summary>
public static class ListInstanceAudit
{
    public static async Task<IResult> Handle(
        int? limit,
        ClaimsPrincipal user,
        AppDbContext db,
        InstanceAuthorization auth,
        CancellationToken ct)
    {
        await auth.RequireAdminAsync(user, ct);

        var entries = await db.AuditEntries.AsNoTracking()
            .Where(a => a.WorkspaceId == null)
            .OrderByDescending(a => a.Id)
            .Take(Math.Clamp(limit ?? ListAudit.DefaultLimit, 1, ListAudit.MaxLimit))
            .Select(a => new AuditEntryDto(a.At, a.ActorId, a.Action, a.Target))
            .ToListAsync(ct);

        return Results.Ok(entries);
    }
}
