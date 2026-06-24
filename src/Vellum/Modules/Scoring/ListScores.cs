using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Vellum.Modules.Workspaces.Authorization;

namespace Vellum.Modules.Scoring;

public static class ListScores
{
    public static async Task<IResult> Handle(
        Guid projectId, Guid docId,
        ClaimsPrincipal user,
        ScoringDbContext db,
        WorkspaceAuthorizationService auth,
        CancellationToken ct)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier)!;
        await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Viewer, ct);

        var scores = await db.Scores.AsNoTracking()
            .Where(s => s.DocId == docId && s.ProjectId == projectId)
            .OrderByDescending(s => s.CreatedAt)
            .Select(s => new ScoreListDto(s.Id, s.DocId, s.DocType, s.OverallScore, s.ScoredBy, s.CreatedAt))
            .ToListAsync(ct);

        return Results.Ok(scores);
    }
}
