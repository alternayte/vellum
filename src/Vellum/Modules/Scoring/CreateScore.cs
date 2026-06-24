using System.Security.Claims;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Vellum.Modules.Docs;
using Vellum.Modules.Scoring.Entities;
using Vellum.Modules.Workspaces.Authorization;
using Vellum.Shared;

namespace Vellum.Modules.Scoring;

public static class CreateScore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static async Task<IResult> Handle(
        Guid projectId, Guid docId,
        ClaimsPrincipal user,
        DocsDbContext docsDb,
        ScoringDbContext scoringDb,
        IServiceProvider services,
        RubricService rubrics,
        WorkspaceAuthorizationService auth,
        CancellationToken ct)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier)!;
        await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Editor, ct);

        var chatClient = services.GetService<IChatClient>();
        if (chatClient is null)
            return Results.Problem("AI provider not configured", statusCode: 503);

        var doc = await docsDb.Documents.FindAsync([docId], ct);
        if (doc is null || doc.ProjectId != projectId)
            return Results.NotFound(new ErrorResponse("not_found", "Document not found"));

        if (doc.Type is null)
            return Results.UnprocessableEntity(new ErrorResponse("no_type", "Document has no type assigned"));

        var rubric = rubrics.GetRubric(doc.Type);
        if (rubric is null)
            return Results.UnprocessableEntity(new ErrorResponse("no_rubric", $"No rubric available for doc type '{doc.Type}'"));

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, rubric.Prompt),
            new(ChatRole.User, doc.Content),
        };

        var response = await chatClient.GetResponseAsync(messages, cancellationToken: ct);
        var responseText = response.Text;

        var jsonStart = responseText.IndexOf('{');
        var jsonEnd = responseText.LastIndexOf('}');
        if (jsonStart < 0 || jsonEnd < 0)
            return Results.Problem("Failed to parse LLM response as JSON", statusCode: 502);

        var jsonStr = responseText[jsonStart..(jsonEnd + 1)];
        var parsed = JsonSerializer.Deserialize<LlmScoreResponse>(jsonStr, JsonOptions);
        if (parsed is null)
            return Results.Problem("Failed to parse LLM response", statusCode: 502);

        var criteriaResults = parsed.Criteria.Select(c =>
        {
            var rubricCriterion = rubric.Criteria.FirstOrDefault(rc => rc.Key == c.Key);
            return new CriterionResult(c.Key, rubricCriterion?.Name ?? c.Key, c.Score, 5, c.Explanation, c.SuggestedEdit);
        }).ToList();

        var totalWeight = rubric.Criteria.Sum(c => c.Weight);
        var weightedSum = parsed.Criteria.Sum(c =>
        {
            var weight = rubric.Criteria.FirstOrDefault(rc => rc.Key == c.Key)?.Weight ?? 1;
            return c.Score * weight;
        });
        var overallScore = totalWeight > 0 ? (decimal)weightedSum / totalWeight : 0m;

        var score = new ScoreEntity
        {
            Id = Guid.NewGuid(),
            DocId = docId,
            ProjectId = projectId,
            DocType = doc.Type,
            OverallScore = Math.Round(overallScore, 1),
            CriteriaResultsJson = JsonSerializer.Serialize(criteriaResults, JsonOptions),
            SuggestedContent = parsed.SuggestedContent,
            ScoredBy = userId,
        };

        scoringDb.Scores.Add(score);
        await scoringDb.SaveChangesAsync(ct);

        return Results.Created(
            $"/api/projects/{projectId}/docs/{docId}/scores/{score.Id}",
            new ScoreDto(score.Id, score.DocId, score.DocType, score.OverallScore,
                criteriaResults, score.SuggestedContent, score.ScoredBy, score.CreatedAt));
    }

    private sealed record LlmCriterionResponse(string Key, int Score, string Explanation, string? SuggestedEdit);
    private sealed record LlmScoreResponse(List<LlmCriterionResponse> Criteria, string? SuggestedContent);
}
