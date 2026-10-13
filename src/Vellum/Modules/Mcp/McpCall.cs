using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Deedbox;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using Vellum.Modules.Identity.AgentTokens;
using Vellum.Modules.Workspaces.Entities;
using Vellum.Shared;

namespace Vellum.Modules.Mcp;

/// <summary>The answer of an endpoint that a tool called.</summary>
public sealed record EndpointReply(int Status, byte[] Body, IHeaderDictionary Headers)
{
    public bool Succeeded => Status is >= 200 and < 300;

    public JsonObject Json() => JsonNode.Parse(Body)!.AsObject();

    public string Text() => Encoding.UTF8.GetString(Body);
}

/// <summary>
/// One tool call, after the gate let it through: who calls, the tool, and the project the
/// gate checked. A tool reads the model through the services here, or calls the endpoint
/// that the HTTP API has for the same thing with <see cref="SendAsync"/>. The MCP endpoint
/// takes one message in one request, so one scope holds one call.
/// </summary>
public sealed class McpCall(IServiceProvider services, AgentEndpoints endpoints, IOptions<JsonOptions> json)
{
    private AgentTool _tool = null!;
    private ProjectEntity? _project;

    public ClaimsPrincipal User { get; private set; } = null!;

    /// <summary>The project the gate checked. Only <c>list_projects</c> has none.</summary>
    public ProjectEntity Project => _project ?? throw new InvalidOperationException($"The tool {_tool.Name} names no project.");

    public CancellationToken Aborted { get; private set; }

    internal void Begin(AgentTool tool, ClaimsPrincipal user, ProjectEntity? project, CancellationToken ct) =>
        (_tool, User, _project, Aborted) = (tool, user, project, ct);

    public T Service<T>() where T : notnull => services.GetRequiredService<T>();

    /// <summary>A value as the HTTP API writes it: the same names, the same enum words, the same IDs.</summary>
    public JsonNode? Node<T>(T value) => JsonSerializer.SerializeToNode(value, json.Value.SerializerOptions);

    /// <summary>
    /// Calls an endpoint of the HTTP API in this process, as the token: the handler, its
    /// filters, its role check, its validation and its limits are the ones a request over
    /// HTTP gets, and the events it appends name the token as their actor. The route is one
    /// that the HTTP boundary opens to the scope of the tool; its project is the one the gate
    /// checked, whatever the tool passes. A body larger than the endpoint takes over HTTP is
    /// refused here with the same error.
    /// </summary>
    public Task<EndpointReply> SendAsync(
        IReadOnlyDictionary<string, string>? values = null, IReadOnlyDictionary<string, string?>? query = null, JsonNode? body = null) =>
        SendToAsync(_tool.Endpoint ?? throw new InvalidOperationException($"The tool {_tool.Name} has no endpoint."), values, query, body);

    /// <summary><see cref="SendAsync"/> for a route that is not the one the catalog states for the tool.</summary>
    public async Task<EndpointReply> SendToAsync(
        HttpRoute route, IReadOnlyDictionary<string, string>? values = null, IReadOnlyDictionary<string, string?>? query = null, JsonNode? body = null)
    {
        if (endpoints.For(route, _tool.Scope) is not { } endpoint)
            throw new InvalidOperationException($"The tool {_tool.Name} calls {route}, which is not open to the scope {AgentScopes.Name(_tool.Scope)}.");

        var http = new DefaultHttpContext { RequestServices = services, User = User, RequestAborted = Aborted };
        http.SetEndpoint(endpoint);
        http.Request.Method = route.Method;
        http.Request.RouteValues = new RouteValueDictionary(values) { ["projectId"] = Project.Id.ToString() };
        http.Request.Path = "/" + string.Join('/', route.Pattern.Trim('/').Split('/').Select(part =>
            part.StartsWith('{') ? Uri.EscapeDataString(http.Request.RouteValues[part.Trim('{', '}').Split(':')[0]]?.ToString() ?? "") : part));
        http.Request.QueryString = QueryString.Create((query ?? new Dictionary<string, string?>()).Where(q => q.Value is not null));

        var response = new MemoryStream();
        http.Response.Body = response;
        if (body is not null)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(body);
            var limit = endpoint.Metadata.GetMetadata<BodyLimitMetadata>()?.Bytes
                ?? endpoint.Metadata.GetMetadata<RequestBodyLimit>()?.MaxBytes
                ?? RequestBodyLimit.Default;
            if (bytes.Length > limit)
            {
                return Reply(StatusCodes.Status413PayloadTooLarge,
                    new ErrorResponse("payload_too_large", $"The request body holds at most {limit} bytes."));
            }
            http.Request.Body = new MemoryStream(bytes, writable: false);
            http.Request.ContentType = "application/json";
            http.Request.ContentLength = bytes.Length;
            http.Features.Set<IHttpRequestBodyDetectionFeature>(HasBody.Instance);
        }

