using Vellum.Kernel.Results;

namespace Vellum.Shared;

public sealed record ErrorResponse(
    string Type,
    string Title,
    string? Detail = null,
    IReadOnlyList<FieldError>? Errors = null);

public sealed record FieldError(string Field, string Message);

public static class ResultExtensions
{
    public static IResult ToHttpResult(this CommandResult result) => result switch
    {
        CommandResult.Success => Results.NoContent(),
        CommandResult.Invalid inv => Results.BadRequest(new ErrorResponse(
            "validation_error", "Validation failed", Errors: inv.Errors.Select(e => new FieldError(e.Field, e.Message)).ToList())),
        CommandResult.Conflict c => Results.Conflict(new ErrorResponse("conflict", c.Message)),
        CommandResult.NotFound n => Results.NotFound(new ErrorResponse("not_found", n.Message)),
        _ => Results.StatusCode(500)
    };

    public static IResult ToHttpResult<T>(this CommandResult<T> result) => result switch
    {
        CommandResult<T>.Success s => Results.Ok(s.Value),
        CommandResult<T>.Invalid inv => Results.BadRequest(new ErrorResponse(
            "validation_error", "Validation failed", Errors: inv.Errors.Select(e => new FieldError(e.Field, e.Message)).ToList())),
        CommandResult<T>.Conflict c => Results.Conflict(new ErrorResponse("conflict", c.Message)),
        CommandResult<T>.NotFound n => Results.NotFound(new ErrorResponse("not_found", n.Message)),
        _ => Results.StatusCode(500)
    };

    public static IResult ToCreatedResult<T>(this CommandResult<T> result, string uri) => result switch
    {
        CommandResult<T>.Success s => Results.Created(uri, s.Value),
        _ => result.ToHttpResult()
    };
}
