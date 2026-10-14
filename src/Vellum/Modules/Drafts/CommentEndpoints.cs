using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Vellum.Modules.Workspaces;
using Vellum.Shared;
using static Vellum.Shared.ResultExtensions;

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

public sealed record CommentDto(
    Guid Id, Guid DraftId, Guid? EntityId, string? EntityType,
    string Author, string Body,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public sealed record CreateCommentRequest(Guid Id, string Body, Guid? EntityId = null, string? EntityType = null);

public static class CreateComment
{
    public static async Task<IResult> Handle(
        Guid projectId, Guid draftId,
        CreateCommentRequest request,
        ClaimsPrincipal user,
        AppDbContext db,
        WorkspaceAuthorizationService auth,
        CancellationToken ct)
    {
        var userId = user.UserId();
        await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Editor, ct);

        if (request.Id == Guid.Empty)
            return Invalid(new ValidationError("id", "An ID is required"));
        if (CommentRules.Check(request.Body, request.EntityType) is { } invalid)
            return Invalid(invalid);

        var draft = await db.Drafts.AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == draftId && d.ProjectId == projectId, ct);
        if (draft is null)
            return Results.NotFound(new ErrorResponse("not_found", "Draft not found"));

        // The ID makes a repeated create idempotent inside one draft. It never reads a comment
        // of another draft, which can belong to another project.
        var existing = await db.Comments.FindAsync([request.Id], ct);
        if (existing is not null)
        {
            return existing.DraftId == draftId
                ? Results.Ok(ToDto(existing))
                : Results.Conflict(new ErrorResponse("conflict", "A comment with this ID already exists."));
        }

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

/// <summary>What a comment may hold, on create and on edit.</summary>
internal static class CommentRules
{
    public static ValidationError? Check(string? body, string? entityType) =>
        string.IsNullOrWhiteSpace(body)
            ? new ValidationError("body", "A comment needs text")
            : TextRule.Lines("body", body) ?? TextRule.Line("entityType", entityType, TextRule.MaxIdentifier);

}

public static class ListComments
{
    public static async Task<IResult> Handle(
        Guid projectId, Guid draftId,
        Guid? entityId, string? cursor, int? limit,
        ClaimsPrincipal user,
        AppDbContext db,
        WorkspaceAuthorizationService auth,
        CancellationToken ct)
    {
        var userId = user.UserId();
        await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Viewer, ct);

        var draft = await db.Drafts.AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == draftId && d.ProjectId == projectId, ct);
        if (draft is null)
            return Results.NotFound(new ErrorResponse("not_found", "Draft not found"));

        var pageSize = Math.Clamp(limit ?? 50, 1, 200);

        var query = db.Comments.AsNoTracking()
            .Where(c => c.DraftId == draftId);

        if (entityId.HasValue)
            query = query.Where(c => c.EntityId == entityId.Value);

        query = query.OrderBy(c => c.CreatedAt).ThenBy(c => c.Id);

        var decoded = CursorEncoder.Decode(cursor);
        if (decoded is not null)
        {
            var (sortKey, afterId) = decoded.Value;
            var afterDate = DateTimeOffset.Parse(sortKey);
            query = query.Where(c => c.CreatedAt > afterDate
                || (c.CreatedAt == afterDate && c.Id.CompareTo(afterId) > 0));
        }

        var items = await query
            .Take(pageSize + 1)
            .Select(c => new CommentDto(c.Id, c.DraftId, c.EntityId, c.EntityType, c.Author, c.Body, c.CreatedAt, c.UpdatedAt))
            .ToListAsync(ct);

        string? nextCursor = null;
        if (items.Count > pageSize)
        {
            items.RemoveAt(items.Count - 1);
            var last = items[^1];
            nextCursor = CursorEncoder.Encode(last.CreatedAt.ToString("O"), last.Id);
        }

        return Results.Ok(new Page<CommentDto>(items, nextCursor));
    }
}

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

public static class DeleteComment
{
    public static async Task<IResult> Handle(
        Guid projectId, Guid draftId, Guid commentId,
        ClaimsPrincipal user,
        AppDbContext db,
        WorkspaceAuthorizationService auth,
        CancellationToken ct)
    {
        var userId = user.UserId();
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
