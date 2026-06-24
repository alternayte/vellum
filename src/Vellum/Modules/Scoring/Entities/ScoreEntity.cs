namespace Vellum.Modules.Scoring.Entities;

public class ScoreEntity
{
    public Guid Id { get; set; }
    public Guid DocId { get; set; }
    public Guid ProjectId { get; set; }
    public string DocType { get; set; } = null!;
    public decimal OverallScore { get; set; }
    public string CriteriaResultsJson { get; set; } = "[]";
    public string? SuggestedContent { get; set; }
    public string ScoredBy { get; set; } = null!;
    public DateTimeOffset CreatedAt { get; set; }
}
