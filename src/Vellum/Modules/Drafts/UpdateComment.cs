// src/Vellum/Modules/Drafts/UpdateComment.cs
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Vellum.Modules.Workspaces.Authorization;
using Vellum.Shared;
using static Vellum.Shared.ResultExtensions;

namespace Vellum.Modules.Drafts;

public sealed record UpdateCommentRequest(string Body);

public static class UpdateComment
{
    public static async Task<IResult> Handle(
        Guid projectId, Guid draftId, Guid commentId,
        UpdateCommentRequest request,
        ClaimsPrincipal user,
        AppDbContext db,
        WorkspaceAuthorizationService auth,
        TimeProvider time,
        CancellationToken ct)
    {
        var userId = user.UserId();
        await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Editor, ct);

        if (CommentRules.Check(request.Body, entityType: null) is { } invalid)
            return Invalid(invalid);

        // The draft must be one of this project. Without that, a caller could name a draft of
        // another project and learn from the 403 below that the comment exists.
        var draftInProject = await db.Drafts.AsNoTracking()
            .AnyAsync(d => d.Id == draftId && d.ProjectId == projectId, ct);
        var comment = draftInProject ? await db.Comments.FindAsync([commentId], ct) : null;
        if (comment is null || comment.DraftId != draftId)
            return Results.NotFound(new ErrorResponse("not_found", "Comment not found"));

        if (comment.Author != userId)
            return Results.Json(new ErrorResponse("forbidden", "You can only edit your own comments"),
                statusCode: 403);

        comment.Body = request.Body;
        comment.UpdatedAt = time.GetUtcNow();

        await db.SaveChangesAsync(ct);
        return Results.Ok(CreateComment.ToDto(comment));
    }
}
