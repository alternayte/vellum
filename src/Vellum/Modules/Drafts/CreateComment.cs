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
        AppDbContext db,
        WorkspaceAuthorizationService auth,
        CancellationToken ct)
    {
        var userId = user.UserId();
        await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Editor, ct);

        if (request.Id == Guid.Empty)
            return CommentRules.Invalid(new ValidationError("id", "An ID is required"));
        if (CommentRules.Check(request.Body, request.EntityType) is { } invalid)
            return CommentRules.Invalid(invalid);

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

    public static IResult Invalid(ValidationError error) =>
        Results.BadRequest(new ErrorResponse("validation_error", "Validation failed",
            Errors: [new FieldError(error.Field, error.Message)]));
}
