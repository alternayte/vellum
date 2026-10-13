using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Vellum.Modules.Workspaces.Authorization;

namespace Vellum.Modules.Settings.Audit;

/// <summary>
/// The record of who changed a sensitive setting. An entry names the thing that changed and
/// never holds a value. A null workspace marks an instance-level entry.
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

    /// <summary>
    /// Writes on the connection of <paramref name="db"/>. A caller that opened a transaction
    /// on that context gets the entry in the same transaction as the change it records.
    /// </summary>
    public static Task AppendAsync(
        DbContext db, Guid? workspaceId, string actorId, string action, string target, CancellationToken ct) =>
        db.Database.ExecuteSqlAsync(
            $"INSERT INTO settings.audit_entries (workspace_id, actor_id, action, target) VALUES ({workspaceId}, {actorId}, {action}, {target})",
            ct);
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
        SettingsDbContext db,
        WorkspaceAuthorizationService auth,
        CancellationToken ct)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier)!;
        await auth.RequireRoleAsync(workspaceId, userId, WorkspaceRole.Owner, ct);

        // A key rotation touches every workspace's secrets, so each workspace sees it.
        var entries = await db.AuditEntries.AsNoTracking()
            .Where(a => a.WorkspaceId == workspaceId || (a.WorkspaceId == null && a.Action == AuditLog.KeysRotated))
            .OrderByDescending(a => a.Id)
            .Take(Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit))
            .Select(a => new AuditEntryDto(a.At, a.ActorId, a.Action, a.Target))
            .ToListAsync(ct);

        return Results.Ok(entries);
    }
}
