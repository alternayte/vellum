using Deedbox;
using Microsoft.AspNetCore.Diagnostics;
using Npgsql;
using Vellum.Shared.Telemetry;

namespace Vellum.Shared;

/// <summary>
/// Refusals of the event store and the database that wrote nothing. Each one gets the shared
/// error envelope instead of a 500, so the caller knows whether to reload, pick another ID,
/// change the request or try again.
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
            // Text that Postgres cannot hold or compare, such as U+0000 in a filter or a cursor.
            // TextRule refuses it, with the field, wherever a value is stored; this is the
            // answer for a value that only reaches a query. The statement failed as a whole.
            _ when exception is UnstorableTextException
                || HasSqlState(exception, PostgresErrorCodes.CharacterNotInRepertoire, PostgresErrorCodes.UntranslatableCharacter) =>
                (StatusCodes.Status400BadRequest,
                    new ErrorResponse("invalid_text", "The request holds text that cannot be stored, such as the character U+0000. Nothing was saved.")),
            _ when IsDeadlock(exception) => (StatusCodes.Status503ServiceUnavailable,
                new ErrorResponse("busy", "Another write held the model. Nothing was saved; try again.")),
            _ => (0, null!),
        };
        if (status == 0)
            return false;
        if (exception is ConcurrencyException)
            VellumMetrics.AppendConflict();

        if (status == StatusCodes.Status503ServiceUnavailable)
            httpContext.Response.Headers.RetryAfter = "1";
        httpContext.Response.StatusCode = status;
        await httpContext.Response.WriteAsJsonAsync(error, cancellationToken);
        return true;
    }

    public static bool IsDeadlock(Exception exception) => HasSqlState(exception, PostgresErrorCodes.DeadlockDetected);

    private static bool HasSqlState(Exception exception, params string[] states)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is PostgresException postgres && states.Contains(postgres.SqlState))
                return true;
        }
        return false;
    }
}

/// <summary>Text of a query that Postgres could not hold, refused before the query runs (<see cref="EnumText.TryParseFilter{T}"/>).</summary>
public sealed class UnstorableTextException() : Exception("The text holds a character that Postgres cannot hold, such as U+0000.");
