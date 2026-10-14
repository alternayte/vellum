using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Vellum.Modules.Settings.Audit;
using Vellum.Modules.Settings.Document;
using Vellum.Modules.Settings.Entities;
using Vellum.Modules.Workspaces.Authorization;
using Vellum.Shared;

namespace Vellum.Modules.Settings.Secrets;

/// <summary>
/// The status of a credential. No endpoint returns the value. <paramref name="BoundOrigin"/>
/// is the origin (scheme, host, port) that the value was saved for.
/// <paramref name="MatchesCurrentUrl"/> is false when the settings now point at another
/// origin, when policy blocks the URL, or when the stored row cannot be verified; the
/// credential is then not sent, and an owner enters the value again.
/// </summary>
public sealed record SecretStatusDto(
    string Name,
    bool IsSet,
    DateTimeOffset? UpdatedAt,
    string? UpdatedBy,
    string? Fingerprint,
    string? BoundOrigin,
    bool MatchesCurrentUrl);

public sealed record SetSecretRequest(string? Value);

public static class ListSecrets
{
    public static async Task<IResult> Handle(
        Guid workspaceId,
        ClaimsPrincipal user,
        WorkspaceAuthorizationService auth,
        AppDbContext db,
        SettingsResolver settings,
        Bootstrap bootstrap,
        CancellationToken ct)
    {
        var userId = user.UserId();
        await auth.RequireRoleAsync(workspaceId, userId, WorkspaceRole.Owner, ct);

        var workspace = await settings.GetWorkspaceAsync(workspaceId, ct);
        var stored = (await db.Secrets.AsNoTracking().Where(s => s.WorkspaceId == workspaceId).ToListAsync(ct))
            .ToDictionary(s => s.Name);

        // Every credential the workspace's settings can use, set or not, then any other stored one.
        var names = workspace.Effective.Ai.Providers.Select(p => SecretNames.AiApiKey(p.Id))
            .Append(SecretNames.GithubToken)
            .Append(SecretNames.WebhookSecret)
            .Concat(stored.Keys.Order(StringComparer.Ordinal))
            .Distinct();

        return Results.Ok(names.Select(name =>
        {
            if (!stored.TryGetValue(name, out var row))
                return new SecretStatusDto(name, false, null, null, null, null, false);

            // The fingerprint and the origin are shown only when the master key vouches for them.
            var authentic = SecretEnvelope.IsAuthentic(bootstrap.Keys, row);
            var matches = authentic
                          && workspace.DestinationOf(name) is SettingsUrl.Usable destination
                          && destination.Origin == row.BoundOrigin;
            return new SecretStatusDto(name, true, row.UpdatedAt, row.UpdatedBy,
                authentic ? row.Fingerprint : null, authentic ? row.BoundOrigin : null, matches);
        }).ToList());
    }
}

public static class SetSecret
{
    /// <summary>A 4096-character value in JSON, with room for escapes.</summary>
    public const int MaxBodyBytes = 32 * 1024;

    public static async Task<IResult> Handle(
        Guid workspaceId,
        string name,
        SetSecretRequest request,
        ClaimsPrincipal user,
        WorkspaceAuthorizationService auth,
        AppDbContext db,
        SettingsResolver settings,
        Bootstrap bootstrap,
        CancellationToken ct)
    {
        var userId = user.UserId();
        await auth.RequireRoleAsync(workspaceId, userId, WorkspaceRole.Owner, ct);

        if (SecretNames.Normalize(name) is not { } secretName)
            return SecretErrors.UnknownName();
        if (request.Value is not { Length: >= 1 and <= SecretEnvelope.MaxValueLength } value)
            return Results.BadRequest(new ErrorResponse("validation_error", "Validation failed",
                Errors: [new FieldError("value", $"Must be 1 to {SecretEnvelope.MaxValueLength} characters.")]));
        // The message is a fixed sentence. It never repeats the value.
        if (SecretNames.ValueRuleBrokenBy(secretName, value) is { } rule)
            return Results.BadRequest(new ErrorResponse("validation_error", "Validation failed", Errors: [new FieldError("value", rule)]));

        // The settings cannot change between the read below and the write: a settings save
        // takes the same lock. The credential is bound to the origin that the settings name now.
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await SettingsLocks.WorkspaceAsync(db, workspaceId, ct);
        var workspace = await settings.GetWorkspaceAsync(workspaceId, ct);

        if (SecretNames.ProviderIdOf(secretName) is { } providerId && workspace.Effective.Ai.Providers.All(p => p.Id != providerId))
            return SecretErrors.Name("No AI provider with this id is in the settings of this workspace. Add the provider first.");

        switch (workspace.DestinationOf(secretName))
        {
            case SettingsUrl.BlockedByPolicy blocked:
                return SecretErrors.Name($"The instance policy blocks the URL in {blocked.Field}: {blocked.Message}");
            case SettingsUrl.Usable destination:
                var row = new SecretEntity { WorkspaceId = workspaceId, Name = secretName };
                var bytes = Encoding.UTF8.GetBytes(value);
                try
                {
                    SecretEnvelope.Seal(bootstrap.Keys, row, destination.Origin, bytes);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(bytes);
                }

                // One statement replaces every column of the row, so a key rotation that runs
                // beside it sees the old row or the new row and never a mix.
                await db.Database.ExecuteSqlAsync($"""
                    INSERT INTO settings.secrets
                        (workspace_id, name, master_key_id, wrap_nonce, wrapped_key, nonce, ciphertext, fingerprint, bound_origin, updated_at, updated_by)
                    VALUES
                        ({workspaceId}, {secretName}, {row.MasterKeyId}, {row.WrapNonce}, {row.WrappedKey},
                         {row.Nonce}, {row.Ciphertext}, {row.Fingerprint}, {row.BoundOrigin}, now(), {userId})
                    ON CONFLICT (workspace_id, name) DO UPDATE
                    SET master_key_id = excluded.master_key_id, wrap_nonce = excluded.wrap_nonce, wrapped_key = excluded.wrapped_key,
                        nonce = excluded.nonce, ciphertext = excluded.ciphertext, fingerprint = excluded.fingerprint,
                        bound_origin = excluded.bound_origin, updated_at = excluded.updated_at, updated_by = excluded.updated_by
                    """, ct);
                await AuditLog.AppendAsync(db, workspaceId, userId, AuditLog.SecretSet, secretName, ct);
                await transaction.CommitAsync(ct);
                return Results.NoContent();
            default:
                return SecretErrors.Name("The settings of this workspace have no URL for this secret. Set notifications.webhookUrl first.");
        }
    }
}

public static class DeleteSecret
{
    public static async Task<IResult> Handle(
        Guid workspaceId,
        string name,
        ClaimsPrincipal user,
        WorkspaceAuthorizationService auth,
        AppDbContext db,
        CancellationToken ct)
    {
        var userId = user.UserId();
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
    public static IResult UnknownName() =>
        Name($"Must be '{SecretNames.GithubToken}', '{SecretNames.WebhookSecret}' or 'ai:<provider id>:apiKey'.");

    public static IResult Name(string message) => Results.BadRequest(
        new ErrorResponse("validation_error", "Validation failed", Errors: [new FieldError("name", message)]));
}
