using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Vellum.Modules.Settings.Audit;
using Vellum.Modules.Settings.Document;
using Vellum.Modules.Workspaces.Authorization;
using Vellum.Shared;

namespace Vellum.Modules.Settings.Workspace;

/// <summary>
/// <paramref name="Overrides"/> is a partial settings document: what this workspace set.
/// <paramref name="Effective"/> is what applies; <paramref name="Defaults"/> is what applies
/// where the workspace set nothing.
/// </summary>
public sealed record WorkspaceSettingsResponse(SettingsDocument Effective, JsonObject Overrides, SettingsDocument Defaults);

public static class GetWorkspaceSettings
{
    public static async Task<IResult> Handle(
        Guid workspaceId,
        ClaimsPrincipal user,
        WorkspaceAuthorizationService auth,
        SettingsResolver settings,
        CancellationToken ct)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier)!;
        await auth.RequireRoleAsync(workspaceId, userId, WorkspaceRole.Viewer, ct);

        var workspace = await settings.GetWorkspaceAsync(workspaceId, ct);
        return Results.Ok(new WorkspaceSettingsResponse(workspace.Effective, workspace.Overrides, workspace.Defaults));
    }
}

public static class UpdateWorkspaceSettings
{
    public static async Task<IResult> Handle(
        Guid workspaceId,
        JsonElement overrides,
        ClaimsPrincipal user,
        WorkspaceAuthorizationService auth,
        SettingsDbContext db,
        SettingsResolver settings,
        SettingsValidator validator,
        CancellationToken ct)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier)!;
        await auth.RequireRoleAsync(workspaceId, userId, WorkspaceRole.Owner, ct);

        if (SettingsJson.ToDocument(overrides) is not { } overridesJson)
            return Invalid([new FieldError("", SettingsJson.NotADocument)]);

        var instance = await settings.GetInstanceAsync(ct);
        var effective = SettingsJson.Read(SettingsJson.Merge(instance.DefaultsJson, overridesJson), out var readError);
        if (effective is null)
            return Invalid([readError!]);

        var errors = await validator.ValidateAsync(effective, instance.Policy, ct);
        if (errors.Count > 0)
            return Invalid(errors);

        var stored = overridesJson.ToJsonString();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO settings.workspace_settings (workspace_id, overrides, updated_at, updated_by)
            VALUES ({workspaceId}, {stored}::jsonb, now(), {userId})
            ON CONFLICT (workspace_id) DO UPDATE
            SET overrides = excluded.overrides, updated_at = excluded.updated_at, updated_by = excluded.updated_by
            """, ct);
        await AuditLog.AppendAsync(db, workspaceId, userId, AuditLog.SettingsUpdated, "workspace-settings", ct);
        await transaction.CommitAsync(ct);

        return Results.Ok(new WorkspaceSettingsResponse(
            SettingsResolver.ApplyPolicy(effective, instance.Policy), overridesJson, instance.Defaults));
    }

    private static IResult Invalid(IReadOnlyList<FieldError> errors) =>
        Results.BadRequest(new ErrorResponse("validation_error", "Validation failed", Errors: errors));
}
