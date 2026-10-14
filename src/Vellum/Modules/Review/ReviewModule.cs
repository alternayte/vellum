using Deedbox;
using Microsoft.EntityFrameworkCore;
using Vellum.Modules.Ai.Design;
using Vellum.Modules.Identity.AgentTokens;
using Vellum.Modules.Review.Critique;
using Vellum.Modules.Review.Decisions;
using Vellum.Modules.Review.Rules;
using Vellum.Shared;

namespace Vellum.Modules.Review;

/// <summary>
/// Review mode (SDD §3 and §8): deterministic rules, their findings, decisions (ADRs), open
/// questions and the Rubber Duck critique. The rules, the findings, the decisions and the
/// questions call no AI. A blocking finding prevents Build Ready and never a save.
/// </summary>
public static class ReviewModule
{
    public static IServiceCollection AddReviewModule(this IServiceCollection services)
    {
        // The connection string is read when the context is first used, so configuration that
        // a test host overrides applies.
        services.AddScoped<IProjectRetention, ReviewRetention>();
        return services;
    }

    /// <summary>One stream per decision, keyed by the decision ID.</summary>
    public static DeedboxBuilder AddDecisionStreams(this DeedboxBuilder deedbox) => deedbox
        .Stream<DecisionState>("decision", stream => stream.EventsNestedIn(typeof(DecisionEvent)))
        .Projection<DecisionProjection>("decision_read_model", Run.Inline);

