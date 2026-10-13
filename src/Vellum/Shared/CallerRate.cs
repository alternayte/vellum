using System.Globalization;
using System.Security.Claims;
using System.Threading.RateLimiting;

namespace Vellum.Shared;

/// <summary>
/// A rate limit of one kind of write: one caller makes a bounded number of them in a minute.
/// The caller is the user, so a person and every agent token that acts for that person share
/// one window. It is a filter of the endpoint and not a policy of the rate limiting
/// middleware: a call that reaches the endpoint without that middleware (an MCP tool) counts
/// in the same window as a call over HTTP. Each kind of write is a class of its own, so each
/// has a window of its own.
/// </summary>
public abstract class CallerRate(int perMinute) : IDisposable
{
    private readonly PartitionedRateLimiter<string> _limiter = PartitionedRateLimiter.Create<string, string>(caller =>
        RateLimitPartition.GetFixedWindowLimiter(caller, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = perMinute,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
        }));

    /// <summary>The endpoint filter of the window <typeparamref name="TRate"/>, which the container holds once.</summary>
    public static async ValueTask<object?> Filter<TRate>(EndpointFilterInvocationContext context, EndpointFilterDelegate next) where TRate : CallerRate
    {
        var http = context.HttpContext;
        var caller = http.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? http.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        using var lease = http.RequestServices.GetRequiredService<TRate>()._limiter.AttemptAcquire(caller);
        if (lease.IsAcquired)
            return await next(context);

        // The same answer as the rate limiting middleware gives (RequestLimits).
        var wait = lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter) ? retryAfter : TimeSpan.FromMinutes(1);
        var seconds = Math.Max(1, (int)Math.Ceiling(wait.TotalSeconds));
        http.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
        return Results.Json(
            new ErrorResponse("rate_limited", "Too many attempts", $"Wait {seconds} seconds and try again."),
            statusCode: StatusCodes.Status429TooManyRequests);
    }

    public void Dispose()
    {
        _limiter.Dispose();
        GC.SuppressFinalize(this);
    }
}
