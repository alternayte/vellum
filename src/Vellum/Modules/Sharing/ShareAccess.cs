using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Vellum.Modules.Settings;
using Vellum.Shared;

namespace Vellum.Modules.Sharing;

/// <summary>
/// The token of a share link: 32 random bytes in base64url, 43 characters. Only its SHA-256
/// is stored, so a copy of the database opens no link. The token has the strength of a key,
/// so one unsalted hash is enough to look it up.
/// </summary>
public static class ShareToken
{
    public const int Length = 43;

    public static string New() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    /// <summary>True when <paramref name="token"/> has the form of a token. Anything else is never looked up.</summary>
    public static bool HasForm(string? token) =>
        token is { Length: Length } && token.All(c => c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '_');

    public static byte[] Hash(string token) => SHA256.HashData(Encoding.ASCII.GetBytes(token));
}

/// <summary>
/// The proof that a visitor typed the password of a link. It is no cookie: the page keeps it
/// in memory and sends it in a header, so it works inside the frame of another site, where a
/// browser holds back cookies, and it changes nothing of the app's own session cookie. It is
/// signed and encrypted by Data Protection and names the link and the version of its
/// password: a new password, a revocation or the end of its lifetime ends it.
/// </summary>
public sealed class ShareGrants(IDataProtectionProvider protection, TimeProvider time)
{
    public const string Header = "X-Vellum-Share-Grant";
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(12);

    private readonly ITimeLimitedDataProtector _protector =
        protection.CreateProtector("Vellum.Sharing.Grant.v1").ToTimeLimitedDataProtector();

    public (string Grant, DateTimeOffset ExpiresAt) Issue(ShareLinkEntity link)
    {
        var expires = time.GetUtcNow() + Lifetime;
        return (_protector.Protect($"{link.Id:N}.{link.PasswordVersion}", expires), expires);
    }

    public bool Admits(HttpRequest request, ShareLinkEntity link)
    {
        var grant = request.Headers[Header].ToString();
        if (grant.Length is 0 or > 1024)
            return false;
        try
        {
            var payload = _protector.Unprotect(grant, out var expiration);
            return expiration > time.GetUtcNow() && payload == $"{link.Id:N}.{link.PasswordVersion}";
        }
        catch (CryptographicException)
        {
            return false;
        }
    }
}

/// <summary>
/// The guess limit of a link password. Each attempt takes a place in the window of the link
/// before the password is checked, and a right password gives its place back, so wrong
/// guesses from every address together stay under <see cref="MaxFailures"/> in
/// <see cref="Window"/>. The count is in the database, so every instance of the app shares it.
/// The per-address limit of credential endpoints applies as well.
/// </summary>
public static class SharePasswordLimit
{
    public const int MaxFailures = 10;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(15);

    /// <summary>Takes a place in the window. Null when the window is full: then the seconds until it ends.</summary>
    public static async Task<int?> TryTakeAsync(AppDbContext db, Guid linkId, DateTimeOffset now, CancellationToken ct)
    {
        var windowStart = now - Window;
        var taken = await db.Database.ExecuteSqlAsync($"""
            UPDATE sharing.share_links
            SET failed_attempts = CASE WHEN attempt_window_start IS NULL OR attempt_window_start <= {windowStart} THEN 1 ELSE failed_attempts + 1 END,
                attempt_window_start = CASE WHEN attempt_window_start IS NULL OR attempt_window_start <= {windowStart} THEN {now} ELSE attempt_window_start END
            WHERE id = {linkId}
              AND (attempt_window_start IS NULL OR attempt_window_start <= {windowStart} OR failed_attempts < {MaxFailures})
            """, ct);
        if (taken == 1)
            return null;

        var start = await db.Set<ShareLinkEntity>().AsNoTracking().Where(l => l.Id == linkId).Select(l => l.AttemptWindowStart).FirstOrDefaultAsync(ct);
        var wait = (start ?? now) + Window - now;
        return Math.Max(1, (int)Math.Ceiling(wait.TotalSeconds));
    }

    public static Task GiveBackAsync(AppDbContext db, Guid linkId, CancellationToken ct) =>
        db.Database.ExecuteSqlAsync(
            $"UPDATE sharing.share_links SET failed_attempts = greatest(failed_attempts - 1, 0) WHERE id = {linkId}", ct);
}

/// <summary>Whether the workspace policy lets a link answer, now.</summary>
public static class ShareLinkPolicy
{
    /// <summary>
    /// A link with no password under a policy of password-only answers 404, as a link does
    /// under the policy <c>off</c>. It does not ask for a password that it never had.
    /// </summary>
    public static bool Allows(string policy, bool hasPassword) => policy switch
    {
        SharingSettings.Public => true,
        SharingSettings.PasswordOnly => hasPassword,
        _ => false,
    };

    /// <summary>Why a link with this protection cannot be made or kept under the policy, or null.</summary>
    public static string? Refusal(string policy, bool hasPassword) => policy switch
    {
        SharingSettings.Off => "The workspace link policy is off. An Owner can change it in the workspace settings.",
        SharingSettings.PasswordOnly when !hasPassword => "The workspace link policy allows links with a password only.",
        _ => null,
    };
}

/// <summary>The link of a token, as a visitor may use it now.</summary>
public sealed class ShareLinkResolver(AppDbContext db, SettingsResolver settings, TimeProvider time)
{
    /// <summary>
    /// The link and the effective sharing settings of its workspace, or null when the token
    /// opens nothing: no such link, revoked, expired, a project that is no longer in the
    /// workspace of the link, or a workspace policy that does not allow it. Each gives the
    /// same null, so a visitor learns nothing of why.
    /// </summary>
    public async Task<(ShareLinkEntity Link, SharingSettings Sharing)?> ResolveAsync(string? token, CancellationToken ct)
    {
        if (!ShareToken.HasForm(token))
            return null;

        var hash = ShareToken.Hash(token!);
        var link = await db.Set<ShareLinkEntity>().AsNoTracking().FirstOrDefaultAsync(l => l.TokenHash == hash, ct);
        if (link is null || link.RevokedAt is not null || link.ExpiresAt <= time.GetUtcNow())
            return null;

        // Tenant scope: the project is read through the workspace the link was made in.
        if (!await db.Projects.AsNoTracking().AnyAsync(p => p.Id == link.ProjectId && p.WorkspaceId == link.WorkspaceId, ct))
            return null;
        if (link.ViewId is { } viewId && !await db.Views.AsNoTracking().AnyAsync(v => v.Id == viewId && v.ProjectId == link.ProjectId, ct)
            && link.Mode == ShareMode.Live)
            return null;

        var sharing = (await settings.GetWorkspaceAsync(link.WorkspaceId, ct)).Effective.Sharing;
        return ShareLinkPolicy.Allows(sharing.LinkPolicy, link.PasswordHash is not null) ? (link, sharing) : null;
    }
}