    public static WebApplication MapReviewEndpoints(this WebApplication app)
    {
        // Every body here is a command of bounded text; none carries a document.
        var review = app.MapGroup("/api/projects/{projectId}/review")
            .RequireAuthorization().WithBodyLimit(RequestBodyLimit.Default).WithTags("Review");

        review.MapGet("/rules", ReviewEndpoints.Rules).AllowAgent(AgentScope.Read).Produces<IReadOnlyList<RuleDto>>();
        review.MapGet("/findings", ReviewEndpoints.Findings).AllowAgent(AgentScope.Read)
            .Produces<FindingsResponse>()
            .Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
            .Produces<ErrorResponse>(StatusCodes.Status413PayloadTooLarge);

        review.MapGet("/snapshots", ReviewEndpoints.ListSnapshots).Produces<Page<SnapshotSummary>>();
        review.MapPost("/snapshots", ReviewEndpoints.SaveSnapshot).RequireHuman()
            .Accepts<SaveSnapshotRequest>("application/json")
            .Produces<SnapshotDto>(StatusCodes.Status201Created)
            .Produces<ErrorResponse>(StatusCodes.Status409Conflict);
        review.MapGet("/snapshots/{snapshotId:guid}", ReviewEndpoints.GetSnapshot)
            .Produces<SnapshotDto>().Produces<ErrorResponse>(StatusCodes.Status404NotFound);
        review.MapDelete("/snapshots/{snapshotId:guid}", ReviewEndpoints.DeleteSnapshot).RequireHuman()
            .Produces(StatusCodes.Status204NoContent).Produces<ErrorResponse>(StatusCodes.Status404NotFound);

        review.MapPut("/dispositions", ReviewEndpoints.SetDisposition).RequireHuman()
            .Accepts<SetDispositionRequest>("application/json")
            .Produces<FindingView>()
            .Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
            .Produces<ErrorResponse>(StatusCodes.Status403Forbidden)
            .Produces<ErrorResponse>(StatusCodes.Status404NotFound)
            .Produces<ErrorResponse>(StatusCodes.Status409Conflict);

        var critiques = review.MapGroup("/critiques");
        critiques.MapPost("/", CritiqueEndpoints.Create).RequireHuman()
            .WithMetadata(new AiGenerationLimit())
            .Produces<CritiqueDto>(StatusCodes.Status201Created)
            .Produces<AiUnavailable>()
            .Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
            .Produces<ErrorResponse>(StatusCodes.Status429TooManyRequests);
        critiques.MapGet("/", CritiqueEndpoints.List).Produces<Page<CritiqueSummary>>();
        critiques.MapGet("/{critiqueId:guid}", CritiqueEndpoints.Get)
            .Produces<CritiqueDto>().Produces<ErrorResponse>(StatusCodes.Status404NotFound);
        critiques.MapDelete("/{critiqueId:guid}", CritiqueEndpoints.Delete).RequireHuman()
            .Produces(StatusCodes.Status204NoContent).Produces<ErrorResponse>(StatusCodes.Status404NotFound);
        critiques.MapPost("/{critiqueId:guid}/proposed-decisions/{index:int}/keep", CritiqueEndpoints.KeepDecision).RequireHuman()
            .Produces<DecisionDto>(StatusCodes.Status201Created)
            .Produces<ErrorResponse>(StatusCodes.Status404NotFound)
            .Produces<ErrorResponse>(StatusCodes.Status409Conflict);
        critiques.MapPost("/{critiqueId:guid}/questions/{index:int}/keep", CritiqueEndpoints.KeepQuestion).RequireHuman()
            .Produces<QuestionDto>(StatusCodes.Status201Created)
            .Produces<ErrorResponse>(StatusCodes.Status404NotFound)
            .Produces<ErrorResponse>(StatusCodes.Status409Conflict);

        var decisions = app.MapGroup("/api/projects/{projectId}/decisions")
            .RequireAuthorization().WithBodyLimit(RequestBodyLimit.Default).WithTags("Decisions");
        decisions.MapGet("/", DecisionEndpoints.List).AllowAgent(AgentScope.Read).Produces<Page<DecisionDto>>();
        // An agent proposes. It does not change, accept, reject or delete a decision.
        decisions.MapPost("/", DecisionEndpoints.Create).AllowAgent(AgentScope.Propose)
            .Accepts<DecisionRequest>("application/json")
            .Produces<DecisionDto>(StatusCodes.Status201Created)
            .Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
            .Produces<ErrorResponse>(StatusCodes.Status409Conflict);
        decisions.MapGet("/export", DecisionEndpoints.ExportAll).AllowAgent(AgentScope.Read).Produces<string>(contentType: "text/markdown");
        decisions.MapGet("/{decisionId:guid}", DecisionEndpoints.Get).AllowAgent(AgentScope.Read)
            .Produces<DecisionDto>().Produces<ErrorResponse>(StatusCodes.Status404NotFound);
        decisions.MapGet("/{decisionId:guid}/export", DecisionEndpoints.Export).AllowAgent(AgentScope.Read)
            .Produces<string>(contentType: "text/markdown").Produces<ErrorResponse>(StatusCodes.Status404NotFound);
        decisions.MapPut("/{decisionId:guid}", DecisionEndpoints.Update)
            .Accepts<DecisionRequest>("application/json")
            .Produces<DecisionDto>()
            .Produces<ErrorResponse>(StatusCodes.Status409Conflict);
        decisions.MapDelete("/{decisionId:guid}", DecisionEndpoints.Delete)
            .Produces(StatusCodes.Status204NoContent).Produces<ErrorResponse>(StatusCodes.Status409Conflict);
        decisions.MapPost("/{decisionId:guid}/accept", DecisionEndpoints.Accept).RequireHuman()
            .Accepts<AcceptDecisionRequest>("application/json")
            .Produces<DecisionDto>()
            .Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
            .Produces<ErrorResponse>(StatusCodes.Status403Forbidden)
            .Produces<DecisionChangedResponse>(StatusCodes.Status409Conflict);
        decisions.MapPost("/{decisionId:guid}/reject", DecisionEndpoints.Reject).RequireHuman()
            .Accepts<RejectDecisionRequest>("application/json")
            .Produces<DecisionDto>()
            .Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
            .Produces<ErrorResponse>(StatusCodes.Status403Forbidden)
            .Produces<DecisionChangedResponse>(StatusCodes.Status409Conflict);

        var questions = app.MapGroup("/api/projects/{projectId}/questions")
            .RequireAuthorization().WithBodyLimit(RequestBodyLimit.Default).WithTags("Questions");
        questions.MapGet("/", Questions.List).AllowAgent(AgentScope.Read).Produces<Page<QuestionDto>>();
        questions.MapPost("/", Questions.Create).AllowAgent(AgentScope.Propose)
            .Accepts<QuestionRequest>("application/json")
            .Produces<QuestionDto>(StatusCodes.Status201Created)
            .Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
            .Produces<ErrorResponse>(StatusCodes.Status409Conflict);
        questions.MapPost("/keep", Questions.Keep).RequireHuman()
            .Accepts<KeepQuestionRequest>("application/json")
            .Produces<QuestionDto>(StatusCodes.Status201Created)
            .Produces<QuestionDto>()
            .Produces<ErrorResponse>(StatusCodes.Status404NotFound);
        questions.MapPost("/{questionId:guid}/answer", Questions.Answer).RequireHuman()
            .Accepts<AnswerQuestionRequest>("application/json")
            .Produces<QuestionDto>().Produces<ErrorResponse>(StatusCodes.Status404NotFound).Produces<ErrorResponse>(StatusCodes.Status409Conflict);
        questions.MapPost("/{questionId:guid}/drop", Questions.Drop).RequireHuman()
            .Produces<QuestionDto>().Produces<ErrorResponse>(StatusCodes.Status404NotFound).Produces<ErrorResponse>(StatusCodes.Status409Conflict);
        questions.MapDelete("/{questionId:guid}", Questions.Delete).RequireHuman()
            .Produces(StatusCodes.Status204NoContent).Produces<ErrorResponse>(StatusCodes.Status404NotFound);

        return app;
    }
}

