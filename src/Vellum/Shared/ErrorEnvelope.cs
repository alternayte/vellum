
namespace Vellum.Shared;

public sealed record ErrorResponse(
    string Type,
    string Title,
    string? Detail = null,
    IReadOnlyList<FieldError>? Errors = null);

public sealed record FieldError(string Field, string Message);

/// <summary>
/// The answer to a create with a client ID that a record outside the caller's scope already
/// has. It shows nothing of that record.
/// </summary>
public static class IdInUse
{
    public static IResult Conflict() =>
        Results.Conflict(new ErrorResponse("id_in_use", "The ID is in use. Send a new ID."));
}

public static class ResultExtensions
{
    /// <summary>
    /// The 400 for the first rule that a request breaks, or null when it breaks none. Each
    /// argument is the result of one rule, such as <see cref="TextRule.Line"/>.
    /// </summary>
    public static IResult? Refusal(params ValidationError?[] checks)
    {
        var errors = checks.OfType<ValidationError>().ToList();
        return errors.Count == 0 ? null : Invalid(errors);
    }

    /// <summary>400 <c>validation_error</c> for one field.</summary>
    public static IResult Invalid(string field, string message) => Invalid([new FieldError(field, message)]);

    /// <summary>400 <c>validation_error</c> for one broken rule.</summary>
    public static IResult Invalid(ValidationError error) => Invalid(error.Field, error.Message);

    /// <summary>400 <c>validation_error</c> for each broken rule.</summary>
    public static IResult Invalid(IEnumerable<ValidationError> errors) =>
        Invalid(errors.Select(e => new FieldError(e.Field, e.Message)).ToList());

    /// <summary>400 <c>validation_error</c> with the errors of the fields.</summary>
    public static IResult Invalid(IReadOnlyList<FieldError> errors) =>
        Results.BadRequest(new ErrorResponse("validation_error", "Validation failed", Errors: errors));

    /// <summary>404 <c>not_found</c>: "<paramref name="what"/> not found".</summary>
    public static IResult NotFound(string what) => Results.NotFound(new ErrorResponse("not_found", $"{what} not found"));

    /// <summary>409 <c>conflict</c> with the sentence that says why.</summary>
    public static IResult Conflict(string message) => Results.Conflict(new ErrorResponse("conflict", message));

    /// <summary>A value that must be present and hold more than white space.</summary>
    public static ValidationError? Required(string field, string? value) =>
        string.IsNullOrWhiteSpace(value) ? new ValidationError(field, "Required") : null;

    public static IResult ToHttpResult(this CommandResult result) => result switch
    {
        CommandResult.Success => Results.NoContent(),
        CommandResult.Invalid inv => Invalid(inv.Errors),
        CommandResult.Conflict c => Conflict(c.Message),
        CommandResult.NotFound n => Results.NotFound(new ErrorResponse("not_found", n.Message)),
        _ => Results.StatusCode(500)
    };

    public static IResult ToHttpResult<T>(this CommandResult<T> result) => result switch
    {
        CommandResult<T>.Success s => Results.Ok(s.Value),
        CommandResult<T>.Invalid inv => Invalid(inv.Errors),
        CommandResult<T>.Conflict c => Conflict(c.Message),
        CommandResult<T>.NotFound n => Results.NotFound(new ErrorResponse("not_found", n.Message)),
        _ => Results.StatusCode(500)
    };

    public static IResult ToCreatedResult<T>(this CommandResult<T> result, string uri) => result switch
    {
        CommandResult<T>.Success s => Results.Created(uri, s.Value),
        _ => result.ToHttpResult()
    };
}
