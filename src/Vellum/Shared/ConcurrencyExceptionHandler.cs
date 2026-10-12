using Deedbox;
using Microsoft.AspNetCore.Diagnostics;
using Npgsql;

namespace Vellum.Shared;

/// <summary>
/// Event-store refusals that wrote nothing. Each one gets the shared error envelope instead
/// of a 500, so the caller knows whether to reload, pick another ID or try again.
/// </summary>
public sealed class ConcurrencyExceptionHandler : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        var (status, error) = exception switch
        {
            // A stream moved between a load and its append.
            ConcurrencyException => (StatusCodes.Status409Conflict,
                new ErrorResponse("conflict", "The model changed while the request ran. Reload and try again.")),
            // The ID already names a stream of another kind, such as a model branch.
            DeedboxException { Code: DeedboxError.StreamTypeMismatch } => (StatusCodes.Status409Conflict,
                new ErrorResponse("conflict", "This ID is already in use. Choose another ID.")),
            _ when IsDeadlock(exception) => (StatusCodes.Status503ServiceUnavailable,
                new ErrorResponse("busy", "Another write held the model. Nothing was saved; try again.")),
            _ => (0, null!),
        };
        if (status == 0)
            return false;

        if (status == StatusCodes.Status503ServiceUnavailable)
            httpContext.Response.Headers.RetryAfter = "1";
        httpContext.Response.StatusCode = status;
        await httpContext.Response.WriteAsJsonAsync(error, cancellationToken);
        return true;
    }

    public static bool IsDeadlock(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is PostgresException { SqlState: PostgresErrorCodes.DeadlockDetected })
                return true;
        }
        return false;
    }
}
