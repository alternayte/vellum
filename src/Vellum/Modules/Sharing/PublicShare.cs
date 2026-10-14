using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Vellum.Modules.Settings;
using Microsoft.Extensions.Options;
using Vellum.Modules.Build;
using Vellum.Modules.Promises;
using Vellum.Shared;

namespace Vellum.Modules.Sharing;

public sealed record UnlockShareRequest(string? Password);

/// <param name="Grant">Send it in the header <c>X-Vellum-Share-Grant</c> with each read of the link.</param>
public sealed record ShareGrantDto(string Grant, DateTimeOffset ExpiresAt);

/// <summary>
/// The read API of a share link, for a visitor with no account. It is a route of its own
/// (<c>/api/share/{token}</c>): it resolves the token, reads the page through
/// <see cref="SharePageReader"/> and answers with the share's own types. It calls no member
/// endpoint, and the identity of a signed-in caller plays no part.
/// </summary>
public static class PublicShare
{
    /// <summary>One answer for every token that opens nothing, whatever the reason.</summary>
    public static IResult Gone() => Results.NotFound(new ErrorResponse("not_found", "This link does not exist or no longer works"));

    public static async Task<IResult> Read(
        string token, HttpContext http, ShareLinkResolver resolver, ShareGrants grants, SharePageReader reader,
        AppDbContext db, IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions> json, TimeProvider time, CancellationToken ct)
    {
        http.Response.Headers["X-Robots-Tag"] = "noindex, nofollow";
        if (await resolver.ResolveAsync(token, ct) is not var (link, _))
            return Gone();

        if (link.PasswordHash is not null && !grants.Admits(http.Request, link))
        {
            return Results.Json(
                new ErrorResponse("password_required", "This link needs a password", "Type the password that the person who shared it gave you."),
                statusCode: StatusCodes.Status401Unauthorized);
        }

        SharedPage? page;
        if (link.Mode == ShareMode.Pinned)
        {
            var stored = await db.Set<ShareSnapshotEntity>().FindAsync([link.Id], ct);
            page = stored is null ? null : SharePageReader.Deserialize(stored.Content, json.Value.SerializerOptions).For(link);
        }
        else
        {
            var (model, _) = await PinnedModel.ResolveAsync(http.RequestServices, link.ProjectId, null, null, ct);
            if (model is null)
                return Gone();
            try
            {
                var whole = await reader.ReadAsync(new SharePageRequest(
                    link.ProjectId, model, null, time.GetUtcNow(), link.ShowCosts, link.ShowPromises,
                    WithProjectParts: link.Scope == ShareScope.Project,
                    ViewId: link.Scope == ShareScope.View ? link.ViewId : null), ct);
                page = whole.For(link);
            }
            catch (BundleTooLargeException)
            {
                return Results.Json(new ErrorResponse("too_large", "This project is too large to show here"),
                    statusCode: StatusCodes.Status422UnprocessableEntity);
            }
        }

        if (page is null)
            return Gone();

        await LastAccess.TouchAsync(db, link.Id, time.GetUtcNow(), ct);
        return Results.Ok(page);
    }

    public static async Task<IResult> Unlock(
        string token, UnlockShareRequest request, HttpContext http, ShareLinkResolver resolver, ShareGrants grants,
        IPasswordHasher<ShareLinkEntity> hasher, AppDbContext db, TimeProvider time, CancellationToken ct)
    {
        http.Response.Headers["X-Robots-Tag"] = "noindex, nofollow";
        if (await resolver.ResolveAsync(token, ct) is not var (link, _) || link.PasswordHash is null)
            return Gone();

        if (await SharePasswordLimit.TryTakeAsync(db, link.Id, time.GetUtcNow(), ct) is { } wait)
            return CallerRate.Refusal(http, wait, $"Too many wrong passwords for this link. Wait {wait} seconds and try again.");

        var password = request.Password ?? "";
        if (password.Length is 0 or > SharePassword.MaxLength
            || hasher.VerifyHashedPassword(link, link.PasswordHash, password) == PasswordVerificationResult.Failed)
        {
            return Results.Json(new ErrorResponse("wrong_password", "The password is not right"), statusCode: StatusCodes.Status401Unauthorized);
        }

        await SharePasswordLimit.GiveBackAsync(db, link.Id, ct);
        var (grant, expires) = grants.Issue(link);
        return Results.Ok(new ShareGrantDto(grant, expires));
    }
}

/// <summary>The last time a link was read. At most one write a minute for each link.</summary>
public static class LastAccess
{
    public static readonly TimeSpan Resolution = TimeSpan.FromMinutes(1);

    public static Task TouchAsync(AppDbContext db, Guid linkId, DateTimeOffset now, CancellationToken ct)
    {
        var before = now - Resolution;
        return db.Database.ExecuteSqlAsync(
            $"UPDATE sharing.share_links SET last_accessed_at = {now} WHERE id = {linkId} AND (last_accessed_at IS NULL OR last_accessed_at < {before})",
            ct);
    }
}

/// <summary>
/// The frame rule of <c>/embed/{token}</c>, the only page another site may frame. For a view
/// link that a visitor may open now, the response allows the origins of the workspace's embed
/// allow-list and nothing else. Every other response keeps <c>frame-ancestors 'none'</c> and
/// <c>X-Frame-Options: DENY</c> from <see cref="RequestHardening"/>.
/// </summary>
public static class EmbedFraming
{
    public const string Prefix = "/embed";

    public static IApplicationBuilder UseEmbedFraming(this IApplicationBuilder app) => app.Use(async (http, next) =>
    {
        if ((HttpMethods.IsGet(http.Request.Method) || HttpMethods.IsHead(http.Request.Method))
            && http.Request.Path.StartsWithSegments(Prefix, out var rest)
            && rest.Value is { Length: > 1 } tail && tail.IndexOf('/', 1) < 0)
        {
            var headers = http.Response.Headers;
            headers["X-Robots-Tag"] = "noindex, nofollow";
            var resolved = await http.RequestServices.GetRequiredService<ShareLinkResolver>().ResolveAsync(tail[1..], http.RequestAborted);
            if (resolved is var (link, sharing) && link.Scope == ShareScope.View && sharing.EmbedHosts.Count > 0)
            {
                headers.ContentSecurityPolicy = FrameAncestors(sharing.EmbedHosts);
                headers.Remove("X-Frame-Options");
            }
        }

        await next(http);
    });

    /// <summary>
    /// The policy of the app with its <c>frame-ancestors</c> set to <paramref name="hosts"/>.
    /// A host that is not an origin of the checked form is left out, so a stored value can
    /// never add a directive.
    /// </summary>
    public static string FrameAncestors(IEnumerable<string> hosts)
    {
        var allowed = hosts.Where(h => EmbedOrigin.IsValid(h, httpsOnly: false)).Distinct(StringComparer.Ordinal).ToList();
        var ancestors = allowed.Count == 0 ? "'none'" : string.Join(' ', allowed);
        return RequestHardening.ContentSecurityPolicy.Replace("frame-ancestors 'none'", $"frame-ancestors {ancestors}", StringComparison.Ordinal);
    }
}
