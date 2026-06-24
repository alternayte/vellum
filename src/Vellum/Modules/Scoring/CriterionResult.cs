namespace Vellum.Modules.Scoring;

public sealed record CriterionResult(
    string Key, string Name, int Score, int MaxScore,
    string Explanation, string? SuggestedEdit);