        // The tool call may run in a scope of its own, so the events say who caused them here.
        services.GetRequiredService<DeedboxContext>().Metadata = new EventMetadata
        {
            CorrelationId = http.TraceIdentifier,
            Actor = AgentPrincipal.Actor(User),
        };

        try
        {
            await endpoint.RequestDelegate!(http);
        }
        catch (BadHttpRequestException bad)
        {
            return Reply(bad.StatusCode, new ErrorResponse("invalid_request", "The arguments are not what the endpoint takes", bad.Message));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The same answers as the exception handler middleware gives over HTTP.
            var handled = false;
            foreach (var handler in services.GetServices<IExceptionHandler>())
            {
                handled = await handler.TryHandleAsync(http, ex, Aborted);
                if (handled)
                    break;
            }
            if (!handled)
                throw;
        }

        await http.Response.Body.FlushAsync(Aborted);
        return new EndpointReply(http.Response.StatusCode, response.ToArray(), http.Response.Headers);
    }

    private EndpointReply Reply(int status, ErrorResponse error) =>
        new(status, JsonSerializer.SerializeToUtf8Bytes(error, json.Value.SerializerOptions), new HeaderDictionary());

    /// <summary>
    /// The result of a tool that worked. Every result has the same members:
    /// <c>projectId</c>; <c>truth</c>, the label of every fact in <c>data</c> that has no
    /// <c>factStatus</c> of its own, and <c>truthNote</c>; <c>data</c>; <c>nextCursor</c>, the
    /// value that gets the next page, or null; and <c>notice</c>, a sentence of the server
    /// when the result was cut, or null. Text of the model is only ever inside <c>data</c>.
    /// </summary>
    public CallToolResult Ok(JsonObject data, string? nextCursor = null, string? notice = null)
    {
        var text = new JsonObject
        {
            ["projectId"] = _project?.Id,
            ["truth"] = _tool.Truth,
            ["truthNote"] = _tool.TruthNote,
            ["data"] = data,
            ["nextCursor"] = nextCursor,
            ["notice"] = notice,
        }.ToJsonString();
        return McpOutput.Text(text);
    }

    /// <summary>The refusal of an endpoint as a tool error: its own error document, and the status it had over HTTP.</summary>
    public CallToolResult Refused(EndpointReply reply)
    {
        JsonObject? error = null;
        try
        {
            error = reply.Body.Length > 0 ? JsonNode.Parse(reply.Body) as JsonObject : null;
        }
        catch (JsonException)
        {
            // An answer that is not JSON has nothing to pass on but its status.
        }

        // A problem document names its type by a URL; the envelope of the app by a word.
        var type = error?["type"] is JsonValue value && value.TryGetValue<string>(out var word) && !word.Contains("://", StringComparison.Ordinal)
            ? word
            : McpOutput.TypeOf(reply.Status);
        return McpOutput.Error(type, error?["title"]?.ToString() ?? "The request was refused", error?["detail"]?.ToString(), reply.Status, error);
    }

    private sealed class HasBody : IHttpRequestBodyDetectionFeature
    {
        public static readonly HasBody Instance = new();

        public bool CanHaveBody => true;
    }
}
