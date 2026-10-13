using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Net.Http.Headers;
using ModelContextProtocol.Server;
using Vellum.Modules.Identity.AgentTokens;
using Vellum.Shared;

namespace Vellum.Modules.Mcp;

/// <summary>
/// The agent interface: the MCP endpoint and its tools, the capability endpoint, and the
/// agent reference (<c>/llms.txt</c>, <c>/llms-full.txt</c> and the skill). The server has
/// tools and nothing else: no resource and no prompt.
/// </summary>
public static class McpModule
{
    public static IServiceCollection AddMcpModule(this IServiceCollection services)
    {
        // Stateless: the server keeps no session and opens no stream of its own. Every
        // message is one POST that authenticates by itself, so a token that is revoked or
        // expired stops at the next message, and there is no stream that could outlive it.
        services.AddMcpServer().WithHttpTransport(transport => transport.Stateless = true);

        // The tools are the catalog, and only the catalog.
        foreach (var tool in AgentTools.All)
            services.AddSingleton<McpServerTool>(provider => tool.Create(provider));

        services.AddScoped<McpProjects>();
        services.AddScoped<McpCall>();
        services.AddSingleton<AgentEndpoints>();
        services.AddSingleton<AgentReference>();
        return services;
    }

    public static WebApplication MapMcpEndpoints(this WebApplication app)
    {
        app.MapMcp(AgentReference.McpPath).RequireAgent().WithBodyLimit(RequestBodyLimit.Document);

        app.MapGet(AgentReference.CapabilitiesPath, AgentCapabilities.Handle)
            .RequireAuthorization().AllowAnyAgent()
            .Produces<AgentCapabilities>()
            .WithTags("Agent");

        // The reference holds nothing of an instance and nothing of a workspace, so it needs
        // no credential and a cache may keep it. A request that brings a token gets it too.
        static IResult Reference(HttpContext http, string text, string contentType)
        {
            http.Response.Headers[HeaderNames.CacheControl] = "public, max-age=3600";
            return Results.Text(text, contentType, System.Text.Encoding.UTF8);
        }
        app.MapGet(AgentReference.LlmsPath, (HttpContext http, AgentReference reference) =>
                Reference(http, reference.Llms, "text/plain"))
            .AllowAnonymous().AllowAnyAgent().ExcludeFromDescription();
        app.MapGet(AgentReference.LlmsFullPath, (HttpContext http, AgentReference reference) =>
                Reference(http, reference.LlmsFull, "text/plain"))
            .AllowAnonymous().AllowAnyAgent().ExcludeFromDescription();
        app.MapGet(AgentReference.SkillPath, (HttpContext http, AgentReference reference) =>
                Reference(http, reference.Skill, "text/markdown"))
            .AllowAnonymous().AllowAnyAgent().ExcludeFromDescription();

        return app;
    }
}
