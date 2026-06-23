using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace Vellum.Modules.Mcp;

public static class McpModule
{
    public static IServiceCollection AddMcpModule(this IServiceCollection services)
    {
        services.AddMcpServer()
            .WithHttpTransport()
            .WithToolsFromAssembly(typeof(McpModule).Assembly);
        return services;
    }

    public static WebApplication MapMcpEndpoints(this WebApplication app)
    {
        app.MapMcp("/mcp");
        return app;
    }
}
