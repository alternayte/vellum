using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Vellum.Shared.Outbound;

namespace Vellum.Modules.Ai.Providers;

/// <summary>Where one call goes: a provider entry of the workspace's settings, resolved.</summary>
/// <param name="RequireHttps"><c>WorkspaceSettings.RequireHttps</c>. The pipeline enforces it on every attempt.</param>
internal sealed record ProviderTarget(
    Guid ProviderId, string Kind, string Model, Uri BaseUrl, OutboundCredential? Credential,
    bool AllowPrivateNetwork, bool RequireHttps, TimeSpan Timeout, double? Temperature);

internal sealed record ProviderMessage(AiRole Role, string Text);

/// <summary>
/// What is known about a call while it runs. The meter reads it when the call ends in any
/// way, a cancellation or an exception included, to decide whether the provider can have
/// generated output that it bills.
/// </summary>
internal sealed class CallProgress
{
    /// <summary>The provider answered with a success status: generation started or ran.</summary>
    public bool ResponseStarted { get; set; }

    /// <summary>At least one piece of the answer arrived.</summary>
    public bool OutputStarted { get; set; }

    /// <summary>
    /// False when the provider refused the request before it generated anything: it sent no
    /// success status, or it sent one and then, with no output, an error of the kind that
    /// rejects a request.
    /// </summary>
    public bool MayHaveGenerated(ProviderError? error) => ResponseStarted && (OutputStarted || error is not { Rejected: true });
}

/// <param name="NativeSchema">Set when the provider's own structured-output mode is used.</param>
/// <param name="SchemaName">A name for the schema, for the providers that ask for one.</param>
internal sealed record ProviderRequest(
    ProviderTarget Target, string System, IReadOnlyList<ProviderMessage> Messages,
    JsonElement? NativeSchema, string SchemaName, int MaxOutputTokens, CallProgress Progress);

internal enum ProviderStop
{
    Completed,
    /// <summary>The output limit cut the answer.</summary>
    Truncated,
    Refused,
}

/// <param name="Rejected">The error is of a kind that a provider reports before it starts to generate.</param>
internal sealed record ProviderError(string Code, string Message, TimeSpan? RetryAfter, bool Rejected)
{
    /// <summary>The same error with the fixed sentence only, without any text that the provider's server wrote.</summary>
    public ProviderError WithoutProviderText() => this with { Message = ProviderErrors.Sentence(Code) };
}

/// <summary>The normalized answer of any provider. When <see cref="Error"/> is set, nothing else is meaningful.</summary>
internal sealed record ProviderReply(
    ProviderStop Stop, string Text, string? RefusalReason,
    int? InputTokens, int? OutputTokens, string? ReportedModel, ProviderError? Error = null)
{
    public static ProviderReply Failed(ProviderError error) => new(ProviderStop.Completed, "", null, null, null, null, error);
}

internal abstract record ProviderStreamEvent
{
    public sealed record Delta(string Text) : ProviderStreamEvent;
    /// <summary>The terminal event. Its reply holds the stop reason and the usage; its text can be empty.</summary>
    public sealed record End(ProviderReply Reply) : ProviderStreamEvent;
}

/// <summary>
/// One vendor API. An adapter maps the neutral request to the vendor's request and the
/// vendor's answer to <see cref="ProviderReply"/>. No vendor type leaves its folder.
/// </summary>
internal interface IAiProviderAdapter
{
    /// <summary>The <c>kind</c> of a provider entry in settings.</summary>
    string Kind { get; }

    /// <summary>The vendor's address, used when the entry has no base URL. Null when the entry must give one.</summary>
    Uri? DefaultBaseUrl { get; }

    bool RequiresApiKey { get; }

    OutboundCredential Credential(string apiKey);

    /// <summary>
    /// The most output tokens the vendor can bill for one request with this output limit.
    /// It is the limit itself, plus what the adapter adds to it for the vendor. The budget
    /// reserves this much.
    /// </summary>
    int OutputTokenCeiling(int maxOutputTokens);

    Task<ProviderReply> CompleteAsync(ProviderRequest request, CancellationToken ct);

    /// <summary>Text deltas, then exactly one <see cref="ProviderStreamEvent.End"/>.</summary>
    IAsyncEnumerable<ProviderStreamEvent> StreamAsync(ProviderRequest request, CancellationToken ct);
}

/// <summary>The error words every adapter uses, with the sentence a user reads.</summary>
internal static class ProviderErrors
{
    public const string UnexpectedStopCode = "unexpected_stop";
    private const int MaxReasonChars = 30;

