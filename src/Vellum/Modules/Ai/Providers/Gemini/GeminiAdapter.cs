using System.Net.ServerSentEvents;
using System.Text.Json;
using System.Text.Json.Nodes;
using Refit;
using Vellum.Shared.Outbound;

namespace Vellum.Modules.Ai.Providers.Gemini;

/// <summary>The Gemini API <c>generateContent</c> and <c>streamGenerateContent</c> methods (v1beta).</summary>
internal interface IGeminiApi
{
    [Post("/v1beta/models/{model}:generateContent")]
    Task<HttpResponseMessage> GenerateContent(
        string model,
        [Body(buffered: true)] JsonObject body,
        [Property(OutboundCall.PropertyName)] OutboundCall call,
        CancellationToken ct);

    [Post("/v1beta/models/{model}:streamGenerateContent?alt=sse")]
    Task<HttpResponseMessage> StreamGenerateContent(
        string model,
        [Body(buffered: true)] JsonObject body,
        [Property(OutboundCall.PropertyName)] OutboundCall call,
        CancellationToken ct);
}

/// <summary>
/// The key goes in the <c>x-goog-api-key</c> header and never in the query string.
/// Structured output uses <c>generationConfig.responseFormat.text</c>, which replaced
/// <c>responseMimeType</c> and <c>responseJsonSchema</c>. A stream has no separate end event:
/// the chunk that carries <c>finishReason</c> is the end.
/// </summary>
internal sealed class GeminiAdapter(IGeminiApi api) : IAiProviderAdapter
{
    public string Kind => "gemini";
    public Uri? DefaultBaseUrl { get; } = new("https://generativelanguage.googleapis.com");
    public bool RequiresApiKey => true;

    public OutboundCredential Credential(string apiKey) => new("x-goog-api-key", null, apiKey);

    public Task<ProviderReply> CompleteAsync(ProviderRequest request, CancellationToken ct) =>
        ProviderHttp.SendAsync(
            () => api.GenerateContent(Model(request), Body(request), ProviderHttp.Call(request, "generate-content", streaming: false), ct),
            Read, ReadError, ct);

    public IAsyncEnumerable<ProviderStreamEvent> StreamAsync(ProviderRequest request, CancellationToken ct)
    {
        string? model = null;
        int? input = null, output = null;

        return ProviderHttp.StreamAsync(
            () => api.StreamGenerateContent(Model(request), Body(request), ProviderHttp.Call(request, "stream-generate-content", streaming: true), ct),
            Decode, ReadError, ct);

        IReadOnlyList<ProviderStreamEvent> Decode(SseItem<string> item)
        {
            if (OutboundBody.ParseElement(item.Data) is not { ValueKind: JsonValueKind.Object } data)
                return [];

            var chunk = ReadChunk(data, out var terminal);
            model = chunk.ReportedModel ?? model;
            input = chunk.InputTokens ?? input;
            output = chunk.OutputTokens ?? output;

            var events = new List<ProviderStreamEvent>(2);
            if (chunk.Error is null && chunk.Text.Length > 0)
                events.Add(new ProviderStreamEvent.Delta(chunk.Text));
            if (terminal)
                events.Add(new ProviderStreamEvent.End(chunk with { Text = "", ReportedModel = model, InputTokens = input, OutputTokens = output }));
            return events;
        }
    }

    // Settings can hold the name with or without the resource prefix.
    private static string Model(ProviderRequest request) =>
        request.Target.Model.StartsWith("models/", StringComparison.Ordinal) ? request.Target.Model[7..] : request.Target.Model;

    private static JsonObject Body(ProviderRequest request)
    {
        var config = new JsonObject { ["maxOutputTokens"] = request.MaxOutputTokens };
        if (request.Target.Temperature is { } temperature)
            config["temperature"] = temperature;
        if (request.NativeSchema is { } schema)
        {
            config["responseFormat"] = new JsonObject
            {
                ["text"] = new JsonObject { ["mimeType"] = "APPLICATION_JSON", ["schema"] = JsonNode.Parse(schema.GetRawText()) },
            };
        }

        return new JsonObject
        {
            ["systemInstruction"] = new JsonObject { ["parts"] = new JsonArray(new JsonObject { ["text"] = request.System }) },
            ["contents"] = new JsonArray(request.Messages.Select(m => (JsonNode)new JsonObject
            {
                ["role"] = m.Role == AiRole.Assistant ? "model" : "user",
                ["parts"] = new JsonArray(new JsonObject { ["text"] = m.Text }),
            }).ToArray()),
            ["generationConfig"] = config,
        };
    }

    private static ProviderReply Read(JsonElement body)
    {
        var reply = ReadChunk(body, out var terminal);
        return terminal ? reply : ProviderReply.Failed(ProviderErrors.Create("malformed_response"));
    }

    /// <param name="terminal">False for a stream chunk that is not the last one.</param>
    private static ProviderReply ReadChunk(JsonElement body, out bool terminal)
    {
        terminal = true;
        if (body.Get("error") is { } error)
        {
            var (word, detail) = ReadError(body);
            return ProviderReply.Failed(ProviderErrors.FromStatus(error.Get("code").Int() ?? 500, word, detail));
        }

        var usage = body.Get("usageMetadata");
        var input = usage.Get("promptTokenCount").Int();
        // Thinking tokens are billed as output and reported apart from the answer's tokens.
        var output = JsonReads.Sum(usage.Get("candidatesTokenCount").Int(), usage.Get("thoughtsTokenCount").Int());
        var model = body.Get("modelVersion").Text();

        if (body.Get("promptFeedback").Get("blockReason").Text() is { } blocked)
            return new(ProviderStop.Refused, "", blocked.ToLowerInvariant(), input, output, model);

        var candidate = body.Get("candidates").Items().FirstOrDefault();
        var text = string.Concat(candidate.Get("content").Get("parts").Items()
            .Where(part => part.Get("thought") is not { ValueKind: JsonValueKind.True })
            .Select(part => part.Get("text").Text()));

        var finish = candidate.Get("finishReason").Text();
        switch (finish)
        {
            case null or "" or "FINISH_REASON_UNSPECIFIED":
                terminal = false;
                return new(ProviderStop.Completed, text, null, input, output, model);
            case "STOP":
                return new(ProviderStop.Completed, text, null, input, output, model);
            case "MAX_TOKENS":
                return new(ProviderStop.Truncated, text, null, input, output, model);
            case "SAFETY" or "RECITATION" or "LANGUAGE" or "BLOCKLIST" or "PROHIBITED_CONTENT" or "SPII":
                return new(ProviderStop.Refused, "", finish.ToLowerInvariant(), input, output, model);
            default:
                return new(ProviderStop.Completed, "", null, input, output, model, ProviderErrors.Create("unexpected_stop"));
        }
    }

    // "API key not valid" is a 400 whose details name this reason.
    private static (string? Code, string? Message) ReadError(JsonElement body)
    {
        var error = body.Get("error");
        var word = body.GetRawText().Contains("API_KEY_INVALID", StringComparison.Ordinal) ? "api_key_invalid" : error.Get("status").Text();
        return (word, error.Get("message").Text());
    }
}
