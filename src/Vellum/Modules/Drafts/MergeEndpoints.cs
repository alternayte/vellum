// src/Vellum/Modules/Drafts/MergeEndpoints.cs
namespace Vellum.Modules.Drafts;

public static class MergeEndpoints
{
    public static WebApplication MapMergeEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/projects/{projectId}/drafts/{draftId}/merge")
            .RequireAuthorization()
            .WithTags("Merge");

        group.MapPost("/preview", PreviewMerge.Handle);
        group.MapPost("/execute", ExecuteMerge.Handle);

        return app;
    }
}
