// src/Vellum/Modules/Drafts/DraftsModule.cs
using Microsoft.EntityFrameworkCore;
using Vellum.Shared;

namespace Vellum.Modules.Drafts;

public static class DraftsModule
{
    public static IServiceCollection AddDraftsModule(this IServiceCollection services)
    {
        services.AddScoped<Shared.IProjectRetention, DraftsRetention>();
        return services;
    }
}