    public static ProviderError Create(string code, string? detail = null, TimeSpan? retryAfter = null) => new(
        code,
        Sentence(code) + (string.IsNullOrWhiteSpace(detail) ? "" : $" The provider said: {Clean(detail)}"),
        retryAfter,
        code is "invalid_api_key" or "permission_denied" or "model_not_found" or "rate_limited" or "quota_exceeded"
            or "invalid_request" or "unsupported_parameter" or "request_too_large" or "url_refused" or "invalid_setting"
            or "output_limit_too_small" or "connection_failed" or "unexpected_redirect");

    /// <summary>
    /// The provider stopped for a reason that Vellum has no meaning for. The reason goes into
    /// the code as lower-case letters, digits and underscores, cut short, so a new vendor
    /// word is visible and no vendor text is.
    /// </summary>
    public static ProviderError UnexpectedStop(string? reason)
    {
        var word = new StringBuilder();
        foreach (var c in reason ?? "")
        {
            if (word.Length == MaxReasonChars)
                break;
            if (char.IsAsciiLetterOrDigit(c))
                word.Append(char.ToLowerInvariant(c));
            else if (word.Length > 0 && word[^1] != '_')
                word.Append('_');
        }

        return Create(word.Length == 0 ? UnexpectedStopCode : $"{UnexpectedStopCode}.{word}");
    }

    public static string Sentence(string code) => code switch
    {
        "invalid_api_key" => "The provider refused the API key. Save a valid key for this provider in the workspace secrets.",
        "permission_denied" => "The provider does not allow this API key to use this model or endpoint. Check that the API key is active, that it is not revoked, and that its account can use the model.",
        "model_not_found" => "The provider has no such model at this base URL. Check the model name and the base URL.",
        "rate_limited" => "The provider's rate limit is reached. Try again later.",
        "quota_exceeded" => "The provider account has no quota or credit left. Check the billing of the provider account.",
        "invalid_request" => "The provider rejected the request. A provider setting (model, structured output, or the optional temperature) or the schema does not fit this model.",
        "unsupported_parameter" => "The model does not accept a setting of this provider entry. Temperature is optional and some models reject it: clear the temperature of the provider entry.",
        "output_limit_too_small" => "The output limit of this request is below the smallest limit that the provider accepts.",
        "request_too_large" => "The request is larger than the provider accepts.",
        "provider_unavailable" => "The provider reported a server error or is overloaded. Try again later.",
        "timeout" => "The provider did not answer within the configured timeout.",
        "connection_failed" => "No connection to the provider's base URL could be made. Check the base URL.",
        "connection_lost" => "The connection to the provider broke before the answer arrived.",
        "url_refused" => "The instance policy refuses the provider's base URL. It must be an address that the policy allows: a public address, and https where the instance requires it.",
        "invalid_setting" => "The stored API key, the model name or the base URL holds a line break, another control character, or (the API key) a character outside ASCII. Nothing was sent. Save the API key again and check the model name and the base URL.",
        "response_too_large" => "The provider's answer is larger than Vellum accepts.",
        "malformed_response" => "The answer was not in the provider's documented format. Check the base URL and the provider kind.",
        "unexpected_redirect" => "The provider's base URL redirects to another address. Set the base URL to the final address.",
        "stream_interrupted" => "The provider's stream ended before it was complete.",
        _ when code.StartsWith(UnexpectedStopCode, StringComparison.Ordinal) =>
            "The provider stopped for a reason that Vellum does not use, for example a tool call.",
        _ => "The provider reported an error.",
    };

    // A provider's own sentence helps with a rejected request. It is cut, cleared of control
    // characters and passed through the redactor; it goes to the caller and never to a log.
    // ProviderHttp removes it again when the answer came from a private network.
    private static string Clean(string detail)
    {
        var text = SecretRedactor.Redact(OutboundText.Clean(detail, 300));
        return text.EndsWith('.') ? text : text + ".";
    }

    public static ProviderError FromTransport(OutboundException ex, bool midStream = false) => Create(ex.Failure switch
    {
        OutboundFailure.UrlRefused => "url_refused",
        OutboundFailure.InvalidRequest => "invalid_setting",
        OutboundFailure.Timeout => "timeout",
        OutboundFailure.ConnectionFailed => "connection_failed",
        OutboundFailure.ResponseTooLarge => "response_too_large",
        _ => midStream ? "stream_interrupted" : "connection_lost",
    });

    /// <summary>True for the vendor words that say the account cannot pay: quota, credit, billing or a spend or usage limit.</summary>
    public static bool IsBillingWord(string? vendorCode)
    {
        var word = vendorCode?.ToLowerInvariant() ?? "";
        return word.Contains("quota", StringComparison.Ordinal) || word.Contains("billing", StringComparison.Ordinal)
            || word.Contains("credit", StringComparison.Ordinal) || word.Contains("spend_limit", StringComparison.Ordinal)
            || word.Contains("usage_limit", StringComparison.Ordinal) || word is "payment_required";
    }

