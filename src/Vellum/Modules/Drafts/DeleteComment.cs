// src/Vellum/Modules/Drafts/DeleteComment.cs
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Vellum.Modules.Workspaces.Authorization;
using Vellum.Shared;

namespace Vellum.Modules.Drafts;

public static class DeleteComment
{
    public static async Task<IResult> Handle(
        Guid projectId, Guid draftId, Guid commentId,
        ClaimsPrincipal user,
        AppDbContext db,
        WorkspaceAuthorizationService auth,
        CancellationToken ct)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier)!;
        await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Editor, ct);

        // The draft must be one of this project. Without that, a caller could name a draft of
        // another project and learn from the 403 below that the comment exists.
        var draftInProject = await db.Drafts.AsNoTracking()
            .AnyAsync(d => d.Id == draftId && d.ProjectId == projectId, ct);
        var comment = draftInProject ? await db.Comments.FindAsync([commentId], ct) : null;
        if (comment is null || comment.DraftId != draftId)
            return Results.NotFound(new ErrorResponse("not_found", "Comment not found"));

        if (comment.Author != userId)
            return Results.Json(new ErrorResponse("forbidden", "You can only delete your own comments"),
                statusCode: 403);

        db.Comments.Remove(comment);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }
}
