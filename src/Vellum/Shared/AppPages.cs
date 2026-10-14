namespace Vellum.Shared;

/// <summary>
/// The pages of the single-page app: every path that no endpoint and no file has gets
/// <c>index.html</c>, and the app's router shows the page. A path under the API, the MCP
/// endpoint or the agent files is no page: there it gets 404 in the error shape of the API.
/// </summary>
public static class AppPages
{
    private static readonly string[] NotPages = ["/api", "/mcp", "/skills"];

    private sealed class Page;

    /// <summary>Maps the fallback to <c>index.html</c>. Last, after every endpoint.</summary>
    public static void MapAppPages(this WebApplication app) =>
        app.MapFallbackToFile("index.html").WithMetadata(new Page());

    /// <summary>
    /// Answers 404 for a path under a prefix that is not the app's, when routing found the page
    /// fallback or nothing. Before authentication: the answer is the same for every caller and
    /// names nothing of a workspace.
    /// </summary>
    public static IApplicationBuilder UseNoPageOutsideTheApp(this IApplicationBuilder app) => app.Use(async (http, next) =>
    {
        var endpoint = http.GetEndpoint();
        if ((endpoint is null || endpoint.Metadata.GetMetadata<Page>() is not null)
            && NotPages.Any(prefix => http.Request.Path.StartsWithSegments(prefix)))
        {
            await Results.NotFound(new ErrorResponse("not_found", "No endpoint has this path")).ExecuteAsync(http);
            return;
        }

        await next(http);
    });
}
