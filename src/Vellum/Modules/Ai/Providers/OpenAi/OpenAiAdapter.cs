using System.Net.ServerSentEvents;
using System.Text.Json;
using System.Text.Json.Nodes;
using Refit;
using Vellum.Shared.Outbound;

namespace Vellum.Modules.Ai.Providers.OpenAi;

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
/// so OpenAI does not keep the response for later retrieval.
/// </summary>
internal sealed class OpenAiAdapter(IOpenAiApi api) : IAiProviderAdapter
{
    public string Kind => "openai";
    public Uri? DefaultBaseUrl { get; } = new("https://api.openai.com/v1");
    public bool RequiresApiKey => true;

    public OutboundCredential Credential(string apiKey) => new("Authorization", "Bearer", apiKey);

    public Task<ProviderReply> CompleteAsync(ProviderRequest request, CancellationToken ct) =>
        ProviderHttp.SendAsync(
            () => api.CreateResponse(Body(request, stream: false), ProviderHttp.Call(request, "responses", streaming: false), ct),
            Read, ReadError, ct);

    public IAsyncEnumerable<ProviderStreamEvent> StreamAsync(ProviderRequest request, CancellationToken ct) =>
        ProviderHttp.StreamAsync(
            () => api.CreateResponse(Body(request, stream: true), ProviderHttp.Call(request, "responses-stream", streaming: true), ct),
            Decode, ReadError, ct);

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
                return body.Get("incomplete_details").Get("reason").Text() switch
                {
                    "max_output_tokens" => new(ProviderStop.Truncated, text, null, input, output, model),
                    "content_filter" => new(ProviderStop.Refused, "", "content_filter", input, output, model),
                    _ => new(ProviderStop.Completed, "", null, input, output, model, ProviderErrors.Create("unexpected_stop")),
                };
            case "failed":
                var error = body.Get("error");
                return ProviderReply.Failed(FromErrorCode(error.Get("code").Word(), error.Get("message").Text()));
            default:
                return ProviderReply.Failed(ProviderErrors.Create("malformed_response"));
        }
    }

    private static (string? Code, string? Message) ReadError(JsonElement body)
    {
        var error = body.Get("error");
        return (error.Get("code").Word() ?? error.Get("type").Text(), error.Get("message").Text());
    }

    // A failed response and a stream error carry a code and no status.
    private static ProviderError FromErrorCode(string? code, string? detail) => code switch
    {
        "rate_limit_exceeded" => ProviderErrors.Create("rate_limited"),
        "invalid_api_key" => ProviderErrors.Create("invalid_api_key"),
        "model_not_found" => ProviderErrors.Create("model_not_found"),
        "insufficient_quota" => ProviderErrors.Create("quota_exceeded"),
        "invalid_prompt" => ProviderErrors.Create("invalid_request", detail),
        _ => ProviderErrors.Create("provider_unavailable"),
    };
}
