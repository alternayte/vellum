using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Vellum.Shared.Outbound;

namespace Vellum.Modules.Ai.Providers;

/// <summary>Where one call goes: a provider entry of the workspace's settings, resolved.</summary>
internal sealed record ProviderTarget(
    Guid ProviderId, string Kind, string Model, Uri BaseUrl, OutboundCredential? Credential,
    bool AllowPrivateNetwork, TimeSpan Timeout, double? Temperature);

internal sealed record ProviderMessage(AiRole Role, string Text);

/// <param name="NativeSchema">Set when the provider's own structured-output mode is used.</param>
/// <param name="SchemaName">A name for the schema, for the providers that ask for one.</param>
internal sealed record ProviderRequest(
    ProviderTarget Target, string System, IReadOnlyList<ProviderMessage> Messages,
    JsonElement? NativeSchema, string SchemaName, int MaxOutputTokens);

internal enum ProviderStop
{
    Completed,
    /// <summary>The output limit cut the answer.</summary>
    Truncated,
    Refused,
}

/// <param name="Rejected">The provider did not start to generate, so the call cost nothing.</param>
internal sealed record ProviderError(string Code, string Message, TimeSpan? RetryAfter, bool Rejected);

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

    Task<ProviderReply> CompleteAsync(ProviderRequest request, CancellationToken ct);

    /// <summary>Text deltas, then exactly one <see cref="ProviderStreamEvent.End"/>.</summary>
    IAsyncEnumerable<ProviderStreamEvent> StreamAsync(ProviderRequest request, CancellationToken ct);
}

/// <summary>The error words every adapter uses, with the sentence a user reads.</summary>
internal static class ProviderErrors
{
    public static ProviderError Create(string code, string? detail = null, TimeSpan? retryAfter = null) => new(
        code,
        Sentence(code) + (string.IsNullOrWhiteSpace(detail) ? "" : $" The provider said: {Clean(detail)}"),
        retryAfter,
        code is "invalid_api_key" or "permission_denied" or "model_not_found" or "rate_limited" or "quota_exceeded"
            or "invalid_request" or "request_too_large" or "url_refused" or "connection_failed" or "unexpected_redirect");

    private static string Sentence(string code) => code switch
    {
        "invalid_api_key" => "The provider refused the API key. Save a valid key for this provider in the workspace secrets.",
        "permission_denied" => "The API key is valid, and the provider does not allow it to use this model or endpoint.",
        "model_not_found" => "The provider has no such model at this base URL. Check the model name and the base URL.",
        "rate_limited" => "The provider's rate limit is reached. Try again later.",
        "quota_exceeded" => "The provider account has no quota or credit left. Check the billing of the provider account.",
        "invalid_request" => "The provider rejected the request. A provider setting (model, temperature, structured output) or the schema does not fit this model.",
        "request_too_large" => "The request is larger than the provider accepts.",
        "provider_unavailable" => "The provider reported a server error or is overloaded. Try again later.",
        "timeout" => "The provider did not answer within the configured timeout.",
        "connection_failed" => "No connection to the provider's base URL could be made. Check the base URL.",
        "connection_lost" => "The connection to the provider broke before the answer arrived.",
        "unexpected_stop" => "The provider stopped for a reason that Vellum does not use, for example a tool call.",
        "url_refused" => "The instance policy refuses the provider's base URL. It must be a public http or https address.",
        "response_too_large" => "The provider's answer is larger than Vellum accepts.",
        "malformed_response" => "The answer was not in the provider's documented format. Check the base URL and the provider kind.",
        "unexpected_redirect" => "The provider's base URL redirects to another address. Set the base URL to the final address.",
        "stream_interrupted" => "The provider's stream ended before it was complete.",
        _ => "The provider reported an error.",
    };

    // A provider's own sentence helps with a rejected request. It is cut, cleared of control
    // characters and passed through the redactor; it goes to the caller and never to a log.
    private static string Clean(string detail)
    {
        var text = SecretRedactor.Redact(new string(detail.Where(c => !char.IsControl(c)).Take(300).ToArray()));
        return text.EndsWith('.') ? text : text + ".";
    }

    public static ProviderError FromTransport(OutboundException ex, bool midStream = false) => Create(ex.Failure switch
    {
        OutboundFailure.UrlRefused => "url_refused",
        OutboundFailure.Timeout => "timeout",
        OutboundFailure.ConnectionFailed => "connection_failed",
        OutboundFailure.ResponseTooLarge => "response_too_large",
        _ => midStream ? "stream_interrupted" : "connection_lost",
    });

