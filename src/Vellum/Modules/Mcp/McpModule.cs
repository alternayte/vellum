using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Vellum.Modules.Identity.AgentTokens;
using Vellum.Shared;

namespace Vellum.Modules.Mcp;

public static class McpModule
{
    public static IServiceCollection AddMcpModule(this IServiceCollection services)
    {
        services.AddMcpServer()
            .WithHttpTransport()
            .WithToolsFromAssembly(typeof(McpModule).Assembly);
        services.AddScoped<McpProjects>();
        return services;
    }

    public static WebApplication MapMcpEndpoints(this WebApplication app)
    {
        app.MapMcp("/mcp").RequireAgent().WithBodyLimit(RequestBodyLimit.Document);
        return app;
    }
}