/// <summary>
/// What the review tables keep when a project is deleted: nothing. A decision is a stream, so
/// its stream is deleted too, and a rebuild of the read model does not bring the row back.
/// Every statement is safe to run again, and <see cref="DeleteOrphansAsync"/> repeats the
/// work for a project whose delete was cut short.
/// </summary>
public sealed class ReviewRetention(AppDbContext db, IEventStore store) : IProjectRetention
{
    public async Task DeleteProjectAsync(DeletedProject project, CancellationToken ct)
    {
        await DeleteProjectAsync(project.Id, ct);
        await DeleteOrphansAsync(ct);
    }

    private async Task DeleteProjectAsync(Guid projectId, CancellationToken ct)
    {
        // A row exists only for a decision stream, so each ID names one. The projection
        // removes the row when it sees the tombstone.
        foreach (var decisionId in await db.Decisions.AsNoTracking().Where(d => d.ProjectId == projectId).Select(d => d.Id).ToListAsync(ct))
            await store.DeleteStream(StreamId.From(decisionId), ct);

        await db.Decisions.Where(d => d.ProjectId == projectId).ExecuteDeleteAsync(ct);
        await db.DecisionNumbering.Where(n => n.ProjectId == projectId).ExecuteDeleteAsync(ct);
        await db.Dispositions.Where(d => d.ProjectId == projectId).ExecuteDeleteAsync(ct);
        await db.ReviewSnapshots.Where(s => s.ProjectId == projectId).ExecuteDeleteAsync(ct);
        await db.Questions.Where(q => q.ProjectId == projectId).ExecuteDeleteAsync(ct);
        await db.Critiques.Where(c => c.ProjectId == projectId).ExecuteDeleteAsync(ct);
    }

    /// <summary>Runs <see cref="DeleteProjectAsync"/> for every project that the review tables name and that no longer exists.</summary>
    private async Task DeleteOrphansAsync(CancellationToken ct)
    {
        var gone = await db.Database.SqlQuery<Guid>($"""
            SELECT i.project_id AS "Value"
            FROM (SELECT project_id FROM review.decisions
                  UNION SELECT project_id FROM review.decision_numbering
                  UNION SELECT project_id FROM review.dispositions
                  UNION SELECT project_id FROM review.snapshots
                  UNION SELECT project_id FROM review.questions
                  UNION SELECT project_id FROM review.critiques) i
            WHERE NOT EXISTS (SELECT 1 FROM workspaces.projects p WHERE p.id = i.project_id)
            """).ToListAsync(ct);
        foreach (var projectId in gone)
            await DeleteProjectAsync(projectId, ct);
    }
}
