using Microsoft.EntityFrameworkCore;
using Vellum.Shared;

namespace Vellum.Modules.Views;

public static class ViewsModule
{
    public static IServiceCollection AddViewsModule(this IServiceCollection services)
    {
        services.AddScoped<Shared.IProjectRetention, ViewsRetention>();

        return services;
    }
}