    /// <param name="vendorCode">The vendor's error word, when the body had one.</param>
    public static ProviderError FromStatus(int status, string? vendorCode, string? vendorMessage, TimeSpan? retryAfter = null)
    {
        var word = vendorCode?.ToLowerInvariant() ?? "";
        var quota = word.Contains("quota", StringComparison.Ordinal) || word.Contains("billing", StringComparison.Ordinal)
            || word.Contains("credit", StringComparison.Ordinal) || word.Contains("spend_limit", StringComparison.Ordinal)
            || word.Contains("usage_limit", StringComparison.Ordinal) || word is "payment_required";

        return status switch
        {
            >= 300 and < 400 => Create("unexpected_redirect"),
            401 => Create("invalid_api_key"),
            402 => Create("quota_exceeded"),
            403 => Create("permission_denied"),
            404 => Create("model_not_found"),
            408 or 504 => Create("timeout"),
            413 => Create("request_too_large"),
            429 => Create(quota ? "quota_exceeded" : "rate_limited", retryAfter: retryAfter),
            >= 500 => Create("provider_unavailable", retryAfter: retryAfter),
            _ when word.Contains("model_not_found", StringComparison.Ordinal) => Create("model_not_found"),
            _ when word is "api_key_invalid" or "invalid_api_key" => Create("invalid_api_key"),
            _ => Create("invalid_request", vendorMessage),
        };
    }
}

/// <summary>What the four adapters share: one way to send, to read an error and to read a stream.</summary>
internal static class ProviderHttp
{
    public const long MaxResponseBytes = 8 * 1024 * 1024;

    public static OutboundCall Call(ProviderRequest request, string operation, bool streaming) =>
        new(request.Target.Kind, operation, request.Target.BaseUrl, request.Target.AllowPrivateNetwork, request.Target.Timeout)
        {
            Credential = request.Target.Credential,
            MaxResponseBytes = MaxResponseBytes,
            Streaming = streaming,
        };

    /// <param name="readError">Reads the vendor's error word and sentence from an error body.</param>
    public static async Task<ProviderReply> SendAsync(
        Func<Task<HttpResponseMessage>> send,
        Func<JsonElement, ProviderReply> read,
        Func<JsonElement, (string? Code, string? Message)> readError,
        CancellationToken ct)
    {
        try
        {
            using var response = await OutboundSend.RunAsync(send);
            if (!response.IsSuccessStatusCode)
                return ProviderReply.Failed(await ErrorAsync(response, readError, ct));

            return await OutboundBody.ReadElementAsync(response, ct) is { ValueKind: JsonValueKind.Object } body
                ? read(body)
                : ProviderReply.Failed(ProviderErrors.Create("malformed_response"));
        }
        catch (OutboundException ex)
        {
            return ProviderReply.Failed(ProviderErrors.FromTransport(ex));
        }
    }

    /// <param name="decode">Turns one event's JSON into zero or more stream events. It keeps the state of the stream.</param>
    public static async IAsyncEnumerable<ProviderStreamEvent> StreamAsync(
        Func<Task<HttpResponseMessage>> send,
        Func<SseItem<string>, IReadOnlyList<ProviderStreamEvent>> decode,
        Func<JsonElement, (string? Code, string? Message)> readError,
        [EnumeratorCancellation] CancellationToken ct)
    {
        HttpResponseMessage? response = null;
        ProviderError? error = null;
        try
        {
            response = await OutboundSend.RunAsync(send);
            if (!response.IsSuccessStatusCode)
                error =await ErrorAsync(response, readError, ct);
        }
        catch (OutboundException ex)
        {
            error = ProviderErrors.FromTransport(ex);
        }

        // Disposing the response on any exit, a cancelled read included, ends the HTTP request.
        using (response)
        {
            if (error is null)
            {
                await using var events = OutboundBody.ReadEventsAsync(response!, ct).GetAsyncEnumerator(ct);
                while (error is null)
                {
                    IReadOnlyList<ProviderStreamEvent> decoded;
                    try
                    {
                        if (!await events.MoveNextAsync())
                            break;
                        decoded = decode(events.Current);
                    }
                    catch (OutboundException ex)
                    {
                        error = ProviderErrors.FromTransport(ex, midStream: true);
                        break;
                    }

                    foreach (var item in decoded)
                    {
                        yield return item;
                        if (item is ProviderStreamEvent.End)
                            yield break;
                    }
                }
            }

            // The stream closed with no terminal event: the answer is not complete.
            yield return new ProviderStreamEvent.End(ProviderReply.Failed(error ?? ProviderErrors.Create("stream_interrupted")));
        }
    }

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

    public static int? Int(this JsonElement? element) =>
        element is { ValueKind: JsonValueKind.Number } value && value.TryGetInt32(out var number) ? number : null;

    public static IEnumerable<JsonElement> Items(this JsonElement? element) =>
        element is { ValueKind: JsonValueKind.Array } value ? value.EnumerateArray() : [];

    public static int? Sum(params int?[] counts) => counts.Any(c => c is not null) ? counts.Sum(c => c ?? 0) : null;
}
