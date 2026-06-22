// src/Vellum/Modules/Drafts/CreateComment.cs
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Vellum.Modules.Drafts.Entities;
using Vellum.Modules.Workspaces.Authorization;
using Vellum.Shared;

namespace Vellum.Modules.Drafts;

public sealed record CreateCommentRequest(Guid Id, string Body, Guid? EntityId = null, string? EntityType = null);

public static class CreateComment
{
    public static async Task<IResult> Handle(
        Guid projectId, Guid draftId,
        CreateCommentRequest request,
        ClaimsPrincipal user,
        DraftsDbContext db,
        WorkspaceAuthorizationService auth,
        CancellationToken ct)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier)!;
        await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Editor, ct);

        var draft = await db.Drafts.AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == draftId && d.ProjectId == projectId, ct);
        if (draft is null)
            return Results.NotFound(new ErrorResponse("not_found", "Draft not found"));

        var existing = await db.Comments.FindAsync([request.Id], ct);
        if (existing is not null)
            return Results.Ok(ToDto(existing));

        var comment = new CommentEntity
        {
            Id = request.Id,
            DraftId = draftId,
            EntityId = request.EntityId,
            EntityType = request.EntityType,
            Author = userId,
            Body = request.Body,
        };
        db.Comments.Add(comment);
        await db.SaveChangesAsync(ct);

        return Results.Created(
            $"/api/projects/{projectId}/drafts/{draftId}/comments/{comment.Id}",
            ToDto(comment));
    }

    internal static CommentDto ToDto(CommentEntity c) =>
        new(c.Id, c.DraftId, c.EntityId, c.EntityType, c.Author, c.Body, c.CreatedAt, c.UpdatedAt);
}
