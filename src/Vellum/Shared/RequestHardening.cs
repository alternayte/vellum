using System.Net;
using Microsoft.AspNetCore.HttpOverrides;
using Vellum.Modules.Identity.AgentTokens;
using Vellum.Modules.Settings;

namespace Vellum.Shared;

/// <summary>
/// What every response and every state-changing request of a browser gets, whatever the
/// endpoint: the security headers, the check that a write comes from this app's own pages,
/// and the address of the client when a proxy stands in front.
/// </summary>
public static class RequestHardening
{
    public const string CrossSiteError = "cross_site_request";
    public const string TrustedProxiesSetting = "Vellum:TrustedProxies";

    /// <summary>
    /// The built app loads scripts, styles, fonts and images from its own origin only. Styles
    /// need <c>'unsafe-inline'</c>: the canvas and the editor position their parts with style
    /// attributes. <c>'wasm-unsafe-eval'</c> lets the code highlighter compile its grammar
    /// engine; it allows no script text. No page may frame the app.
    /// </summary>
    public const string ContentSecurityPolicy =
        "default-src 'self'; script-src 'self' 'wasm-unsafe-eval'; style-src 'self' 'unsafe-inline'; img-src 'self' data: blob:; " +
        "font-src 'self' data:; connect-src 'self'; worker-src 'self' blob:; object-src 'none'; base-uri 'self'; " +
        "form-action 'self'; frame-ancestors 'none'";

    /// <summary>
    /// Adds the response headers. HSTS is sent by the hosted profile only, and only on a
    /// request that came over HTTPS: a local instance on plain HTTP must not pin a browser to
    /// HTTPS for its host name.
    /// </summary>
    public static IApplicationBuilder UseSecurityHeaders(this WebApplication app)
    {
        var hosted = app.Services.GetRequiredService<Bootstrap>().Profile == OperatingProfile.Hosted;
        var development = app.Environment.IsDevelopment();

        return app.Use(async (http, next) =>
        {
            var headers = http.Response.Headers;
            headers.XContentTypeOptions = "nosniff";
            headers["Referrer-Policy"] = "no-referrer";
            headers.XFrameOptions = "DENY";
            headers["Cross-Origin-Opener-Policy"] = "same-origin";
            headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=()";

            // The API reference of a development run loads its viewer from a CDN.
            if (!(development && (http.Request.Path.StartsWithSegments("/scalar") || http.Request.Path.StartsWithSegments("/openapi"))))
                headers.ContentSecurityPolicy = ContentSecurityPolicy;
            if (hosted && http.Request.IsHttps)
                headers.StrictTransportSecurity = "max-age=31536000";

            // An answer of the API belongs to one caller. No shared cache and no browser history keeps it.
            if (http.Request.Path.StartsWithSegments("/api") || http.Request.Path.StartsWithSegments("/mcp"))
                headers.CacheControl = "no-store";

            await next(http);
        });
    }

    /// <summary>
    /// The anti-forgery rule for the session cookie. The cookie is <c>SameSite=Strict</c>, so a
    /// browser does not send it with a request that another site starts. That rule knows
    /// sites, not origins: a page on a sibling host name (<c>evil.example.com</c> against
    /// <c>vellum.example.com</c>) is the same site. So each request that can change state
    /// (any method other than GET, HEAD, OPTIONS and TRACE) is also checked here:
    /// <list type="bullet">
    /// <item><c>Sec-Fetch-Site</c>, which every current browser sends and no page can set,
    /// must be <c>same-origin</c> or <c>none</c>.</item>
    /// <item>Without that header, an <c>Origin</c> header must name the host of the request.</item>
    /// <item>A request with neither header does not come from a page in a current browser
    /// (curl, a script, an agent) and passes: it carries no ambient cookie of a victim.</item>
    /// </list>
    /// A request that presents an agent token is not checked: it is authenticated by the
    /// token alone, never by the cookie.
    /// </summary>
    public static IApplicationBuilder UseSameOriginWrites(this IApplicationBuilder app) => app.Use(async (http, next) =>
    {
        var request = http.Request;
        var method = request.Method;
        if (HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method) || HttpMethods.IsTrace(method)
            || AgentTokenSecret.Presented(request)
            || IsSameOrigin(request))
        {
            await next(http);
            return;
        }

        http.Response.StatusCode = StatusCodes.Status403Forbidden;
        await http.Response.WriteAsJsonAsync(new ErrorResponse(CrossSiteError,
            "The request does not come from this app",
            "A page of another origin sent it. Open Vellum and do it there."));
    });

    private static bool IsSameOrigin(HttpRequest request)
    {
        var site = request.Headers["Sec-Fetch-Site"].ToString();
        if (site.Length > 0)
            return site is "same-origin" or "none";

        var origin = request.Headers.Origin.ToString();
        if (origin.Length == 0)
            return true;

        // The scheme is not compared: behind a proxy that ends TLS the app sees http.
        return Uri.TryCreate(origin, UriKind.Absolute, out var uri)
               && string.Equals(uri.Authority, request.Host.Value, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Reads <c>X-Forwarded-For</c> and <c>X-Forwarded-Proto</c> from the proxies that
    /// <c>Vellum__TrustedProxies</c> names (addresses or CIDR ranges, separated by <c>;</c>),
    /// and from no other sender. Only the last hop is read, which is the value the trusted
    /// proxy wrote. Without the setting the headers are ignored.
    /// </summary>
    /// <exception cref="BootstrapException">An entry is no address and no range.</exception>
    public static IApplicationBuilder UseTrustedProxies(this WebApplication app)
    {
        var entries = (app.Configuration[TrustedProxiesSetting] ?? "")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (entries.Length == 0)
            return app;

        var options = new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
            ForwardLimit = 1,
        };
        options.KnownProxies.Clear();
        options.KnownIPNetworks.Clear();
        foreach (var entry in entries)
        {
            if (IPAddress.TryParse(entry, out var address))
                options.KnownProxies.Add(address);
            else if (System.Net.IPNetwork.TryParse(entry, out var network) && network.PrefixLength > 0)
                options.KnownIPNetworks.Add(network);
            else
                throw new BootstrapException(
                    "Vellum__TrustedProxies is not valid. Set it to the addresses or CIDR ranges of your reverse proxies, separated by ';', " +
                    "for example '10.0.0.5;172.20.0.0/16'. A range that holds every address is not accepted.");
        }

        return app.UseForwardedHeaders(options);
    }
}
