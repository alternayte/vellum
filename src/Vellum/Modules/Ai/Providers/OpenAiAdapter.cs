using System.Net.ServerSentEvents;
using System.Text.Json;
using System.Text.Json.Nodes;
using Refit;
using Vellum.Shared.Outbound;

namespace Vellum.Modules.Ai.Providers;

/// <summary>The OpenAI Responses API.</summary>
internal interface IOpenAiApi
{
    [Post("/responses")]
    Task<HttpResponseMessage> CreateResponse(
        [Body(buffered: true)] JsonObject body,
        [Property(OutboundCall.PropertyName)] OutboundCall call,
        CancellationToken ct);
}

/// <summary>
/// Structured output uses <c>text.format</c> with a strict JSON Schema. <c>store</c> is false,
/// so OpenAI does not keep the response for later retrieval. <c>max_output_tokens</c> counts
/// reasoning tokens and visible output together and has a minimum of
/// <see cref="MinOutputTokens"/> in the API's schema; a smaller limit is refused here and
/// nothing is sent. A billing error is not a rate limit: the codes that say the account
/// cannot pay map to <c>quota_exceeded</c> in a status, in a failed response and in a stream.
/// </summary>
internal sealed class OpenAiAdapter(IOpenAiApi api) : IAiProviderAdapter
{
    public const int MinOutputTokens = 16;

    public string Kind => "openai";
    public Uri? DefaultBaseUrl { get; } = new("https://api.openai.com/v1");
    public bool RequiresApiKey => true;

    public OutboundCredential Credential(string apiKey) => new("Authorization", "Bearer", apiKey);

    public int OutputTokenCeiling(int maxOutputTokens) => maxOutputTokens;

    public Task<ProviderReply> CompleteAsync(ProviderRequest request, CancellationToken ct) =>
        request.MaxOutputTokens < MinOutputTokens
            ? Task.FromResult(ProviderReply.Failed(ProviderErrors.Create("output_limit_too_small")))
            : ProviderHttp.SendAsync(
                () => api.CreateResponse(Body(request, stream: false), ProviderHttp.Call(request, "responses", streaming: false), ct),
                Read, ReadError, request.Progress, ct);

    public IAsyncEnumerable<ProviderStreamEvent> StreamAsync(ProviderRequest request, CancellationToken ct) =>
        request.MaxOutputTokens < MinOutputTokens
            ? TooSmall()
            : ProviderHttp.StreamAsync(
                () => api.CreateResponse(Body(request, stream: true), ProviderHttp.Call(request, "responses-stream", streaming: true), ct),
                Decode,
                // response.completed, response.incomplete or response.failed ends a response. A stream without one was cut.
                () => null,
                ReadError, request.Progress, ct);

    private static IAsyncEnumerable<ProviderStreamEvent> TooSmall() =>
        new ProviderStreamEvent[] { new ProviderStreamEvent.End(ProviderReply.Failed(ProviderErrors.Create("output_limit_too_small"))) }
            .ToAsyncEnumerable();

    private static IReadOnlyList<ProviderStreamEvent> Decode(SseItem<string> item)
    {
        if (OutboundBody.ParseElement(item.Data) is not { ValueKind: JsonValueKind.Object } data)
            return [];

        switch (data.Get("type").Text() ?? item.EventType)
        {
            case "response.output_text.delta":
                return data.Get("delta").Text() is { Length: > 0 } text ? [new ProviderStreamEvent.Delta(text)] : [];
            case "response.completed" or "response.incomplete" or "response.failed":
                return [new ProviderStreamEvent.End(data.Get("response") is { } response
                    ? Read(response)
                    : ProviderReply.Failed(ProviderErrors.Create("malformed_response")))];
            case "error":
                return [new ProviderStreamEvent.End(ProviderReply.Failed(
                    FromErrorCode(data.Get("code").Word(), data.Get("message").Text())))];
            default:
                return [];
        }
    }

    private static JsonObject Body(ProviderRequest request, bool stream)
    {
        var body = new JsonObject
        {
            ["model"] = request.Target.Model,
            ["instructions"] = request.System,
            ["input"] = new JsonArray(request.Messages.Select(m => (JsonNode)new JsonObject
            {
                ["role"] = m.Role == AiRole.Assistant ? "assistant" : "user",
                ["content"] = m.Text,
            }).ToArray()),
            ["max_output_tokens"] = request.MaxOutputTokens,
            ["store"] = false,
        };
        if (request.Target.Temperature is { } temperature)
            body["temperature"] = temperature;
        if (stream)
            body["stream"] = true;
        if (request.NativeSchema is { } schema)
        {
            body["text"] = new JsonObject
            {
                ["format"] = new JsonObject
                {
                    ["type"] = "json_schema",
                    ["name"] = request.SchemaName,
                    ["schema"] = JsonNode.Parse(schema.GetRawText()),
                    ["strict"] = true,
                },
            };
        }

        return body;
    }

    private static ProviderReply Read(JsonElement body)
    {
        var input = body.Get("usage").Get("input_tokens").Int();
        var output = body.Get("usage").Get("output_tokens").Int();
        var model = body.Get("model").Text();

        var parts = body.Get("output").Items()
            .Where(item => item.Get("type").Text() == "message")
            .SelectMany(item => item.Get("content").Items())
            .ToList();
        var text = string.Concat(parts.Where(p => p.Get("type").Text() == "output_text").Select(p => p.Get("text").Text()));
        var refused = parts.Any(p => p.Get("type").Text() == "refusal");

        switch (body.Get("status").Text())
        {
            case "completed":
                return refused
                    ? new(ProviderStop.Refused, "", "refusal", input, output, model)
                    : new(ProviderStop.Completed, text, null, input, output, model);
            case "incomplete":
                // The reference lists max_output_tokens, content_filter, max_messages and
                // steered. Only the first is a cut answer and only the second is a refusal.
                var reason = body.Get("incomplete_details").Get("reason").Text();
                return reason switch
                {
                    "max_output_tokens" => new(ProviderStop.Truncated, text, null, input, output, model),
                    "content_filter" => new(ProviderStop.Refused, "", "content_filter", input, output, model),
                    _ => new(ProviderStop.Completed, "", null, input, output, model, ProviderErrors.UnexpectedStop(reason)),
                };
            case "failed":
                var error = body.Get("error");
                return ProviderReply.Failed(FromErrorCode(error.Get("code").Word(), error.Get("message").Text()));
            default:
                return ProviderReply.Failed(ProviderErrors.Create("malformed_response"));
        }
    }

    // A billing error names its cause in the code and can keep the broader type insufficient_quota.
    private static (string? Code, string? Message) ReadError(JsonElement body)
    {
        var error = body.Get("error");
        var code = error.Get("code").Word();
        var type = error.Get("type").Text();
        return (ProviderErrors.IsBillingWord(type) ? type : code ?? type, error.Get("message").Text());
    }

    // A failed response and a stream error carry a code and no status.
    private static ProviderError FromErrorCode(string? code, string? detail) => code switch
    {
        "rate_limit_exceeded" or "slow_down" => ProviderErrors.Create("rate_limited"),
        "invalid_api_key" => ProviderErrors.Create("invalid_api_key"),
        "model_not_found" => ProviderErrors.Create("model_not_found"),
        _ when ProviderErrors.IsBillingWord(code) => ProviderErrors.Create("quota_exceeded"),
        "invalid_prompt" => ProviderErrors.Create("invalid_request", detail),
        "unsupported_parameter" or "unsupported_value" => ProviderErrors.Rejected(code, detail),
        _ => ProviderErrors.Create("provider_unavailable"),
    };
}
