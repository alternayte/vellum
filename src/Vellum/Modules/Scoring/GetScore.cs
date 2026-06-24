using System.Security.Claims;
using System.Text.Json;
using Vellum.Modules.Workspaces.Authorization;
using Vellum.Shared;

namespace Vellum.Modules.Scoring;

public static class GetScore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static async Task<IResult> Handle(
        Guid projectId, Guid docId, Guid scoreId,
        ClaimsPrincipal user,
        ScoringDbContext db,
        WorkspaceAuthorizationService auth,
        CancellationToken ct)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier)!;
        await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Viewer, ct);

        var score = await db.Scores.FindAsync([scoreId], ct);
        if (score is null || score.DocId != docId || score.ProjectId != projectId)
            return Results.NotFound(new ErrorResponse("not_found", "Score not found"));

        var criteria = JsonSerializer.Deserialize<List<CriterionResult>>(score.CriteriaResultsJson, JsonOptions) ?? [];

        return Results.Ok(new ScoreDto(score.Id, score.DocId, score.DocType, score.OverallScore,
            criteria, score.SuggestedContent, score.ScoredBy, score.CreatedAt));
    }
}
