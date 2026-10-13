using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace Vellum.Shared;

/// <summary>Endpoint metadata: the request body has at most <paramref name="Bytes"/> bytes.</summary>
public sealed record BodyLimitMetadata(int Bytes);

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

    public static IServiceCollection AddRequestLimits(this IServiceCollection services) =>
        services.AddRateLimiter(options =>
        {
            // One fixed window for each client address. A proxy must pass the client address
            // on (ASPNETCORE_FORWARDEDHEADERS_ENABLED), or all its clients share one window.
            options.AddPolicy(CredentialPolicy, http => RateLimitPartition.GetFixedWindowLimiter(
                http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = CredentialPermits,
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
