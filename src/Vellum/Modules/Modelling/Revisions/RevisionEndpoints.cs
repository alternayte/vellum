using Vellum.Modules.Identity.AgentTokens;

namespace Vellum.Modules.Modelling.Revisions;

public static class RevisionEndpoints
{
    public static WebApplication MapRevisionEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/projects/{projectId}/revisions")
            .RequireAuthorization()
            .WithTags("Revisions");

        group.MapPost("/", CreateRevision.Handle);
        group.MapGet("/", ListRevisions.Handle).AllowAgent(AgentScope.Read);
        group.MapGet("/{revisionId}", GetRevision.Handle).AllowAgent(AgentScope.Read);
        group.MapPatch("/{revisionId}", RenameRevision.Handle).Produces<RevisionDto>();
        group.MapDelete("/{revisionId}", DeleteRevision.Handle).Produces(StatusCodes.Status204NoContent);
        group.MapGet("/{revisionId}/model", PinnedModelRead.RevisionModel).Produces<RevisionModelDto>();

        app.MapGet("/api/projects/{projectId}/model/compare", PinnedModelRead.Compare)
            .RequireAuthorization()
            .WithTags("Revisions")
            .Produces<ModelComparisonDto>();

        return app;
    }
}
