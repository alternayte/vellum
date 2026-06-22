// src/Vellum/Modules/Drafts/DraftsModule.cs
using Microsoft.EntityFrameworkCore;

namespace Vellum.Modules.Drafts;

public static class DraftsModule
{
    public static IServiceCollection AddDraftsModule(this IServiceCollection services, IConfiguration config)
    {
        var connectionString = config.GetConnectionString("DefaultConnection")!;
        services.AddDbContext<DraftsDbContext>(options =>
            options.UseNpgsql(connectionString)
                   .UseSnakeCaseNamingConvention());
        return services;
    }
}
