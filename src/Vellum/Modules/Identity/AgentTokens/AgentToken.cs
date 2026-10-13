using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Net.Http.Headers;
using Vellum.Modules.Settings.Crypto;

namespace Vellum.Modules.Identity.AgentTokens;

/// <summary>
/// What an agent token may do. <see cref="Read"/> reads the model and the promises,
/// <see cref="Propose"/> writes drafts, obligations, runs and evidence, <see cref="Export"/>
/// uses the build endpoints. No scope approves, changes settings or changes members: those
/// are for a person (<see cref="AgentBoundary.RequireHuman{TBuilder}"/>).
/// </summary>
public enum AgentScope
{
    Read,
    Propose,
    Export,
}

public static class AgentScopes
{
    public static string Name(AgentScope scope) => scope switch
    {
        AgentScope.Read => "read",
        AgentScope.Propose => "propose",
        AgentScope.Export => "export",
        _ => throw new ArgumentOutOfRangeException(nameof(scope)),
    };

    public static bool TryParse(string? name, out AgentScope scope)
    {
        foreach (var candidate in Enum.GetValues<AgentScope>())
        {
            if (Name(candidate) == name)
            {
                scope = candidate;
                return true;
            }
        }

        scope = default;
        return false;
    }
}

/// <summary>The bounds of agent tokens. The API documentation states the same numbers.</summary>
public static class AgentTokenRules
{
    /// <summary>The lifetime of a token whose request names no expiry.</summary>
    public static readonly TimeSpan DefaultLifetime = TimeSpan.FromDays(90);

    /// <summary>The longest lifetime a request may ask for.</summary>
    public static readonly TimeSpan MaxLifetime = TimeSpan.FromDays(365);

    /// <summary>Tokens of one workspace that are neither revoked nor expired.</summary>
    public const int MaxLivePerWorkspace = 50;

    /// <summary>
    /// Revoked and expired tokens that a workspace keeps for its list. A create deletes the
    /// older ones; the audit log keeps their history.
    /// </summary>
    public const int MaxDeadKeptPerWorkspace = 50;

    public const int MaxNameLength = 100;

    /// <summary>The last-used time is written at most once in this interval for one token.</summary>
    public static readonly TimeSpan LastUsedInterval = TimeSpan.FromMinutes(1);

    /// <summary>A refused use is audited at most once in this interval for one token.</summary>
    public static readonly TimeSpan RefusedAuditInterval = TimeSpan.FromMinutes(1);
}

/// <summary>
/// One agent token. No column holds the token: <see cref="TokenHash"/> is an HMAC-SHA-256
/// under a key that is derived from the master key <see cref="MasterKeyId"/> names.
/// </summary>
public class AgentTokenEntity
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    /// <summary>The one project the token works on, or null for every project of the workspace.</summary>
    public Guid? ProjectId { get; set; }
    public string Name { get; set; } = null!;
    public string[] Scopes { get; set; } = [];
    public byte[] TokenHash { get; set; } = null!;
    public string MasterKeyId { get; set; } = null!;
    /// <summary>The user the token acts for. The token has no more access than this user's role gives.</summary>
    public string CreatedBy { get; set; } = null!;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }
    /// <summary>When a refused use was last written to the audit log.</summary>
    public DateTimeOffset? LastRefusedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public string? RevokedBy { get; set; }
}

/// <summary>
/// The text of a token: <c>vlm_agt_</c>, the token ID as 32 hex digits, <c>_</c>, and 256
/// random bits in base64url. The ID finds the row and is not secret; the random part is the
/// secret. The whole text is what the stored digest covers.
/// </summary>
internal static class AgentTokenSecret
{
    public const string Prefix = "vlm_agt_";
    private const string Bearer = "Bearer ";
    private const int IdLength = 32;
    private const int SecretBytes = 32;
    private const int SecretLength = 43;
    private const int Length = 8 + IdLength + 1 + SecretLength;

    private static readonly byte[] DigestContext = "vellum.agent-token.v1"u8.ToArray();

    public static string New(Guid tokenId)
    {
        Span<byte> secret = stackalloc byte[SecretBytes];
        RandomNumberGenerator.Fill(secret);
        try
        {
            return $"{Prefix}{tokenId:N}_{Base64Url.EncodeToString(secret)}";
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
        }
    }

    /// <summary>True for a request that presents a bearer credential, whatever its value.</summary>
    public static bool Presented(HttpRequest request) =>
        request.Headers.TryGetValue(HeaderNames.Authorization, out var values)
        && values.Any(v => v is not null && v.StartsWith(Bearer, StringComparison.OrdinalIgnoreCase));

    /// <summary>The bearer credential of the request, or null when it has none or more than one.</summary>
    public static string? Read(HttpRequest request) =>
        request.Headers.TryGetValue(HeaderNames.Authorization, out var values)
        && values is [{ } value]
        && value.StartsWith(Bearer, StringComparison.OrdinalIgnoreCase)
            ? value[Bearer.Length..].Trim()
            : null;

    public static bool TryParse(string token, out Guid tokenId)
    {
        tokenId = default;
        return token.Length == Length
            && token.StartsWith(Prefix, StringComparison.Ordinal)
            && token[Prefix.Length + IdLength] == '_'
            && Guid.TryParseExact(token.AsSpan(Prefix.Length, IdLength), "N", out tokenId);
    }

    /// <summary>
    /// The keyed digest of a token under the master key that <paramref name="masterKeyId"/>
    /// names, or null when that key is not loaded.
    /// </summary>
    public static byte[]? Digest(MasterKeyRing keys, string masterKeyId, string token) =>
        keys.Digest(masterKeyId, DigestContext, Encoding.UTF8.GetBytes(token));

    public static bool Matches(byte[] storedHash, byte[] digest) =>
        CryptographicOperations.FixedTimeEquals(storedHash, digest);
}
