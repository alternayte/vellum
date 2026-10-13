// src/Vellum/Modules/Drafts/MergeEndpoints.cs
using Vellum.Shared;

namespace Vellum.Modules.Drafts;

public static class MergeEndpoints
{
    public static WebApplication MapMergeEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/projects/{projectId}/drafts/{draftId}/merge")
            .RequireAuthorization()
            .WithTags("Merge");

        group.MapPost("/preview", PreviewMerge.Handle);
        // A merge carries one resolution for each conflict.
        group.MapPost("/execute", ExecuteMerge.Handle).WithBodyLimit(RequestBodyLimit.Document);

        return app;
    }
}
