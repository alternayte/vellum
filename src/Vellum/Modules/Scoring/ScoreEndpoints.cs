namespace Vellum.Modules.Scoring;

public static class ScoreEndpoints
{
    public static WebApplication MapScoreEndpoints(this WebApplication app)
    {
        app.MapGet("/api/scoring/status", ScoringStatus.Handle)
            .WithTags("Scoring");

        var group = app.MapGroup("/api/projects/{projectId}/docs/{docId}/scores")
            .RequireAuthorization()
            .WithTags("Scoring");

        group.MapPost("/", CreateScore.Handle);
        group.MapGet("/", ListScores.Handle);
        group.MapGet("/{scoreId}", GetScore.Handle);

        return app;
    }
}
