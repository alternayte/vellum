using Deedbox;
using Microsoft.EntityFrameworkCore;

namespace Vellum.Modules.Schemas;

public static class SchemasModule
{
    public static IServiceCollection AddSchemasModule(this IServiceCollection services, IConfiguration config)
    {
        var connectionString = config.GetConnectionString("DefaultConnection")!;

        services.AddDbContext<SchemasDbContext>(options =>
            options.UseNpgsql(connectionString)
                   .UseSnakeCaseNamingConvention());
        services.AddScoped<Shared.IProjectRetention, SchemasRetention>();

        return services;
    }

    /// <summary>One stream per schema, keyed by the schema ID.</summary>
    public static DeedboxBuilder AddSchemaStreams(this DeedboxBuilder deedbox) => deedbox
        .Stream<SchemaState>("schema", stream => stream.EventsNestedIn(typeof(SchemaEvent)))
        .Projection<SchemaProjection>("schema_read_model", Run.Inline);
}
