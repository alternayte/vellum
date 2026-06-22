// src/Vellum/Modules/Drafts/DraftEndpoints.cs
namespace Vellum.Modules.Drafts;

public static class DraftEndpoints
{
    public static WebApplication MapDraftEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/projects/{projectId}/drafts")
            .RequireAuthorization()
            .WithTags("Drafts");

        group.MapPost("/", CreateDraft.Handle);
        group.MapGet("/", ListDrafts.Handle);
        group.MapGet("/{draftId}", GetDraft.Handle);
        group.MapPost("/{draftId}/abandon", AbandonDraft.Handle);

        return app;
    }
}
