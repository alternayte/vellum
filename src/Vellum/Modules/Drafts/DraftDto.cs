// src/Vellum/Modules/Drafts/DraftDto.cs
namespace Vellum.Modules.Drafts;

public sealed record DraftDto(
    Guid Id, Guid ProjectId, string Name, Guid StreamId,
    Guid BaseStreamId, int ForkVersion, string Status,
    string CreatedBy, DateTimeOffset CreatedAt,
    DateTimeOffset? MergedAt, DateTimeOffset? AbandonedAt);
