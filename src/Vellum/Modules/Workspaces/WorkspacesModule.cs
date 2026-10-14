using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Vellum.Modules.Workspaces.Authorization;
using Vellum.Shared;

namespace Vellum.Modules.Workspaces;

public static class WorkspacesModule
{
    public static IServiceCollection AddWorkspacesModule(this IServiceCollection services)
    {
        services.AddScoped<WorkspaceAuthorizationService>();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(InvitationLimits.Default);
        services.TryAddSingleton<InvitationRate>();
        services.TryAddSingleton<InvitationResponseRate>();

        return services;
    }
}
