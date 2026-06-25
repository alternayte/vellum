using Microsoft.EntityFrameworkCore;
using Vellum.Kernel.EventTypes;
using Vellum.Kernel.Projections;
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

        services.AddScoped<ModelProjection>();
        services.AddScoped<IInlineProjection>(sp => sp.GetRequiredService<ModelProjection>());

        return services;
    }

    public static void RegisterEvents(EventTypeRegistry registry)
    {
        registry.Register<ModelEvent.ElementAdded>("modelling.element.added.v1");
        registry.Register<ModelEvent.ElementRenamed>("modelling.element.renamed.v1");
        registry.Register<ModelEvent.ElementDescriptionChanged>("modelling.element.description_changed.v1");
        registry.Register<ModelEvent.ElementTechnologyChanged>("modelling.element.technology_changed.v1");
        registry.Register<ModelEvent.ElementOwnerChanged>("modelling.element.owner_changed.v1");
        registry.Register<ModelEvent.ElementReparented>("modelling.element.reparented.v1");
        registry.Register<ModelEvent.ElementStatusChanged>("modelling.element.status_changed.v1");
        registry.Register<ModelEvent.ElementRetagged>("modelling.element.retagged.v1");
        registry.Register<ModelEvent.ElementIconChanged>("modelling.element.icon_changed.v1");
        registry.Register<ModelEvent.ElementRemoved>("modelling.element.removed.v1");

        registry.Register<ModelEvent.RelationshipAdded>("modelling.relationship.added.v1");
        registry.Register<ModelEvent.RelationshipLabelChanged>("modelling.relationship.label_changed.v1");
        registry.Register<ModelEvent.RelationshipTechnologyChanged>("modelling.relationship.technology_changed.v1");
        registry.Register<ModelEvent.RelationshipLineShapeChanged>("modelling.relationship.line_shape_changed.v1");
        registry.Register<ModelEvent.RelationshipRemoved>("modelling.relationship.removed.v1");

        registry.Register<ModelEvent.MessageAdded>("modelling.message.added.v1");
        registry.Register<ModelEvent.MessageUpdated>("modelling.message.updated.v1");
        registry.Register<ModelEvent.MessageRemoved>("modelling.message.removed.v1");
    }
}
