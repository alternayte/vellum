// src/Vellum/Modules/Docs/DocDto.cs
namespace Vellum.Modules.Docs;

public sealed record DocDto(
    Guid Id, Guid ProjectId, Guid? SpaceId, Guid? ElementId,
    string Title, string Content, string CreatedBy,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
