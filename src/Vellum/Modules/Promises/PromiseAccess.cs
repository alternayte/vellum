using System.Security.Claims;
using System.Text.Json;
using Deedbox;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;
using Vellum.Modules.Identity.AgentTokens;
using Vellum.Shared;

namespace Vellum.Modules.Promises;

/// <summary>The bounds on verification runs. A test host gives smaller ones.</summary>
/// <param name="MaxRunsPerObligation">The most runs one obligation of one revision holds. Every run is an event of the promise stream.</param>
/// <param name="RunsPerMinute">The most runs and pieces of evidence one caller records in a minute.</param>
public sealed record PromiseLimits(int MaxRunsPerObligation = 1_000, int RunsPerMinute = 120)
{
    public static readonly PromiseLimits Default = new();

    /// <summary>The rate limit policy of the run and evidence endpoints.</summary>
    public const string RunPolicy = "promise-run";
}

/// <summary>The result of a write to a promise stream. <paramref name="TooLarge"/>: the write was refused because the revision would pass its size limit.</summary>
internal readonly record struct PromiseWrite(CommandResult<PromiseState> Result, bool TooLarge)
{
    public bool Succeeded => !TooLarge && Result is CommandResult<PromiseState>.Success;

    /// <summary>The answer for a write that did not succeed.</summary>
    public IResult Refusal() => TooLarge
        ? Results.Json(
            new ErrorResponse("promise_too_large",
                $"The revision would hold more than {PromiseRules.MaxRevisionChars} characters",
                "The content and the obligations of one revision have this limit as JSON. Shorten the text or remove an obligation. A promise at the limit can still be revised and retired."),
            statusCode: StatusCodes.Status413PayloadTooLarge)
        : Result.ToHttpResult();
}

/// <summary>What the promise handlers share: who calls, how a body is read and how a promise stream is reached.</summary>
internal static class PromiseAccess
{
    public static string UserId(ClaimsPrincipal user) => user.FindFirstValue(ClaimTypes.NameIdentifier)!;

    /// <summary>
    /// The origin of a new promise, from who calls. An agent token always gives
    /// <c>agent-suggested</c>, whatever the body says: an agent cannot say that a person
    /// wrote the promise. A person gives <c>human</c>, or <c>ai-suggested</c> when the body
    /// says so. Null for a person whose body names any other origin.
    /// </summary>
    public static string? Origin(ClaimsPrincipal user, string? requested) =>
        AgentPrincipal.IsAgent(user) ? PromiseOrigin.AgentSuggested
        : requested is null or PromiseOrigin.Human ? PromiseOrigin.Human
        : requested == PromiseOrigin.AiSuggested ? PromiseOrigin.AiSuggested
        : null;

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
    /// True when a decision adds content that would make the revision it changes larger than
    /// <see cref="PromiseRules.MaxRevisionChars"/>. Only a write that adds content is measured:
    /// a new revision copies content that was inside the limit, a revision that retires copies
    /// nothing, and an approval, a delete and a run add no content. So a promise at the limit
    /// can always be revised, retired and deleted.
    /// </summary>
    internal static bool PassesRevisionLimit(PromiseState state, IReadOnlyList<object> events)
    {
        if (!events.Any(e => e is PromiseEvent.PromiseCreated or PromiseEvent.PromiseDraftChanged
                or PromiseEvent.ObligationAdded or PromiseEvent.ObligationChanged))
            return false;

        var changed = events.Aggregate(state, PromiseState.Evolve).Latest!;
        return PromiseJson.Write(new { changed.Content, changed.Obligations }).Length > PromiseRules.MaxRevisionChars;
    }

    public static IResult Invalid(string field, string message) =>
        Results.BadRequest(new ErrorResponse("validation_error", "Validation failed", Errors: [new FieldError(field, message)]));

    public static IResult NotFound() => Results.NotFound(new ErrorResponse("not_found", "Promise not found"));

    /// <summary>
    /// Runs a decision on a promise stream. Every stream type shares one space of IDs, so an
    /// ID that names a model, a schema or a scenario is not a promise: the write finds nothing.
    /// </summary>
    public static async Task<PromiseWrite> DecideAsync(
        IEventStore store, Guid promiseId, Func<PromiseState, CommandResult<IReadOnlyList<object>>> decide, CancellationToken ct)
    {
        var tooLarge = false;
        try
        {
            var result = await store.Decide<PromiseState, PromiseState>(promiseId, state =>
            {
                var decision = decide(state);
                tooLarge = decision is CommandResult<IReadOnlyList<object>>.Success success && PassesRevisionLimit(state, success.Value);
                return tooLarge ? new CommandResult<IReadOnlyList<object>>.Conflict("The revision would pass its size limit.") : decision;
            }, state => state, ct);
            return new PromiseWrite(result, tooLarge);
        }
        // The stream of a promise of a deleted project is deleted with it.
        catch (DeedboxException e) when (e.Code is DeedboxError.StreamTypeMismatch or DeedboxError.StreamDeleted)
        {
            return new PromiseWrite(new CommandResult<PromiseState>.NotFound("Promise not found"), false);
        }
    }
}
