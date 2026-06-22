// src/Vellum/Modules/Docs/DocsModule.cs
using Microsoft.EntityFrameworkCore;

namespace Vellum.Modules.Docs;

public static class DocsModule
{
    public static IServiceCollection AddDocsModule(this IServiceCollection services, IConfiguration config)
    {
        var connectionString = config.GetConnectionString("DefaultConnection")!;

        services.AddDbContext<DocsDbContext>(options =>
            options.UseNpgsql(connectionString)
                   .UseSnakeCaseNamingConvention());

        return services;
    }
}
