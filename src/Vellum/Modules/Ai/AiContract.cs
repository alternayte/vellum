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

/// <summary>Where the text of an untrusted part comes from. It decides whether the part may leave the instance.</summary>
public enum AiDataClass
{
    /// <summary>
    /// Text that anyone can read: a public repository, a public document. Also text that
    /// holds no repository content, such as validation issues.
    /// </summary>
    Public,
    /// <summary>
    /// Code or text of a private repository, and any text whose origin the caller does not
    /// know. It is sent to a provider only when the workspace's effective setting
    /// <c>privacy.sendPrivateCodeToAi</c> is true.
    /// </summary>
    PrivateRepository,
}

/// <param name="Source">
/// The class of an untrusted part. A part that is built without one is
/// <see cref="AiDataClass.PrivateRepository"/>, the restrictive class.
/// </param>
public sealed record AiPart(AiPartKind Kind, string Text, AiDataClass Source = AiDataClass.PrivateRepository)
{
    /// <summary>Text the application wrote. It holds no repository content, so its class is public.</summary>
    public static AiPart Instruction(string text) => new(AiPartKind.Instruction, text, AiDataClass.Public);

    /// <param name="source">Required: the caller states where the text comes from.</param>
    public static AiPart Untrusted(string text, AiDataClass source) => new(AiPartKind.Untrusted, text, source);

    /// <summary>True for a part that needs <c>privacy.sendPrivateCodeToAi</c>.</summary>
    public bool IsPrivateCode => Kind == AiPartKind.Untrusted && Source == AiDataClass.PrivateRepository;
}

public sealed record AiMessage(AiRole Role, IReadOnlyList<AiPart> Parts);

/// <summary>
/// One provider-neutral request. Every untrusted part states its <see cref="AiDataClass"/>;
/// <see cref="AiClient"/> sends a private-repository part only when the workspace allows it.
/// Every text also passes the secret redactor before it leaves the process; the redactor is
/// best-effort and is not what protects private code.
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
    /// <summary>
    /// A setting forbids the call. <paramref name="Setting"/> is null when the task is routed
    /// to a provider that is switched off or that the instance policy does not allow. It is
    /// <see cref="PrivateCodeSetting"/> when the request holds a private-repository part and
    /// the workspace's effective settings do not allow private code to be sent to a provider;
    /// nothing was sent.
    /// </summary>
    public sealed record Disabled(string? Setting = null) : AiFailure
    {
        public const string PrivateCodeSetting = "privacy.sendPrivateCodeToAi";
    }
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
    /// <summary>
    /// The answer is not valid against the schema after the repair attempts, or it was cut at
    /// the output limit. An issue names a place in the request's schema, the keyword that
    /// failed and the JSON kind of the value. It holds no text of the answer: no property
    /// name and no value.
    /// </summary>
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
