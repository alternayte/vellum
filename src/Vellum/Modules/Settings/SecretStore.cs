using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Vellum.Modules.Settings.Crypto;
using Vellum.Shared;
using Vellum.Shared.Outbound;

namespace Vellum.Modules.Settings;

/// <summary>The names a workspace credential can have.</summary>
public static class SecretNames
{
    public const string GithubToken = "github:token";
    /// <summary>A read-only GitLab token. It goes to the origin of <c>gitlab.apiBaseUrl</c> only.</summary>
    public const string GitlabToken = "gitlab:token";
    /// <summary>A read-only Bitbucket Cloud access token, or <c>user:token</c> for HTTP basic. It goes to the origin of <c>bitbucket.apiBaseUrl</c> only.</summary>
    public const string BitbucketToken = "bitbucket:token";
    /// <summary>An Azure DevOps personal access token with Code (read). It goes to the origin of <c>azureDevops.apiBaseUrl</c> only.</summary>
    public const string AzureDevopsToken = "azure-devops:token";
    public const string WebhookSecret = "notifications:webhookSecret";
    /// <summary>The API key of the Google Cloud Billing Catalog API. It goes to <c>https://cloudbilling.googleapis.com</c> only.</summary>
    public const string GcpApiKey = "gcp:apiKey";

    public static string AiApiKey(Guid providerId) => $"ai:{providerId:D}:apiKey";

    /// <summary>The provider that an <c>ai:&lt;provider id&gt;:apiKey</c> name belongs to, or null for any other name.</summary>
    public static Guid? ProviderIdOf(string? name) =>
        name is not null
        && name.StartsWith("ai:", StringComparison.Ordinal)
        && name.EndsWith(":apiKey", StringComparison.Ordinal)
        && Guid.TryParseExact(name.AsSpan(3, Math.Max(0, name.Length - 10)), "D", out var providerId)
        && providerId != Guid.Empty
            ? providerId
            : null;

    /// <summary>
    /// The rule that <paramref name="value"/> breaks for the secret <paramref name="name"/>,
    /// or null when the value can be stored. A GitHub token and an AI API key travel in an
    /// HTTP header, so each must be one header value: printable ASCII and nothing else. A line
    /// break in such a value would add a header to the request. The same holds for the Google
    /// Cloud API key. A webhook signing secret is
    /// the key of a signature and never a header value; it can be any printable text, and a
    /// control character in it is refused because no real secret holds one.
    /// </summary>
    public static string? ValueRuleBrokenBy(string name, string value) =>
        name == WebhookSecret
            ? OutboundText.HasControlCharacter(value)
                ? "Must not contain a line break, a tab or another control character."
                : null
            : OutboundText.IsHeaderValue(value)
                ? null
                : "Must contain printable ASCII characters only: letters, digits, punctuation and spaces. This credential is sent in an HTTP header, which cannot hold a line break, another control character or a character outside ASCII.";

    /// <summary>Returns the stored form of an allowed name, or null for any other name.</summary>
    public static string? Normalize(string? name) =>
        name is GithubToken or GitlabToken or BitbucketToken or AzureDevopsToken or WebhookSecret or GcpApiKey ? name
        : ProviderIdOf(name) is { } providerId ? AiApiKey(providerId)
        : null;
}

/// <summary>A stored credential exists but cannot be decrypted. The message holds no value.</summary>
public sealed class SecretUnavailableException(Guid workspaceId, string name, Exception inner)
    : Exception($"The secret '{name}' of workspace {workspaceId} cannot be decrypted with the loaded master key.", inner);

/// <summary>What <see cref="SecretReader"/> answers.</summary>
public abstract record SecretRead
{
    private SecretRead() { }

    /// <summary>The credential, saved for the origin of the destination that the caller named.</summary>
    public sealed record Found(string Value) : SecretRead;

    /// <summary>The workspace has no such credential.</summary>
    public sealed record NotSet : SecretRead;

    /// <summary>
    /// The credential was saved for another origin, so it must not go to the destination.
    /// <paramref name="BoundOrigin"/> is null for a credential that was saved before origins
    /// were recorded. An owner enters the value again to bind it to the new origin.
    /// </summary>
    public sealed record SavedForAnotherHost(string? BoundOrigin, string RequestedOrigin) : SecretRead;
}

/// <summary>
/// Seals and opens one credential. The encrypted row is bound to its workspace, its name, the
/// origin it was saved for and its fingerprint: a row that a database writer copies or edits
/// in any of these does not unwrap.
/// </summary>
internal static class SecretEnvelope
{
    public const int MaxValueLength = 4096;

    // Rows from before origins were recorded. They rotate and they are never read out.
    private static byte[] LegacyAssociatedData(Guid workspaceId, string name) =>
        Encoding.UTF8.GetBytes($"vellum.secret.v1|{workspaceId:N}|{name}");

    private static byte[] AssociatedData(Guid workspaceId, string name, string origin, string fingerprint) =>
        Encoding.UTF8.GetBytes($"vellum.secret.v2|{workspaceId:N}|{name}|{origin}|{fingerprint}");

