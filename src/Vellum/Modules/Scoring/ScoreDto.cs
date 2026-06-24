namespace Vellum.Modules.Scoring;

public sealed record ScoreDto(
    Guid Id, Guid DocId, string DocType,
    decimal OverallScore, IReadOnlyList<CriterionResult> CriteriaResults,
    string? SuggestedContent, string ScoredBy, DateTimeOffset CreatedAt);

public sealed record ScoreListDto(
    Guid Id, Guid DocId, string DocType,
    decimal OverallScore, string ScoredBy, DateTimeOffset CreatedAt);
