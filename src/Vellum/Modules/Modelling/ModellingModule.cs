using Deedbox;
using Microsoft.EntityFrameworkCore;
using Vellum.Modules.Modelling.Model;

namespace Vellum.Modules.Modelling;

public static class ModellingModule
{
    public static IServiceCollection AddModellingModule(this IServiceCollection services, IConfiguration config)
    {
        var connectionString = config.GetConnectionString("DefaultConnection")!;

        services.AddDbContext<ModellingDbContext>(options =>
            options.UseNpgsql(connectionString)
                   .UseSnakeCaseNamingConvention());

        return services;
    }

    /// <summary>
    /// One stream per model branch: a project's main model, a draft or a variant. The stream
    /// ID is the branch ID.
    /// </summary>
    public static DeedboxBuilder AddModelStreams(this DeedboxBuilder deedbox) => deedbox
        .Stream<ModelState>("model", stream => stream
            .EventsNestedIn(typeof(ModelEvent))
            // Version 2 of the state added operations, channels and subscriptions.
            // Version 3 added the stale mark of a fact.
            // Version 4 added the bindings between channels.
            .StateVersion(4))
        .Projection<ModelProjection>("model_read_model", Run.Inline);
}
