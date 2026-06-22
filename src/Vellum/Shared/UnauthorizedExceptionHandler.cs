using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace Vellum.Shared;

internal sealed class UnauthorizedExceptionHandler : IExceptionHandler
{
    public ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (exception is not UnauthorizedAccessException)
            return ValueTask.FromResult(false);

        httpContext.Response.StatusCode = StatusCodes.Status403Forbidden;

        var problem = new ProblemDetails
        {
            Status = StatusCodes.Status403Forbidden,
            Type = "https://tools.ietf.org/html/rfc9110#section-15.5.4",
            Title = "Forbidden",
            Detail = exception.Message
        };

        return new ValueTask<bool>(
            httpContext.Response.WriteAsJsonAsync(problem, cancellationToken)
                .ContinueWith(_ => true, cancellationToken));
    }
}
