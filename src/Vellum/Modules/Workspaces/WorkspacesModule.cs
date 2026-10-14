using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Vellum.Modules.Workspaces.Authorization;

namespace Vellum.Modules.Workspaces;

public static class WorkspacesModule
{
    public static IServiceCollection AddWorkspacesModule(this IServiceCollection services, IConfiguration config)
    {
        var connectionString = config.GetConnectionString("DefaultConnection")!;

        services.AddDbContext<WorkspacesDbContext>(options =>
            options.UseNpgsql(connectionString)
                   .UseSnakeCaseNamingConvention());

        services.AddScoped<WorkspaceAuthorizationService>();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(InvitationLimits.Default);
        services.TryAddSingleton<InvitationRate>();
        services.TryAddSingleton<InvitationResponseRate>();

        return services;
    }
}
