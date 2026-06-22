using Microsoft.EntityFrameworkCore;

namespace Vellum.Modules.Views;

public static class ViewsModule
{
    public static IServiceCollection AddViewsModule(this IServiceCollection services, IConfiguration config)
    {
        var connectionString = config.GetConnectionString("DefaultConnection")!;

        services.AddDbContext<ViewsDbContext>(options =>
            options.UseNpgsql(connectionString)
                   .UseSnakeCaseNamingConvention());

        return services;
    }
}
