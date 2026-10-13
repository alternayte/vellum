using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace Vellum.Tests.Modules.Mcp;

/// <summary>
/// The MCP streamable HTTP transport by hand: one JSON-RPC message per POST, the reply as
/// JSON or as one server-sent event, and the session ID the server gives on <c>initialize</c>.
/// </summary>
internal sealed class McpWire(HttpClient client)
{
    private string? _session;
    private int _nextId;

    public static JsonObject Initialize(int id = 1) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id,
        ["method"] = "initialize",
        ["params"] = new JsonObject
        {
            ["protocolVersion"] = "2025-03-26",
            ["capabilities"] = new JsonObject(),
            ["clientInfo"] = new JsonObject { ["name"] = "vellum-tests", ["version"] = "1" },
        },
    };

    public async Task<HttpResponseMessage> PostAsync(JsonObject message)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(message.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        if (_session is not null)
            request.Headers.Add("Mcp-Session-Id", _session);
        var response = await client.SendAsync(request);
        if (response.Headers.TryGetValues("Mcp-Session-Id", out var session))
            _session = session.Single();
        return response;
    }

    /// <summary>Sends a request and returns its JSON-RPC response. A status other than 200 fails the test.</summary>
    public async Task<JsonObject> RequestAsync(string method, JsonObject? parameters = null)
    {
        var id = ++_nextId;
        var message = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method };
        if (parameters is not null)
            message["params"] = parameters;
        using var response = await PostAsync(message);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{method}: {(int)response.StatusCode}: {text}");

        if (response.Content.Headers.ContentType?.MediaType != "text/event-stream")
            return JsonNode.Parse(text)!.AsObject();

        var replies = text.Split('\n')
            .Where(line => line.StartsWith("data:", StringComparison.Ordinal))
            .Select(line => JsonNode.Parse(line[5..])!.AsObject())
            .Where(reply => reply["id"]?.GetValue<int>() == id)
            .ToList();
        return Assert.Single(replies);
    }

    public async Task<JsonObject> InitializeAsync()
    {
        var id = ++_nextId;
        using var response = await PostAsync(Initialize(id));
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"initialize: {(int)response.StatusCode}: {text}");
        var reply = response.Content.Headers.ContentType?.MediaType == "text/event-stream"
            ? JsonNode.Parse(text.Split('\n').First(line => line.StartsWith("data:", StringComparison.Ordinal))[5..])!.AsObject()
            : JsonNode.Parse(text)!.AsObject();
        Assert.NotNull(reply["result"]);

        using var initialized = await PostAsync(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/initialized" });
        Assert.True(initialized.IsSuccessStatusCode, $"initialized: {(int)initialized.StatusCode}");
        return reply;
    }

    /// <summary>Calls a tool. Returns whether the result is an error, and its text.</summary>
    public async Task<(bool IsError, string Text)> ToolAsync(string name, JsonObject arguments)
    {
        var reply = await RequestAsync("tools/call", new JsonObject { ["name"] = name, ["arguments"] = arguments });
        if (reply["error"] is { } error)
            return (true, error.ToJsonString());
        var result = reply["result"]!;
        var text = string.Concat(result["content"]!.AsArray().Select(c => c!["text"]?.GetValue<string>()));
        return (result["isError"]?.GetValue<bool>() ?? false, text);
    }
}
