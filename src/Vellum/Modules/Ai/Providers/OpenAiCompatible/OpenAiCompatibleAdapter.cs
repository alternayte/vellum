using System.Net.ServerSentEvents;
using System.Text.Json;
using System.Text.Json.Nodes;
using Refit;
using Vellum.Shared.Outbound;

namespace Vellum.Modules.Ai.Providers.OpenAiCompatible;

/// <summary>The Chat Completions shape that Ollama, LM Studio, OpenRouter and similar servers implement.</summary>
internal interface IOpenAiCompatibleApi
{
    [Post("/chat/completions")]
    Task<HttpResponseMessage> CreateChatCompletion(
        [Body(buffered: true)] JsonObject body,
        [Property(OutboundCall.PropertyName)] OutboundCall call,
        CancellationToken ct);
}

/// <summary>
/// The base URL comes from the provider entry and the API key is optional. The request uses
/// <c>max_tokens</c>: OpenAI now prefers <c>max_completion_tokens</c>, and the compatible
/// servers implement the older name. A server can leave out <c>usage</c>, <c>model</c> and
/// <c>finish_reason</c>; the token counts are then unknown and never guessed.
/// </summary>
internal sealed class OpenAiCompatibleAdapter(IOpenAiCompatibleApi api) : IAiProviderAdapter
{
    public string Kind => "openai-compatible";
    public Uri? DefaultBaseUrl => null;
    public bool RequiresApiKey => false;

    public OutboundCredential Credential(string apiKey) => new("Authorization", "Bearer", apiKey);

    public Task<ProviderReply> CompleteAsync(ProviderRequest request, CancellationToken ct) =>
        ProviderHttp.SendAsync(
            () => api.CreateChatCompletion(Body(request, stream: false), ProviderHttp.Call(request, "chat-completions", streaming: false), ct),
            Read, ReadError, ct);

    public IAsyncEnumerable<ProviderStreamEvent> StreamAsync(ProviderRequest request, CancellationToken ct)
    {
        string? model = null, finish = null;
        int? input = null, output = null;
        var refused = false;

        return ProviderHttp.StreamAsync(
            () => api.CreateChatCompletion(Body(request, stream: true), ProviderHttp.Call(request, "chat-completions-stream", streaming: true), ct),
            Decode, ReadError, ct);

        IReadOnlyList<ProviderStreamEvent> Decode(SseItem<string> item)
        {
            // The terminal event of this API is a literal, not JSON.
            if (item.Data.Trim() == "[DONE]")
                return [new ProviderStreamEvent.End(Reply(finish, refused, "", input, output, model))];

            if (OutboundBody.ParseElement(item.Data) is not { ValueKind: JsonValueKind.Object } data)
                return [];

            if (data.Get("error") is not null)
            {
                var (code, detail) = ReadError(data);
                return [new ProviderStreamEvent.End(ProviderReply.Failed(FromErrorCode(code, detail)))];
            }

            model = data.Get("model").Text() ?? model;
            input = data.Get("usage").Get("prompt_tokens").Int() ?? input;
            output = data.Get("usage").Get("completion_tokens").Int() ?? output;

            var choice = data.Get("choices").Items().FirstOrDefault();
            finish = choice.Get("finish_reason").Text() ?? finish;
            refused |= choice.Get("delta").Get("refusal").Text() is { Length: > 0 };
            return choice.Get("delta").Get("content").Text() is { Length: > 0 } text ? [new ProviderStreamEvent.Delta(text)] : [];
        }
    }

    private static JsonObject Body(ProviderRequest request, bool stream)
    {
        var messages = new JsonArray(new JsonObject { ["role"] = "system", ["content"] = request.System });
        foreach (var message in request.Messages)
        {
            messages.Add(new JsonObject
            {
                ["role"] = message.Role == AiRole.Assistant ? "assistant" : "user",
                ["content"] = message.Text,
            });
        }

        var body = new JsonObject
        {
            ["model"] = request.Target.Model,
            ["messages"] = messages,
            ["max_tokens"] = request.MaxOutputTokens,
        };
        if (request.Target.Temperature is { } temperature)
            body["temperature"] = temperature;
        if (stream)
        {
            body["stream"] = true;
            body["stream_options"] = new JsonObject { ["include_usage"] = true };
        }

        if (request.NativeSchema is { } schema)
        {
            body["response_format"] = new JsonObject
            {
                ["type"] = "json_schema",
                ["json_schema"] = new JsonObject
                {
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
        // Some gateways answer 200 with an error object.
        if (body.Get("error") is not null)
        {
            var (code, detail) = ReadError(body);
            return ProviderReply.Failed(FromErrorCode(code, detail));
        }

        if (body.Get("choices").Items().FirstOrDefault() is not { ValueKind: JsonValueKind.Object } choice)
            return ProviderReply.Failed(ProviderErrors.Create("malformed_response"));

        var message = choice.Get("message");
        return Reply(
            choice.Get("finish_reason").Text(), message.Get("refusal").Text() is { Length: > 0 },
            message.Get("content").Text() ?? "",
            body.Get("usage").Get("prompt_tokens").Int(), body.Get("usage").Get("completion_tokens").Int(), body.Get("model").Text());
    }

    private static ProviderReply Reply(string? finish, bool refused, string text, int? input, int? output, string? model) =>
        refused ? new(ProviderStop.Refused, "", "refusal", input, output, model) : finish switch
        {
            "length" => new(ProviderStop.Truncated, text, null, input, output, model),
            "content_filter" => new(ProviderStop.Refused, "", "content_filter", input, output, model),
            "tool_calls" or "function_call" => new(ProviderStop.Completed, "", null, input, output, model, ProviderErrors.Create("unexpected_stop")),
            _ => new(ProviderStop.Completed, text, null, input, output, model),
        };

    private static (string? Code, string? Message) ReadError(JsonElement body)
    {
        // The error is an object on most servers and a plain string on some.
        var error = body.Get("error");
        return error is { ValueKind: JsonValueKind.String }
            ? (null, error.Text())
            : (error.Get("code").Word() ?? error.Get("type").Text(), error.Get("message").Text());
    }

    // An error inside a 200 answer or a stream. Gateways put the HTTP status in the code.
    private static ProviderError FromErrorCode(string? code, string? detail) =>
        int.TryParse(code, out var status) && status is >= 400 and < 600
            ? ProviderErrors.FromStatus(status, null, detail)
            : code switch
            {
                "invalid_api_key" => ProviderErrors.Create("invalid_api_key"),
                "model_not_found" => ProviderErrors.Create("model_not_found"),
                "rate_limit_exceeded" => ProviderErrors.Create("rate_limited"),
                "insufficient_quota" => ProviderErrors.Create("quota_exceeded"),
                _ => ProviderErrors.Create("provider_unavailable"),
            };
}
