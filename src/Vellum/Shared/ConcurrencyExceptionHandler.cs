using Deedbox;
using Microsoft.AspNetCore.Diagnostics;

namespace Vellum.Shared;

/// <summary>
/// A stream moved between a load and its append. The request wrote nothing, so the caller
/// gets the same 409 conflict envelope as every other conflict and can try again.
/// </summary>
public sealed class ConcurrencyExceptionHandler : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (exception is not ConcurrencyException)
            return false;

        httpContext.Response.StatusCode = StatusCodes.Status409Conflict;
        await httpContext.Response.WriteAsJsonAsync(
            new ErrorResponse("conflict", "The model changed while the request ran. Reload and try again."),
            cancellationToken);
        return true;
    }
}
