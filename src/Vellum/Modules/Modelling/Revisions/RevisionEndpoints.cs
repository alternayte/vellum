namespace Vellum.Modules.Modelling.Revisions;

public static class RevisionEndpoints
{
    public static WebApplication MapRevisionEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/projects/{projectId}/revisions")
            .RequireAuthorization()
            .WithTags("Revisions");

        group.MapPost("/", CreateRevision.Handle);
        group.MapGet("/", ListRevisions.Handle);
        group.MapGet("/{revisionId}", GetRevision.Handle);

        return app;
    }
}
