// src/Vellum/Modules/Docs/DocsModule.cs
using Microsoft.EntityFrameworkCore;
using Vellum.Shared;

namespace Vellum.Modules.Docs;

public static class DocsModule
{
    public static IServiceCollection AddDocsModule(this IServiceCollection services)
    {
        services.AddScoped<Shared.IProjectRetention, DocsRetention>();

        return services;
    }
}
