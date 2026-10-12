namespace Vellum.Modules.Modelling.Variants;

public static class VariantEndpoints
{
    public static WebApplication MapVariantEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/projects/{projectId}/variants")
            .RequireAuthorization()
            .WithTags("Variants");

        group.MapPost("/", CreateVariant.Handle);
        group.MapGet("/", ListVariants.Handle);
        group.MapGet("/{variantId}", GetVariant.Handle);

        return app;
    }
}
