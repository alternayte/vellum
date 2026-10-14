using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Vellum.Shared;

/// <summary>Endpoint metadata: the request body has at most <paramref name="Bytes"/> bytes.</summary>
public sealed record BodyLimitMetadata(int Bytes);

/// <summary>
/// The limits for callers with no credential, for each client address in each minute. Tests
/// that register many accounts from one address replace it.
/// </summary>
/// <param name="RegistrationsPerMinute">New accounts through <c>POST /api/auth/register</c>.</param>
/// <param name="RequestsPerMinute">Every request to the API, the MCP endpoint and the agent documents that carries no valid credential.</param>
/// <param name="CredentialAttemptsPerMinute">Sign-in and first-run setup attempts.</param>
public sealed record AnonymousRateLimits(
    int RegistrationsPerMinute = 10, int RequestsPerMinute = 300, int CredentialAttemptsPerMinute = RequestLimits.CredentialPermits);

/// <summary>
/// Body size limits and per-client rate limits for endpoints that an attacker can call
/// before any role check.
/// </summary>
public static class RequestLimits
{
    /// <summary>The rate limit policy for sign-in and first-run setup.</summary>
    public const string CredentialPolicy = "credential";
    public const int CredentialPermits = 10;
    public static readonly TimeSpan CredentialWindow = TimeSpan.FromMinutes(1);

    public static TBuilder WithBodyLimit<TBuilder>(this TBuilder builder, int bytes) where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(new BodyLimitMetadata(bytes));

    /// <summary>The rate limit policy for new accounts.</summary>
    public const string RegistrationPolicy = "registration";

    private static readonly string[] LimitedWithoutCredential = ["/api", "/mcp", "/llms.txt", "/llms-full.txt", "/skills"];

    private static string Client(HttpContext http) => http.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    public static IServiceCollection AddRequestLimits(this IServiceCollection services)
    {
        services.TryAddSingleton(new AnonymousRateLimits());
        return services.AddRateLimiter(options =>
        {
            // A caller with no valid credential gets a bounded number of requests, whatever
            // the endpoint: a wrong bearer token, a probe of the API, the open documents. The
            // health probes and the files of the app are not counted. This limit and the
            // limit of an endpoint both apply.
            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(http =>
            {
                var path = http.Request.Path;
                if (http.User.Identity is { IsAuthenticated: true } || !LimitedWithoutCredential.Any(prefix => path.StartsWithSegments(prefix)))
                    return RateLimitPartition.GetNoLimiter("");

                var limits = http.RequestServices.GetRequiredService<AnonymousRateLimits>();
                return RateLimitPartition.GetFixedWindowLimiter(Client(http), _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = limits.RequestsPerMinute,
                    Window = CredentialWindow,
                    QueueLimit = 0,
                });
            });
            options.AddPolicy(RegistrationPolicy, http => RateLimitPartition.GetFixedWindowLimiter(
                Client(http),
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = http.RequestServices.GetRequiredService<AnonymousRateLimits>().RegistrationsPerMinute,
                    Window = CredentialWindow,
                    QueueLimit = 0,
                }));
            // One fixed window for each client address. A proxy must pass the client address
            // on (Vellum__TrustedProxies), or all its clients share one window.
            options.AddPolicy(CredentialPolicy, http => RateLimitPartition.GetFixedWindowLimiter(
                Client(http),
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = http.RequestServices.GetRequiredService<AnonymousRateLimits>().CredentialAttemptsPerMinute,
                    Window = CredentialWindow,
                    QueueLimit = 0,
                }));
            options.OnRejected = async (context, ct) =>
            {
                var wait = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter) ? retryAfter : CredentialWindow;
                var seconds = Math.Max(1, (int)Math.Ceiling(wait.TotalSeconds));
                context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                context.HttpContext.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
                await context.HttpContext.Response.WriteAsJsonAsync(
                    new ErrorResponse("rate_limited", "Too many attempts", $"Wait {seconds} seconds and try again."), ct);
            };
        });
    }

    /// <summary>
    /// Reads the body of an endpoint that has a <see cref="BodyLimitMetadata"/> into memory,
    /// up to the limit, before the endpoint parses it or checks a role. A larger body gets
    /// 413 and the endpoint does not run. The limit counts bytes on the wire.
    /// </summary>
    public static IApplicationBuilder UseBodyLimits(this IApplicationBuilder app) => app.Use(async (http, next) =>
    {
        if (http.GetEndpoint()?.Metadata.GetMetadata<BodyLimitMetadata>() is not { } limit)
        {
            await next(http);
            return;
        }

        if (http.Request.ContentLength > limit.Bytes)
        {
            await RefuseAsync(http, limit.Bytes);
            return;
        }

        // One byte more than the limit is enough to know that the body is too large.
        var buffer = new byte[limit.Bytes + 1];
        var length = 0;
        while (length < buffer.Length)
        {
            var read = await http.Request.Body.ReadAsync(buffer.AsMemory(length), http.RequestAborted);
            if (read == 0)
                break;
            length += read;
        }

        if (length > limit.Bytes)
        {
            await RefuseAsync(http, limit.Bytes);
            return;
        }

        http.Request.Body = new MemoryStream(buffer, 0, length, writable: false);
        http.Request.ContentLength = length;
        await next(http);
    });

    private static Task RefuseAsync(HttpContext http, int bytes)
    {
        http.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
        return http.Response.WriteAsJsonAsync(
            new ErrorResponse("payload_too_large", "The request body is too large", $"Send at most {bytes} bytes."));
    }
}