    /// <param name="vendorCode">The vendor's error word, when the body had one.</param>
    public static ProviderError FromStatus(int status, string? vendorCode, string? vendorMessage, TimeSpan? retryAfter = null)
    {
        var word = vendorCode?.ToLowerInvariant() ?? "";
        return status switch
        {
            >= 300 and < 400 => Create("unexpected_redirect"),
            401 => Create("invalid_api_key"),
            402 => Create("quota_exceeded"),
            403 => Create("permission_denied"),
            404 => Create("model_not_found"),
            408 or 504 => Create("timeout"),
            413 => Create("request_too_large"),
            429 => Create(IsBillingWord(word) ? "quota_exceeded" : "rate_limited", retryAfter: retryAfter),
            >= 500 => Create("provider_unavailable", retryAfter: retryAfter),
            _ when word.Contains("model_not_found", StringComparison.Ordinal) => Create("model_not_found"),
            _ when word is "api_key_invalid" or "invalid_api_key" => Create("invalid_api_key"),
            _ => Rejected(word, vendorMessage),
        };
    }

    /// <summary>
    /// A request that the provider rejected for its content. A rejection that names an
    /// unsupported parameter, or the temperature, gets its own word, so the user is told to
    /// clear the optional setting.
    /// </summary>
    public static ProviderError Rejected(string? vendorCode, string? vendorMessage) =>
        vendorCode is "unsupported_parameter" or "unsupported_value"
        || (vendorMessage?.Contains("temperature", StringComparison.OrdinalIgnoreCase) ?? false)
            ? Create("unsupported_parameter", vendorMessage)
            : Create("invalid_request", vendorMessage);
}

/// <summary>What the four adapters share: one way to send, to read an error and to read a stream.</summary>
internal static class ProviderHttp
{
    public const long MaxResponseBytes = 8 * 1024 * 1024;

    /// <exception cref="OutboundException">
    /// The model name holds a control character. The adapters call this inside the send, so
    /// the exception becomes the typed <c>invalid_setting</c> failure and nothing is sent.
    /// </exception>
    public static OutboundCall Call(ProviderRequest request, string operation, bool streaming)
    {
        var target = request.Target;
        var call = new OutboundCall(target.Kind, operation, target.BaseUrl, target.AllowPrivateNetwork, target.RequireHttps, target.Timeout)
        {
            Credential = target.Credential,
            MaxResponseBytes = MaxResponseBytes,
            Streaming = streaming,
        };

        // The model name goes into a JSON body for most vendors and into the path for one.
        // A name with a control character is a broken setting wherever it would go.
        return OutboundText.HasControlCharacter(target.Model)
            ? throw new OutboundException(OutboundFailure.InvalidRequest, call, "The model name holds a control character.")
            : call;
    }

    /// <param name="readError">Reads the vendor's error word and sentence from an error body.</param>
    public static async Task<ProviderReply> SendAsync(
        Func<Task<HttpResponseMessage>> send,
        Func<JsonElement, ProviderReply> read,
        Func<JsonElement, (string? Code, string? Message)> readError,
        CallProgress progress,
        CancellationToken ct)
    {
        try
        {
            using var response = await OutboundSend.RunAsync(send);
            var showProviderText = OutboundCall.CameFromPublicNetwork(response);
            if (!response.IsSuccessStatusCode)
                return Shown(ProviderReply.Failed(await ErrorAsync(response, readError, ct)), showProviderText);

            progress.ResponseStarted = true;
            return await OutboundBody.ReadElementAsync(response, ct) is { ValueKind: JsonValueKind.Object } body
                ? Shown(read(body), showProviderText)
                : ProviderReply.Failed(ProviderErrors.Create("malformed_response"));
        }
        catch (OutboundException ex)
        {
            return ProviderReply.Failed(ProviderErrors.FromTransport(ex));
        }
    }

