using Microsoft.EntityFrameworkCore;
using Vellum.Kernel.EventTypes;
using Vellum.Kernel.Projections;

namespace Vellum.Modules.Schemas;

public static class SchemasModule
{
    public static IServiceCollection AddSchemasModule(this IServiceCollection services, IConfiguration config)
    {
        var connectionString = config.GetConnectionString("DefaultConnection")!;

        services.AddDbContext<SchemasDbContext>(options =>
            options.UseNpgsql(connectionString)
                   .UseSnakeCaseNamingConvention());

        services.AddScoped<SchemaProjection>();
        services.AddScoped<IInlineProjection>(sp => sp.GetRequiredService<SchemaProjection>());

        return services;
    }

    public static void RegisterEvents(EventTypeRegistry registry)
    {
        registry.Register<SchemaEvent.SchemaCreated>("schemas.created.v1");
        registry.Register<SchemaEvent.SchemaUpdated>("schemas.updated.v1");
        registry.Register<SchemaEvent.SchemaDeleted>("schemas.deleted.v1");
    }
}
