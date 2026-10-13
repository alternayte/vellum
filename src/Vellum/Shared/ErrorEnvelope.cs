
namespace Vellum.Shared;

public sealed record ErrorResponse(
    string Type,
    string Title,
    string? Detail = null,
    IReadOnlyList<FieldError>? Errors = null);

public sealed record FieldError(string Field, string Message);

public static class ResultExtensions
{
    /// <summary>
    /// The 400 for the first rule that a request breaks, or null when it breaks none. Each
    /// argument is the result of one rule, such as <see cref="TextRule.Line"/>.
    /// </summary>
    public static IResult? Refusal(params ValidationError?[] checks)
    {
        var errors = checks.Where(c => c is not null).Select(c => new FieldError(c!.Field, c.Message)).ToList();
        return errors.Count == 0
            ? null
            : Results.BadRequest(new ErrorResponse("validation_error", "Validation failed", Errors: errors));
    }

    /// <summary>A value that must be present and hold more than white space.</summary>
    public static ValidationError? Required(string field, string? value) =>
        string.IsNullOrWhiteSpace(value) ? new ValidationError(field, "Required") : null;

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
