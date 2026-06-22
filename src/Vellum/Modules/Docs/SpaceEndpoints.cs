// src/Vellum/Modules/Docs/SpaceEndpoints.cs
namespace Vellum.Modules.Docs;

public static class SpaceEndpoints
{
    public static WebApplication MapSpaceEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/projects/{projectId}/spaces")
            .RequireAuthorization()
            .WithTags("Spaces");

        group.MapPost("/", CreateSpace.Handle);
        group.MapGet("/", ListSpaces.Handle);
        group.MapPatch("/{spaceId}", UpdateSpace.Handle);
        group.MapDelete("/{spaceId}", DeleteSpace.Handle);

        return app;
    }
}
