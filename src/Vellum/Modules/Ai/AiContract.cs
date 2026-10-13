using System.Text.Json;

namespace Vellum.Modules.Ai;

/// <summary>The AI tasks a workspace routes to a provider (<c>ai.taskRouting</c>).</summary>
public enum AiTask
{
    InferArchitecture,
    ProposeFlow,
    Critique,
    Explain,
}

public enum AiRole
{
    User,
    Assistant,
}

public enum AiPartKind
{
    /// <summary>Text the application wrote.</summary>
    Instruction,
    /// <summary>
    /// Text from a repository, a document, a user's model or an earlier model answer. It is
    /// sent inside a marked data block and the model is told that it is not an instruction.
    /// </summary>
    Untrusted,
}

public sealed record AiPart(AiPartKind Kind, string Text)
{
    public static AiPart Instruction(string text) => new(AiPartKind.Instruction, text);
    public static AiPart Untrusted(string text) => new(AiPartKind.Untrusted, text);
}

public sealed record AiMessage(AiRole Role, IReadOnlyList<AiPart> Parts);

/// <summary>
/// One provider-neutral request. Every text in it passes the secret redactor before it
/// leaves the process.
/// </summary>
/// <param name="Purpose">A short fixed label for the usage record, for example <c>propose-flow</c>. Never user text.</param>
/// <param name="JsonSchema">
/// Required for a structured call. The providers accept a common subset only: every object
/// lists all its properties in <c>required</c> and sets <c>additionalProperties</c> to false,
/// and the schema uses no length, range or pattern keyword.
/// </param>
/// <param name="ActorId">The user who caused the call, for the usage record.</param>
public sealed record AiRequest(
    Guid WorkspaceId,
    AiTask Task,
    string Purpose,
    string PromptTemplateVersion,
    string System,
    IReadOnlyList<AiMessage> Messages,
    JsonElement? JsonSchema,
    int MaxOutputTokens,
    string? ActorId = null);

/// <summary>
/// What the provider calls of one request used. A token count is null when a provider did
/// not report it. The cost is null when a count is missing or the model has no price.
/// </summary>
public sealed record AiUsage(
    Guid ProviderId, string ProviderKind, string Model, string? ReportedModel,
    int Calls, int? InputTokens, int? OutputTokens, decimal? CostUsd);

/// <summary>Why a request gave no value. No member holds prompt text, response text or a credential.</summary>
public abstract record AiFailure
{
    private AiFailure() { }

    /// <summary>The task has no provider, or the provider has no API key.</summary>
    public sealed record NotConfigured : AiFailure;
    /// <summary>The task is routed to a provider that is switched off or that the instance policy does not allow.</summary>
    public sealed record Disabled : AiFailure;
    /// <param name="Scope"><c>provider</c> or <c>workspace</c>.</param>
    /// <param name="Reason">
    /// <c>limit_reached</c>, or <c>price_unknown</c> when a budget is set and the model has no
    /// price, so the budget cannot be kept.
    /// </param>
    public sealed record BudgetExceeded(string Scope, string Reason) : AiFailure;
    /// <summary>The workspace's own limit for the provider, or the provider's limit after the bounded retries.</summary>
    public sealed record RateLimited(TimeSpan? RetryAfter) : AiFailure;
    /// <param name="Reason">The provider's category word, for example <c>content_filter</c>.</param>
    public sealed record Refused(string Reason) : AiFailure;
    /// <summary>The answer is not valid against the schema after the repair attempts, or it was cut at the output limit.</summary>
    public sealed record InvalidOutput(IReadOnlyList<string> Issues) : AiFailure;
    /// <param name="Code">A stable word, for example <c>invalid_api_key</c> or <c>model_not_found</c>.</param>
    /// <param name="Message">A sentence that says what to change.</param>
    public sealed record ProviderError(string Code, string Message) : AiFailure;
    public sealed record TimedOut : AiFailure;
    public sealed record Cancelled : AiFailure;
}

public abstract record AiResult<T>
{
    private AiResult() { }

    public sealed record Succeeded(T Value, AiUsage Usage) : AiResult<T>;
    public sealed record Failed(AiFailure Failure) : AiResult<T>;
}

/// <summary>A text stream ended with a typed failure. A cancelled stream raises <see cref="OperationCanceledException"/>.</summary>
public sealed class AiStreamFailedException(AiFailure failure) : Exception($"The AI stream failed: {failure.GetType().Name}.")
{
    public AiFailure Failure { get; } = failure;
}
