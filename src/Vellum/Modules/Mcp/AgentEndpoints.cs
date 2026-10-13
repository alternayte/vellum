using System.Collections.Concurrent;
using Microsoft.AspNetCore.Routing;
using Vellum.Modules.Identity.AgentTokens;

namespace Vellum.Modules.Mcp;

/// <summary>One route of the HTTP API, as its endpoint is mapped: <c>POST /api/projects/{projectId}/promises</c>.</summary>
public sealed record HttpRoute(string Method, string Pattern)
{
    public override string ToString() => $"{Method} {Pattern}";
}

/// <summary>An endpoint that is open to agent tokens, with the scopes of which a token needs one. No scope: the endpoint checks each call itself.</summary>
public sealed record AgentEndpoint(string Method, string Pattern, IReadOnlyList<AgentScope> Scopes);

/// <summary>
/// The mapped endpoints, read from the endpoint data source and from nowhere else: which of
/// them are open to agent tokens, and the endpoint a tool calls. The capability endpoint, the
/// agent reference and the tools all ask here, so none of them can name an endpoint that is
/// not mapped or state a scope that the endpoint does not have.
/// </summary>
public sealed class AgentEndpoints(EndpointDataSource source)
{
    private readonly ConcurrentDictionary<HttpRoute, RouteEndpoint?> _found = new();

    /// <summary>Every endpoint an agent token may call, by pattern and then by method.</summary>
    public IReadOnlyList<AgentEndpoint> Open() =>
    [
        .. source.Endpoints.OfType<RouteEndpoint>()
            .Where(e => e.Metadata.GetMetadata<AgentAllowed>() is not null && e.Metadata.GetMetadata<HumanOnly>() is null)
            .SelectMany(e => Methods(e).Select(method =>
                new AgentEndpoint(method, Plain(e.RoutePattern.RawText), e.Metadata.GetMetadata<AgentAllowed>()!.Scopes)))
            .Distinct(AgentEndpointComparer.Instance)
            .OrderBy(e => e.Pattern, StringComparer.Ordinal).ThenBy(e => e.Method, StringComparer.Ordinal),
    ];

    /// <summary>
    /// The endpoint of <paramref name="route"/>, when an agent token with
    /// <paramref name="scope"/> may call it over HTTP. Null for a route that is not mapped,
    /// that is for a person only, or that does not take the scope: a tool reaches nothing
    /// that the HTTP boundary would refuse.
    /// </summary>
    public RouteEndpoint? For(HttpRoute route, AgentScope scope)
    {
        var endpoint = _found.GetOrAdd(route, wanted => source.Endpoints.OfType<RouteEndpoint>().FirstOrDefault(e =>
            Plain(e.RoutePattern.RawText) == Plain(wanted.Pattern) && Methods(e).Contains(wanted.Method, StringComparer.Ordinal)));
        return endpoint is not null
            && endpoint.Metadata.GetMetadata<HumanOnly>() is null
            && endpoint.Metadata.GetMetadata<AgentAllowed>() is { } allowed
            && allowed.Scopes.Contains(scope)
                ? endpoint
                : null;
    }

    private static IReadOnlyList<string> Methods(RouteEndpoint endpoint) =>
        endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["*"];

    /// <summary>A group maps its root as <c>/…/promises/</c>. The reference and the tools name it without the last slash.</summary>
    private static string Plain(string? pattern) => pattern is { Length: > 1 } ? pattern.TrimEnd('/') : pattern ?? "";

    private sealed class AgentEndpointComparer : IEqualityComparer<AgentEndpoint>
    {
        public static readonly AgentEndpointComparer Instance = new();

        public bool Equals(AgentEndpoint? x, AgentEndpoint? y) => x?.Method == y?.Method && x?.Pattern == y?.Pattern;

        public int GetHashCode(AgentEndpoint endpoint) => HashCode.Combine(endpoint.Method, endpoint.Pattern);
    }
}
