using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Vellum.Modules.Settings.Audit;
using Vellum.Modules.Settings.Document;
using Vellum.Modules.Settings.Secrets;
using Vellum.Modules.Workspaces.Authorization;
using Vellum.Shared;

namespace Vellum.Modules.Settings.Workspace;

/// <summary>
/// <paramref name="Overrides"/> is a partial settings document: what this workspace set.
/// <paramref name="Effective"/> is what applies; <paramref name="Defaults"/> is what applies
/// where the workspace set nothing. <paramref name="PolicyViolations"/> lists each stored
/// setting that the instance policy refuses now; such a setting does not apply.
/// <paramref name="Version"/> is what the next <c>PUT</c> sends in <c>If-Match</c>.
/// </summary>
public sealed record WorkspaceSettingsResponse(
    SettingsDocument Effective,
    JsonObject Overrides,
    SettingsDocument Defaults,
    IReadOnlyList<PolicyViolation> PolicyViolations,
    long Version)
{
    public static WorkspaceSettingsResponse From(WorkspaceSettings settings) =>
        new(settings.Effective, settings.Overrides, settings.Defaults, settings.PolicyViolations, settings.Version);
}

public static class GetWorkspaceSettings
{
    public static async Task<IResult> Handle(
        Guid workspaceId,
        HttpResponse response,
        ClaimsPrincipal user,
        WorkspaceAuthorizationService auth,
        SettingsResolver settings,
        CancellationToken ct)
    {
        var userId = user.UserId();
        await auth.RequireRoleAsync(workspaceId, userId, WorkspaceRole.Viewer, ct);

        var workspace = await settings.GetWorkspaceAsync(workspaceId, ct);
        return SettingsVersion.Ok(response, workspace.Version, WorkspaceSettingsResponse.From(workspace));
    }
}

public static class UpdateWorkspaceSettings
{
    // The instance settings can change while a save is checked. The save is checked again
    // against the new ones; after this many changes in a row the caller tries again.
    private const int MaxAttempts = 3;

    public static async Task<IResult> Handle(
        Guid workspaceId,
        JsonElement overrides,
        HttpContext http,
        ClaimsPrincipal user,
        WorkspaceAuthorizationService auth,
        AppDbContext db,
        SettingsResolver settings,
        SettingsValidator validator,
        CancellationToken ct)
    {
        var userId = user.UserId();
        await auth.RequireRoleAsync(workspaceId, userId, WorkspaceRole.Owner, ct);

        if (SettingsVersion.ReadIfMatch(http.Request, out var refusal) is not { } expected)
            return refusal!;
        if (SettingsJson.ToDocument(overrides) is not { } overridesJson)
            return Invalid([new FieldError("", SettingsJson.NotADocument)]);
        if (SettingsJson.FindControlCharacter(overridesJson) is { } path)
            return Invalid([new FieldError(path, SettingsJson.HasControlCharacter)]);

        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            // The check resolves host names, so it runs before the transaction opens.
            var instance = await settings.GetInstanceAsync(ct);
            var merged = SettingsJson.Read(SettingsJson.Merge(instance.DefaultsJson, overridesJson), out var readError);
            if (merged is null)
                return Invalid([readError!]);

            var errors = await validator.ValidateAsync(merged, instance.Policy, ct);
            if (errors.Count > 0)
                return Invalid(errors);

            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            await SettingsLocks.InstanceSharedAsync(db, ct);
            // Under the lock the instance settings cannot change. If they changed since the
            // check above, the save is checked again against the new policy.
            if ((await settings.GetInstanceAsync(ct)).Version != instance.Version)
                continue;

            await SettingsLocks.WorkspaceAsync(db, workspaceId, ct);
            var before = await settings.GetWorkspaceAsync(workspaceId, ct);
            if (before.Version != expected)
                return SettingsVersion.Conflict(before.Version);

            var stored = overridesJson.ToJsonString();
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO settings.workspace_settings (workspace_id, overrides, version, updated_at, updated_by)
                VALUES ({workspaceId}, {stored}::jsonb, 1, now(), {userId})
                ON CONFLICT (workspace_id) DO UPDATE
                SET overrides = excluded.overrides, version = settings.workspace_settings.version + 1,
                    updated_at = excluded.updated_at, updated_by = excluded.updated_by
                """, ct);

            // A provider that is no longer in the settings takes its API key with it.
            var providers = merged.Ai.Providers.Select(p => SecretNames.AiApiKey(p.Id)).ToHashSet();
            var orphans = (await db.Secrets.AsNoTracking()
                    .Where(s => s.WorkspaceId == workspaceId && s.Name.StartsWith("ai:"))
                    .Select(s => s.Name)
                    .ToListAsync(ct))
                .Where(name => !providers.Contains(name))
                .Order(StringComparer.Ordinal)
                .ToList();
            foreach (var orphan in orphans)
            {
                await db.Secrets.Where(s => s.WorkspaceId == workspaceId && s.Name == orphan).ExecuteDeleteAsync(ct);
                await AuditLog.AppendAsync(db, workspaceId, userId, AuditLog.SecretDeleted, orphan, ct);
            }

            var after = settings.Build(merged, overridesJson, instance, before.Version + 1);
            await AuditLog.AppendAsync(db, workspaceId, userId, AuditLog.SettingsUpdated,
                AuditLog.ChangeTarget("workspace-settings",
                    SettingsJson.ChangedPaths(before.Overrides, overridesJson)), ct);
            await transaction.CommitAsync(ct);

            return SettingsVersion.Ok(http.Response, after.Version, WorkspaceSettingsResponse.From(after));
        }

        return Results.Json(
            new ErrorResponse("conflict", "The instance settings changed while this save ran", "Nothing was saved. Try again."),
            statusCode: StatusCodes.Status409Conflict);
    }

    private static IResult Invalid(IReadOnlyList<FieldError> errors) =>
        Results.BadRequest(new ErrorResponse("validation_error", "Validation failed", Errors: errors));
}