    private static byte[] AssociatedData(SecretEntity row) => row.BoundOrigin is { } origin
        ? AssociatedData(row.WorkspaceId, row.Name, origin, row.Fingerprint)
        : LegacyAssociatedData(row.WorkspaceId, row.Name);

    /// <summary>
    /// Lets a user tell two values apart without showing either: the first 8 hex digits of an
    /// HMAC-SHA-256 under a key that is derived from the current master key for the workspace.
    /// Equal values in two workspaces have different fingerprints, and a reader of the
    /// database cannot test a guess. It changes when the master key changes.
    /// </summary>
    public static string Fingerprint(MasterKeyRing keys, Guid workspaceId, ReadOnlySpan<byte> value) =>
        Convert.ToHexStringLower(keys.Current.Digest(
            Encoding.UTF8.GetBytes($"vellum.secret-fingerprint.v1|{workspaceId:N}"), value))[..8];

    /// <summary>Writes a new sealed value for <paramref name="origin"/> into <paramref name="row"/>.</summary>
    public static void Seal(MasterKeyRing keys, SecretEntity row, string origin, ReadOnlySpan<byte> value)
    {
        var fingerprint = Fingerprint(keys, row.WorkspaceId, value);
        var sealedValue = keys.Seal(value, AssociatedData(row.WorkspaceId, row.Name, origin, fingerprint));
        row.BoundOrigin = origin;
        row.Fingerprint = fingerprint;
        row.MasterKeyId = sealedValue.MasterKeyId;
        row.WrapNonce = sealedValue.WrapNonce;
        row.WrappedKey = sealedValue.WrappedKey;
        row.Nonce = sealedValue.Nonce;
        row.Ciphertext = sealedValue.Ciphertext;
    }

    /// <summary>The value as UTF-8 bytes. The caller clears them.</summary>
    public static byte[] Open(MasterKeyRing keys, SecretEntity row) => keys.Open(ToSealed(row), AssociatedData(row));

    /// <summary>
    /// True when the stored origin and fingerprint are the ones that were sealed. The value
    /// is not decrypted. False for a row from before origins were recorded, for a changed row
    /// and for a row under a key that is not loaded.
    /// </summary>
    public static bool IsAuthentic(MasterKeyRing keys, SecretEntity row)
    {
        if (row.BoundOrigin is null)
            return false;
        try
        {
            keys.Verify(ToSealed(row), AssociatedData(row));
            return true;
        }
        catch (Exception ex) when (ex is CryptographicException or MasterKeyUnavailableException)
        {
            return false;
        }
    }

    /// <summary>
    /// Moves the row under the current master key. The fingerprint depends on the master key,
    /// so the value is sealed again with its new fingerprint. A row from before origins were
    /// recorded keeps its form and gets a new wrap only.
    /// </summary>
    public static void Rotate(MasterKeyRing keys, SecretEntity row)
    {
        if (row.BoundOrigin is not { } origin)
        {
            var rewrapped = keys.Rewrap(ToSealed(row), AssociatedData(row));
            row.MasterKeyId = rewrapped.MasterKeyId;
            row.WrapNonce = rewrapped.WrapNonce;
            row.WrappedKey = rewrapped.WrappedKey;
            return;
        }

        var value = Open(keys, row);
        try
        {
            Seal(keys, row, origin, value);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(value);
        }
    }

    private static SealedValue ToSealed(SecretEntity row) =>
        new(row.MasterKeyId, row.WrapNonce, row.WrappedKey, row.Nonce, row.Ciphertext);
}

/// <summary>
/// The only way the app reads a stored credential. The caller names the URL that the
/// credential goes to and gets the value only when the credential was saved for the origin of
/// that URL. Callers pass the value to that destination and nowhere else: not to a log, a
/// response, an error or a prompt.
/// </summary>
public sealed class SecretReader(AppDbContext db, Bootstrap bootstrap)
{
    /// <param name="destination">The absolute URL of the request that carries the credential.</param>
    /// <exception cref="SecretUnavailableException">The row exists and does not decrypt.</exception>
    public async Task<SecretRead> ReadAsync(Guid workspaceId, string name, Uri destination, CancellationToken ct = default)
    {
        var requested = OutboundUrlPolicy.OriginOf(destination);
        var row = await db.Secrets.AsNoTracking()
            .FirstOrDefaultAsync(s => s.WorkspaceId == workspaceId && s.Name == name, ct);
        if (row is null)
            return new SecretRead.NotSet();
        // The stored origin is part of the associated data, so a database writer who changes
        // it makes the row fail to decrypt below; it cannot redirect the credential.
        if (!string.Equals(row.BoundOrigin, requested, StringComparison.Ordinal))
            return new SecretRead.SavedForAnotherHost(row.BoundOrigin, requested);

        byte[] value;
        try
        {
            value = SecretEnvelope.Open(bootstrap.Keys, row);
        }
        catch (Exception ex) when (ex is CryptographicException or MasterKeyUnavailableException)
        {
            throw new SecretUnavailableException(workspaceId, name, ex);
        }

        try
        {
            return new SecretRead.Found(Encoding.UTF8.GetString(value));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(value);
        }
    }
}
