using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Vellum.Modules.Settings.Audit;
using Vellum.Modules.Settings.Document;
using Vellum.Modules.Workspaces.Authorization;
using Vellum.Shared;

namespace Vellum.Modules.Settings.Secrets;

/// <summary>The status of a credential. No endpoint returns the value.</summary>
public sealed record SecretStatusDto(string Name, bool IsSet, DateTimeOffset? UpdatedAt, string? UpdatedBy, string? Fingerprint);

public sealed record SetSecretRequest(string? Value);

public static class ListSecrets
{
    public static async Task<IResult> Handle(
        Guid workspaceId,
        ClaimsPrincipal user,
        WorkspaceAuthorizationService auth,
        SettingsDbContext db,
        SettingsResolver settings,
        CancellationToken ct)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier)!;
        await auth.RequireRoleAsync(workspaceId, userId, WorkspaceRole.Owner, ct);

        var stored = await db.Secrets.AsNoTracking()
            .Where(s => s.WorkspaceId == workspaceId)
            .Select(s => new SecretStatusDto(s.Name, true, s.UpdatedAt, s.UpdatedBy, s.Fingerprint))
            .ToDictionaryAsync(s => s.Name, ct);

        // Every credential the workspace's settings can use, set or not, then any other stored one.
        var workspace = await settings.GetWorkspaceAsync(workspaceId, ct);
        var names = workspace.Effective.Ai.Providers.Select(p => SecretNames.AiApiKey(p.Id))
            .Append(SecretNames.GithubToken)
            .Append(SecretNames.WebhookSecret)
            .Concat(stored.Keys.Order(StringComparer.Ordinal))
            .Distinct();

        return Results.Ok(names
            .Select(name => stored.GetValueOrDefault(name) ?? new SecretStatusDto(name, false, null, null, null))
            .ToList());
    }
}

public static class SetSecret
{
    public static async Task<IResult> Handle(
        Guid workspaceId,
        string name,
        SetSecretRequest request,
        ClaimsPrincipal user,
        WorkspaceAuthorizationService auth,
        SettingsDbContext db,
        Bootstrap bootstrap,
        CancellationToken ct)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier)!;
        await auth.RequireRoleAsync(workspaceId, userId, WorkspaceRole.Owner, ct);

        if (SecretNames.Normalize(name) is not { } secretName)
            return SecretErrors.UnknownName();
        if (request.Value is not { Length: >= 1 and <= SecretEnvelope.MaxValueLength } value)
            return Results.BadRequest(new ErrorResponse("validation_error", "Validation failed",
                Errors: [new FieldError("value", $"Must be 1 to {SecretEnvelope.MaxValueLength} characters.")]));

        var sealedValue = SecretEnvelope.Seal(bootstrap.Keys, workspaceId, secretName, value);
        var fingerprint = SecretEnvelope.Fingerprint(value);

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO settings.secrets
                (workspace_id, name, master_key_id, wrap_nonce, wrapped_key, nonce, ciphertext, fingerprint, updated_at, updated_by)
            VALUES
                ({workspaceId}, {secretName}, {sealedValue.MasterKeyId}, {sealedValue.WrapNonce}, {sealedValue.WrappedKey},
                 {sealedValue.Nonce}, {sealedValue.Ciphertext}, {fingerprint}, now(), {userId})
            ON CONFLICT (workspace_id, name) DO UPDATE
            SET master_key_id = excluded.master_key_id, wrap_nonce = excluded.wrap_nonce, wrapped_key = excluded.wrapped_key,
                nonce = excluded.nonce, ciphertext = excluded.ciphertext, fingerprint = excluded.fingerprint,
                updated_at = excluded.updated_at, updated_by = excluded.updated_by
            """, ct);
        await AuditLog.AppendAsync(db, workspaceId, userId, AuditLog.SecretSet, secretName, ct);
        await transaction.CommitAsync(ct);

        return Results.NoContent();
    }
}

public static class DeleteSecret
{
    public static async Task<IResult> Handle(
        Guid workspaceId,
        string name,
        ClaimsPrincipal user,
        WorkspaceAuthorizationService auth,
        SettingsDbContext db,
        CancellationToken ct)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier)!;
        await auth.RequireRoleAsync(workspaceId, userId, WorkspaceRole.Owner, ct);

        if (SecretNames.Normalize(name) is not { } secretName)
            return SecretErrors.UnknownName();

        // Deleting a secret that is not set succeeds and records nothing.
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var deleted = await db.Secrets
            .Where(s => s.WorkspaceId == workspaceId && s.Name == secretName)
            .ExecuteDeleteAsync(ct);
        if (deleted > 0)
            await AuditLog.AppendAsync(db, workspaceId, userId, AuditLog.SecretDeleted, secretName, ct);
        await transaction.CommitAsync(ct);

        return Results.NoContent();
    }
}

internal static class SecretErrors
{
    public static IResult UnknownName() => Results.BadRequest(new ErrorResponse("validation_error", "Validation failed",
        Errors: [new FieldError("name",
            $"Must be '{SecretNames.GithubToken}', '{SecretNames.WebhookSecret}' or 'ai:<provider id>:apiKey'.")]));
}
