using System.Security.Claims;
using System.Text.Json;
using Deedbox;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Vellum.Shared;

namespace Vellum.Modules.Promises;

/// <summary>What the promise handlers share: who calls, how a body is read and how a promise stream is reached.</summary>
internal static class PromiseAccess
{
    public static string UserId(ClaimsPrincipal user) => user.FindFirstValue(ClaimTypes.NameIdentifier)!;

    /// <summary>
    /// True for a person signed in through the app's own session cookie. An API key, an agent
    /// token or any other way to authenticate is not a person at a screen, and such a caller
    /// never approves a promise (SDD guarantee 12, and §6: only a human-authorized revision
    /// changes an approved promise).
    /// </summary>
    public static bool IsInteractive(ClaimsPrincipal user) =>
        user.Identity is { IsAuthenticated: true } identity && identity.AuthenticationType == IdentityConstants.ApplicationScheme;

    public static IResult InteractiveSessionRequired() => Results.Json(
        new ErrorResponse("interactive_session_required",
            "Only a person signed in to Vellum approves a promise",
            "Sign in to the app and approve the promise there. An API key or an agent identity cannot approve."),
        statusCode: StatusCodes.Status403Forbidden);

    /// <summary>
    /// Reads a JSON body with the app's options. A body that is not the expected document gets
    /// 400 with the error envelope and the reader's message, not a bare status.
    /// </summary>
    public static async Task<(T? Body, IResult? Error)> ReadAsync<T>(HttpContext http, bool allowEmpty = false) where T : class
    {
        var options = http.RequestServices.GetRequiredService<IOptions<JsonOptions>>().Value.SerializerOptions;
        using var buffer = new MemoryStream();
        await http.Request.Body.CopyToAsync(buffer, http.RequestAborted);
        if (buffer.Length == 0)
        {
            return allowEmpty
                ? (null, null)
                : (null, Results.BadRequest(new ErrorResponse("invalid_body", "A JSON body is required")));
        }

        try
        {
            return JsonSerializer.Deserialize<T>(buffer.GetBuffer().AsSpan(0, (int)buffer.Length), options) is { } body
                ? (body, null)
                : (null, Results.BadRequest(new ErrorResponse("invalid_body", "The body is null")));
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            return (null, Results.BadRequest(new ErrorResponse("invalid_body", "The body is not the expected JSON document", ex.Message)));
        }
    }

    /// <summary>
    /// Refuses a change that would make the stored promise larger than
    /// <see cref="PromiseRules.MaxDocumentChars"/>. Every revision is kept, so the size of a
    /// promise is the sum of its revisions; the bound keeps one read and one write finite.
    /// </summary>
    private static CommandResult<IReadOnlyList<object>> Bounded(PromiseState state, CommandResult<IReadOnlyList<object>> decision)
    {
        if (decision is not CommandResult<IReadOnlyList<object>>.Success { Value.Count: > 0 } success
            || success.Value.All(e => e is PromiseEvent.VerificationRunRecorded or PromiseEvent.PromiseDeleted or PromiseEvent.PromiseApproved))
            return decision;

        return PromiseJson.Write(success.Value.Aggregate(state, PromiseState.Evolve)).Length > PromiseRules.MaxDocumentChars
            ? new CommandResult<IReadOnlyList<object>>.Conflict(
                $"The promise with all its revisions would be larger than {PromiseRules.MaxDocumentChars} characters. Remove obligations, or retire this promise and write a new one.")
            : decision;
    }

    public static IResult Invalid(string field, string message) =>
        Results.BadRequest(new ErrorResponse("validation_error", "Validation failed", Errors: [new FieldError(field, message)]));

    public static IResult NotFound() => Results.NotFound(new ErrorResponse("not_found", "Promise not found"));

    /// <summary>
    /// Runs a decision on a promise stream. Every stream type shares one space of IDs, so an
    /// ID that names a model, a schema or a scenario is not a promise: the write finds nothing.
    /// </summary>
    public static async Task<CommandResult<PromiseState>> DecideAsync(
        IEventStore store, Guid promiseId, Func<PromiseState, CommandResult<IReadOnlyList<object>>> decide, CancellationToken ct)
    {
        try
        {
            return await store.Decide<PromiseState, PromiseState>(promiseId, state => Bounded(state, decide(state)), state => state, ct);
        }
        catch (DeedboxException e) when (e.Code == DeedboxError.StreamTypeMismatch)
        {
            return new CommandResult<PromiseState>.NotFound("Promise not found");
        }
    }
}
