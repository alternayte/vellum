// src/Vellum/Modules/Drafts/CommentEndpoints.cs
namespace Vellum.Modules.Drafts;

public static class CommentEndpoints
{
    public static WebApplication MapCommentEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/projects/{projectId}/drafts/{draftId}/comments")
            .RequireAuthorization()
            .WithTags("Comments");

        group.MapPost("/", CreateComment.Handle);
        group.MapGet("/", ListComments.Handle);
        group.MapPatch("/{commentId}", UpdateComment.Handle);
        group.MapDelete("/{commentId}", DeleteComment.Handle);

        return app;
    }
}
