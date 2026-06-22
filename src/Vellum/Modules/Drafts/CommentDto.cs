// src/Vellum/Modules/Drafts/CommentDto.cs
namespace Vellum.Modules.Drafts;

public sealed record CommentDto(
    Guid Id, Guid DraftId, Guid? EntityId, string? EntityType,
    string Author, string Body,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
