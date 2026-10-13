using System.Net.ServerSentEvents;
using System.Text.Json;
using System.Text.Json.Nodes;
using Refit;
using Vellum.Shared.Outbound;

namespace Vellum.Modules.Ai.Providers.Gemini;

/// <summary>
/// The Gemini API <c>generateContent</c> and <c>streamGenerateContent</c> methods (v1beta).
/// The model is a resource name of two parts, <c>models/{id}</c> or <c>tunedModels/{id}</c>;
/// the slash between them is part of the path and is not encoded.
/// </summary>
internal interface IGeminiApi
{
    [Post("/v1beta/{collection}/{model}:generateContent")]
    Task<HttpResponseMessage> GenerateContent(
        string collection,
        string model,
        [Body(buffered: true)] JsonObject body,
        [Property(OutboundCall.PropertyName)] OutboundCall call,
        CancellationToken ct);

    [Post("/v1beta/{collection}/{model}:streamGenerateContent?alt=sse")]
    Task<HttpResponseMessage> StreamGenerateContent(
        string collection,
        string model,
        [Body(buffered: true)] JsonObject body,
        [Property(OutboundCall.PropertyName)] OutboundCall call,
        CancellationToken ct);
}

/// <summary>
/// The key goes in the <c>x-goog-api-key</c> header and never in the query string.
/// Structured output uses <c>generationConfig.responseFormat.text</c> with the enum name
/// <c>APPLICATION_JSON</c> as its <c>mimeType</c>, as the REST reference gives it; the
/// reference marks <c>responseSchema</c> and <c>_responseJsonSchema</c> as deprecated in
/// favour of <c>responseFormat</c>.
/// <para>
/// A stream has no separate end event. The chunk that carries <c>finishReason</c> ends the
/// answer, and the reference does not say which chunk carries <c>usageMetadata</c>, so the
/// stream is read until the server closes it and the last counts are kept.
/// </para>
/// </summary>
internal sealed class GeminiAdapter(IGeminiApi api) : IAiProviderAdapter
{
    private const string Models = "models";
    private const string TunedModels = "tunedModels";

    public string Kind => "gemini";
    public Uri? DefaultBaseUrl { get; } = new("https://generativelanguage.googleapis.com");
    public bool RequiresApiKey => true;

    public OutboundCredential Credential(string apiKey) => new("x-goog-api-key", null, apiKey);

    // maxOutputTokens caps thinking tokens and answer tokens together.
    public int OutputTokenCeiling(int maxOutputTokens) => maxOutputTokens;

    public Task<ProviderReply> CompleteAsync(ProviderRequest request, CancellationToken ct)
    {
        var (collection, model) = Model(request);
        return ProviderHttp.SendAsync(
            () => api.GenerateContent(collection, model, Body(request), ProviderHttp.Call(request, "generate-content", streaming: false), ct),
            Read, ReadError, request.Progress, ct);
    }

    public IAsyncEnumerable<ProviderStreamEvent> StreamAsync(ProviderRequest request, CancellationToken ct)
    {
        var (collection, name) = Model(request);
        string? model = null;
        int? input = null, output = null;
        ProviderReply? finished = null;

        return ProviderHttp.StreamAsync(
            () => api.StreamGenerateContent(collection, name, Body(request), ProviderHttp.Call(request, "stream-generate-content", streaming: true), ct),
            Decode,
            // The answer is complete when a chunk gave a finish reason. Its counts are the last ones any chunk gave.
            () => finished is null ? null : finished with { ReportedModel = model, InputTokens = input, OutputTokens = output },
            ReadError, request.Progress, ct);

        IReadOnlyList<ProviderStreamEvent> Decode(SseItem<string> item)
        {
            if (OutboundBody.ParseElement(item.Data) is not { ValueKind: JsonValueKind.Object } data)
                return [];

            var chunk = ReadChunk(data, out var terminal);
            if (chunk.Error is not null)
                return [new ProviderStreamEvent.End(chunk)];

            model = chunk.ReportedModel ?? model;
            input = chunk.InputTokens ?? input;
            output = chunk.OutputTokens ?? output;

            // A chunk after the finish reason carries counts and no text of the answer.
            if (finished is not null)
                return [];
            if (terminal)
                finished = chunk with { Text = "" };
            return chunk.Text.Length > 0 ? [new ProviderStreamEvent.Delta(chunk.Text)] : [];
        }
    }

    /// <summary>Settings can hold the name with or without its collection: <c>gemini-x</c>, <c>models/gemini-x</c> or <c>tunedModels/x</c>.</summary>
    private static (string Collection, string Model) Model(ProviderRequest request)
    {
        var name = request.Target.Model;
        if (name.StartsWith(TunedModels + "/", StringComparison.Ordinal))
            return (TunedModels, name[(TunedModels.Length + 1)..]);
        return (Models, name.StartsWith(Models + "/", StringComparison.Ordinal) ? name[(Models.Length + 1)..] : name);
    }

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

    /// <param name="terminal">False for a stream chunk that does not end the answer.</param>
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
            return new(ProviderStop.Refused, "", Word(blocked), input, output, model);

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
            // CONTINUATION: the per-request token limit was reached and generation is not complete.
            case "MAX_TOKENS" or "CONTINUATION":
                return new(ProviderStop.Truncated, text, null, input, output, model);
            // Content that was flagged or filtered, and an account that policy limits or disables.
            case "SAFETY" or "RECITATION" or "LANGUAGE" or "BLOCKLIST" or "PROHIBITED_CONTENT" or "SPII"
                or "IMAGE_SAFETY" or "IMAGE_PROHIBITED_CONTENT" or "IMAGE_RECITATION"
                or "ESCALATION" or "PUP_LIMITED_DISABLED":
                return new(ProviderStop.Refused, "", Word(finish), input, output, model);
            // OTHER, tool-call reasons, image reasons that are not refusals, and any reason added later.
            default:
                return new(ProviderStop.Completed, "", null, input, output, model, ProviderErrors.UnexpectedStop(finish));
        }
    }

    private static string Word(string reason) => OutboundText.Clean(reason, 60).ToLowerInvariant();

    // "API key not valid" is a 400 whose details name this reason.
    private static (string? Code, string? Message) ReadError(JsonElement body)
    {
        var error = body.Get("error");
        var word = body.GetRawText().Contains("API_KEY_INVALID", StringComparison.Ordinal) ? "api_key_invalid" : error.Get("status").Text();
        return (word, error.Get("message").Text());
    }
}
