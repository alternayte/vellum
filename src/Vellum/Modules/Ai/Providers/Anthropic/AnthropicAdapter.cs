using System.Net.ServerSentEvents;
using System.Text.Json;
using System.Text.Json.Nodes;
using Refit;
using Vellum.Shared.Outbound;

namespace Vellum.Modules.Ai.Providers.Anthropic;

/// <summary>The Anthropic Messages API, version <c>2023-06-01</c>.</summary>
[Headers("anthropic-version: 2023-06-01")]
internal interface IAnthropicApi
{
    [Post("/v1/messages")]
    Task<HttpResponseMessage> CreateMessage(
        [Body(buffered: true)] JsonObject body,
        [Property(OutboundCall.PropertyName)] OutboundCall call,
        CancellationToken ct);
}

/// <summary>
/// Structured output uses <c>output_config.format</c>. The request sends no <c>thinking</c>
/// field, so each model uses its own default, and it sends <c>temperature</c> only when the
/// provider entry sets one, because the newer models reject it.
/// </summary>
internal sealed class AnthropicAdapter(IAnthropicApi api) : IAiProviderAdapter
{
    public string Kind => "anthropic";
    public Uri? DefaultBaseUrl { get; } = new("https://api.anthropic.com");
    public bool RequiresApiKey => true;

    public OutboundCredential Credential(string apiKey) => new("x-api-key", null, apiKey);

    public Task<ProviderReply> CompleteAsync(ProviderRequest request, CancellationToken ct) =>
        ProviderHttp.SendAsync(
            () => api.CreateMessage(Body(request, stream: false), ProviderHttp.Call(request, "messages", streaming: false), ct),
            Read, ReadError, ct);

    public IAsyncEnumerable<ProviderStreamEvent> StreamAsync(ProviderRequest request, CancellationToken ct)
    {
        string? model = null, stop = null, category = null;
        int? input = null, output = null;

        return ProviderHttp.StreamAsync(
            () => api.CreateMessage(Body(request, stream: true), ProviderHttp.Call(request, "messages-stream", streaming: true), ct),
            Decode, ReadError, ct);

        IReadOnlyList<ProviderStreamEvent> Decode(SseItem<string> item)
        {
            if (OutboundBody.ParseElement(item.Data) is not { ValueKind: JsonValueKind.Object } data)
                return [];

            switch (data.Get("type").Text() ?? item.EventType)
            {
                case "message_start":
                    var message = data.Get("message");
                    model = message.Get("model").Text();
                    input = InputTokens(message.Get("usage")) ?? input;
                    break;
                case "content_block_delta" when data.Get("delta").Get("type").Text() == "text_delta":
                    return data.Get("delta").Get("text").Text() is { Length: > 0 } text ? [new ProviderStreamEvent.Delta(text)] : [];
                case "message_delta":
                    stop = data.Get("delta").Get("stop_reason").Text() ?? stop;
                    category = data.Get("delta").Get("stop_details").Get("category").Text() ?? category;
                    input = InputTokens(data.Get("usage")) ?? input;
                    output = data.Get("usage").Get("output_tokens").Int() ?? output;
                    break;
                case "message_stop":
                    return [new ProviderStreamEvent.End(Reply(stop, category, "", input, output, model))];
                case "error":
                    var (code, detail) = ReadError(data);
                    return [new ProviderStreamEvent.End(ProviderReply.Failed(FromErrorType(code, detail)))];
            }

            // ping, thinking deltas and event types added later carry nothing Vellum reads.
            return [];
        }
    }

    private static JsonObject Body(ProviderRequest request, bool stream)
    {
        var body = new JsonObject
        {
            ["model"] = request.Target.Model,
            ["max_tokens"] = request.MaxOutputTokens,
            ["system"] = request.System,
            ["messages"] = new JsonArray(request.Messages.Select(m => (JsonNode)new JsonObject
            {
                ["role"] = m.Role == AiRole.Assistant ? "assistant" : "user",
                ["content"] = m.Text,
            }).ToArray()),
        };
        if (request.Target.Temperature is { } temperature)
            body["temperature"] = temperature;
        if (stream)
            body["stream"] = true;
        if (request.NativeSchema is { } schema)
        {
            body["output_config"] = new JsonObject
            {
                ["format"] = new JsonObject { ["type"] = "json_schema", ["schema"] = JsonNode.Parse(schema.GetRawText()) },
            };
        }

        return body;
    }

    private static ProviderReply Read(JsonElement body)
    {
        if (body.Get("type").Text() == "error")
        {
            var (code, detail) = ReadError(body);
            return ProviderReply.Failed(FromErrorType(code, detail));
        }

        // Thinking blocks and other block types are not part of the answer.
        var text = string.Concat(body.Get("content").Items()
            .Where(block => block.Get("type").Text() == "text")
            .Select(block => block.Get("text").Text()));

        return Reply(
            body.Get("stop_reason").Text(), body.Get("stop_details").Get("category").Text(), text,
            InputTokens(body.Get("usage")), body.Get("usage").Get("output_tokens").Int(), body.Get("model").Text());
    }

    private static ProviderReply Reply(string? stop, string? category, string text, int? input, int? output, string? model) => stop switch
    {
        "end_turn" or "stop_sequence" => new(ProviderStop.Completed, text, null, input, output, model),
        "max_tokens" or "model_context_window_exceeded" => new(ProviderStop.Truncated, text, null, input, output, model),
        "refusal" => new(ProviderStop.Refused, "", category ?? "refusal", input, output, model),
        null => ProviderReply.Failed(ProviderErrors.Create("malformed_response")),
        _ => new(ProviderStop.Completed, "", null, input, output, model, ProviderErrors.Create("unexpected_stop")),
    };

    // Cached input is reported apart from the rest; the three counts together are the input.
    private static int? InputTokens(JsonElement? usage) => JsonReads.Sum(
        usage.Get("input_tokens").Int(), usage.Get("cache_creation_input_tokens").Int(), usage.Get("cache_read_input_tokens").Int());

    private static (string? Code, string? Message) ReadError(JsonElement body) =>
        (body.Get("error").Get("type").Text(), body.Get("error").Get("message").Text());

    // An error event inside a 200 stream has no status. Its type names the same errors.
    private static ProviderError FromErrorType(string? type, string? detail) => type switch
    {
        "authentication_error" => ProviderErrors.Create("invalid_api_key"),
        "permission_error" => ProviderErrors.Create("permission_denied"),
        "not_found_error" => ProviderErrors.Create("model_not_found"),
        "rate_limit_error" => ProviderErrors.Create("rate_limited"),
        "billing_error" => ProviderErrors.Create("quota_exceeded"),
        "request_too_large" => ProviderErrors.Create("request_too_large"),
        "invalid_request_error" => ProviderErrors.Create("invalid_request", detail),
        "timeout_error" => ProviderErrors.Create("timeout"),
        _ => ProviderErrors.Create("provider_unavailable"),
    };
}