    /// <param name="decode">Turns one event's JSON into zero or more stream events. It keeps the state of the stream.</param>
    /// <param name="complete">
    /// Called when the stream closes with no terminal event. It returns the answer when the
    /// events that arrived already make it complete, and null when the stream was cut.
    /// </param>
    public static async IAsyncEnumerable<ProviderStreamEvent> StreamAsync(
        Func<Task<HttpResponseMessage>> send,
        Func<SseItem<string>, IReadOnlyList<ProviderStreamEvent>> decode,
        Func<ProviderReply?> complete,
        Func<JsonElement, (string? Code, string? Message)> readError,
        CallProgress progress,
        [EnumeratorCancellation] CancellationToken ct)
    {
        HttpResponseMessage? response = null;
        ProviderError? error = null;
        var showProviderText = false;
        try
        {
            response = await OutboundSend.RunAsync(send);
            showProviderText = OutboundCall.CameFromPublicNetwork(response);
            if (response.IsSuccessStatusCode)
                progress.ResponseStarted = true;
            else
                error = await ErrorAsync(response, readError, ct);
        }
        catch (OutboundException ex)
        {
            error = ProviderErrors.FromTransport(ex);
        }

        // Disposing the response on any exit, a cancelled read included, ends the HTTP request.
        using (response)
        {
            ProviderReply? last = null;
            if (error is null)
            {
                await using var events = OutboundBody.ReadEventsAsync(response!, ct).GetAsyncEnumerator(ct);
                while (error is null)
                {
                    IReadOnlyList<ProviderStreamEvent> decoded;
                    try
                    {
                        if (!await events.MoveNextAsync())
                        {
                            last = complete();
                            break;
                        }

                        decoded = decode(events.Current);
                    }
                    catch (OutboundException ex)
                    {
                        // A connection that breaks after the answer was complete loses nothing.
                        last = complete();
                        if (last is null)
                            error = ProviderErrors.FromTransport(ex, midStream: true);
                        break;
                    }

                    foreach (var item in decoded)
                    {
                        if (item is ProviderStreamEvent.End end)
                        {
                            yield return new ProviderStreamEvent.End(Shown(end.Reply, showProviderText));
                            yield break;
                        }

                        yield return item;
                    }
                }
            }

            // The stream closed with no terminal event and no complete answer: it was cut.
            last ??= ProviderReply.Failed(error ?? ProviderErrors.Create("stream_interrupted"));
            yield return new ProviderStreamEvent.End(Shown(last, showProviderText));
        }
    }

    /// <summary>
    /// A server on a private network can be any internal service, and its error text would
    /// tell the user what that service answers. Its text is shown only when the answer came
    /// from a public address.
    /// </summary>
    private static ProviderReply Shown(ProviderReply reply, bool showProviderText) =>
        reply.Error is { } error && !showProviderText ? reply with { Error = error.WithoutProviderText() } : reply;

    private static async Task<ProviderError> ErrorAsync(
        HttpResponseMessage response, Func<JsonElement, (string? Code, string? Message)> readError, CancellationToken ct)
    {
        (string? Code, string? Message) vendor = (null, null);
        try
        {
            if (await OutboundBody.ReadElementAsync(response, ct) is { ValueKind: JsonValueKind.Object } body)
                vendor = readError(body);
        }
        catch (OutboundException)
        {
            // The status is enough to name the error.
        }

        return ProviderErrors.FromStatus((int)response.StatusCode, vendor.Code, vendor.Message, response.Headers.RetryAfter?.Delta);
    }
}

/// <summary>Reads of untyped JSON that give null for anything missing or of another kind.</summary>
internal static class JsonReads
{
    public static JsonElement? Get(this JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
        && value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
            ? value
            : null;

    public static JsonElement? Get(this JsonElement? element, string name) => element?.Get(name);

    public static string? Text(this JsonElement? element) =>
        element is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;

    /// <summary>A string, or a number as text: vendors use both for an error code.</summary>
    public static string? Word(this JsonElement? element) => element switch
    {
        { ValueKind: JsonValueKind.String } value => value.GetString(),
        { ValueKind: JsonValueKind.Number } value => value.GetRawText(),
        _ => null,
    };

    /// <summary>
    /// A token count. A number that is not a whole number in the range of <see cref="int"/>
    /// is given as -1, which the meter records as "outside the sane range" and not as "not
    /// reported".
    /// </summary>
    public static int? Int(this JsonElement? element) =>
        element is not { ValueKind: JsonValueKind.Number } value ? null : value.TryGetInt32(out var number) ? number : -1;

    public static IEnumerable<JsonElement> Items(this JsonElement? element) =>
        element is { ValueKind: JsonValueKind.Array } value ? value.EnumerateArray() : [];

    /// <summary>The sum of the counts that are present. A negative count, or a sum past the range of <see cref="int"/>, gives -1.</summary>
    public static int? Sum(params int?[] counts)
    {
        if (counts.All(c => c is null))
            return null;

        long sum = 0;
        foreach (var count in counts)
        {
            if (count < 0)
                return -1;
            sum += count ?? 0;
        }

        return sum > int.MaxValue ? -1 : (int)sum;
    }
}
