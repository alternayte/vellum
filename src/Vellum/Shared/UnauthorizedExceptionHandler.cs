using Microsoft.AspNetCore.Diagnostics;

namespace Vellum.Shared;

/// <summary>A role check that refused the caller: 403 in the shared error envelope.</summary>
internal sealed class UnauthorizedExceptionHandler : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (exception is not UnauthorizedAccessException)
            return false;

        httpContext.Response.StatusCode = StatusCodes.Status403Forbidden;
        await httpContext.Response.WriteAsJsonAsync(
            new ErrorResponse("forbidden", "Forbidden", exception.Message), cancellationToken);
        return true;
    }
}

/// <summary>
/// The answer to an exception that no handler took: 500 in the shared error envelope, with
/// nothing of the exception in it. The exception handler middleware logs the exception.
/// </summary>
internal static class UnhandledException
{
    public static readonly ErrorResponse Error = new("internal_error", "An error occurred while processing your request.");

    public static Task WriteAsync(HttpContext httpContext)
    {
        httpContext.Response.StatusCode = StatusCodes.Status500InternalServerError;
        return httpContext.Response.WriteAsJsonAsync(Error, httpContext.RequestAborted);
    }
}
