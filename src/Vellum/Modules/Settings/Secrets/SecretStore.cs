using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Vellum.Modules.Settings.Crypto;
using Vellum.Modules.Settings.Entities;

namespace Vellum.Modules.Settings.Secrets;

/// <summary>The names a workspace credential can have.</summary>
public static class SecretNames
{
    public const string GithubToken = "github:token";
    public const string WebhookSecret = "notifications:webhookSecret";

    public static string AiApiKey(Guid providerId) => $"ai:{providerId:D}:apiKey";

    /// <summary>Returns the stored form of an allowed name, or null for any other name.</summary>
    public static string? Normalize(string? name)
    {
        if (name is GithubToken or WebhookSecret)
            return name;

        if (name is not null
            && name.StartsWith("ai:", StringComparison.Ordinal)
            && name.EndsWith(":apiKey", StringComparison.Ordinal)
            && Guid.TryParseExact(name.AsSpan(3, Math.Max(0, name.Length - 10)), "D", out var providerId)
            && providerId != Guid.Empty)
            return AiApiKey(providerId);

        return null;
    }
}

/// <summary>A stored credential exists but cannot be decrypted. The message holds no value.</summary>
public sealed class SecretUnavailableException(Guid workspaceId, string name, Exception inner)
    : Exception($"The secret '{name}' of workspace {workspaceId} cannot be decrypted with the loaded master key.", inner);

/// <summary>Seals and opens one credential, bound to its workspace and name.</summary>
internal static class SecretEnvelope
{
    public const int MaxValueLength = 4096;

    // A row copied to another workspace or another name carries the wrong associated data
    // and does not decrypt.
    private static byte[] AssociatedData(Guid workspaceId, string name) =>
        Encoding.UTF8.GetBytes($"vellum.secret.v1|{workspaceId:N}|{name}");

    public static SealedValue Seal(MasterKeyRing keys, Guid workspaceId, string name, string value) =>
        keys.Seal(Encoding.UTF8.GetBytes(value), AssociatedData(workspaceId, name));

    public static string Open(MasterKeyRing keys, SecretEntity row) =>
        Encoding.UTF8.GetString(keys.Open(ToSealed(row), AssociatedData(row.WorkspaceId, row.Name)));

    public static void Rewrap(MasterKeyRing keys, SecretEntity row)
    {
        var rewrapped = keys.Rewrap(ToSealed(row), AssociatedData(row.WorkspaceId, row.Name));
        row.MasterKeyId = rewrapped.MasterKeyId;
        row.WrapNonce = rewrapped.WrapNonce;
        row.WrappedKey = rewrapped.WrappedKey;
    }

    /// <summary>Lets a user tell two keys apart without showing either.</summary>
    public static string Fingerprint(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..8];

    private static SealedValue ToSealed(SecretEntity row) =>
        new(row.MasterKeyId, row.WrapNonce, row.WrappedKey, row.Nonce, row.Ciphertext);
}

/// <summary>
/// The only way the app reads a stored credential. Callers pass the value to the service it
/// is for and nowhere else: not to a log, a response, an error or a prompt.
/// </summary>
public sealed class SecretReader(SettingsDbContext db, Bootstrap bootstrap)
{
    /// <returns>The decrypted value, or null when the workspace has no such secret.</returns>
    /// <exception cref="SecretUnavailableException">The row exists and does not decrypt.</exception>
    public async Task<string?> GetAsync(Guid workspaceId, string name, CancellationToken ct = default)
    {
        var row = await db.Secrets.AsNoTracking()
            .FirstOrDefaultAsync(s => s.WorkspaceId == workspaceId && s.Name == name, ct);
        if (row is null)
            return null;

        try
        {
            return SecretEnvelope.Open(bootstrap.Keys, row);
        }
        catch (Exception ex) when (ex is CryptographicException or MasterKeyUnavailableException)
        {
            throw new SecretUnavailableException(workspaceId, name, ex);
        }
    }
}
