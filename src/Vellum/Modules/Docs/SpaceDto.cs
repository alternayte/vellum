// src/Vellum/Modules/Docs/SpaceDto.cs
namespace Vellum.Modules.Docs;

public sealed record SpaceDto(
    Guid Id, Guid ProjectId, string Name,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
