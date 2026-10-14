using System.Data;
using Deedbox;
using Microsoft.EntityFrameworkCore;
using Vellum.Modules.Modelling;
using Vellum.Modules.Modelling.Model;
using Vellum.Modules.Schemas;
using Vellum.Modules.Trace.Engine;
using Vellum.Modules.Workspaces.Authorization;
using Vellum.Shared;

namespace Vellum.Modules.Trace.Scenarios;

/// <summary>
/// One playback. <paramref name="ModelVersion"/> is the version of the branch stream the run
/// was pinned to; the same scenario over the same version gives the same
/// <paramref name="Hash"/>. A scenario with <paramref name="Issues"/> has no events: it was
/// not played. <paramref name="Labels"/> names every ID an event can carry.
/// </summary>
public sealed record RunScenarioResponse(
    string EngineVersion,
    string Hash,
    long ModelVersion,
    IReadOnlyList<ScenarioIssue> Issues,
    IReadOnlyList<TraceEvent> Events,
    IReadOnlyDictionary<string, StepOutcome> Outcomes,
    IReadOnlyList<AssertionResult> Assertions,
    IReadOnlyDictionary<Guid, string> Labels);

public static class RunScenario
{
    public static Task<IResult> Handle(
        Guid projectId, Guid scenarioId, Guid? branchId,
        HttpContext http,
        IEventStore store,
        AppDbContext db,
        CancellationToken ct) =>
        ModelBranches.RunAsync(http, projectId, branchId, WorkspaceRole.Viewer, async (streamId, _) =>
        {
            // One snapshot: the scenario, the branch's model and the project's schemas are read
            // as of the same instant. The transaction only reads and is rolled back.
            await using var snapshot = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
            var pinned = store.UseDbContext(db);

            var scenario = await ScenarioStreams.LoadAsync(pinned, scenarioId, ct);
            if (scenario.DocumentIn(projectId) is not { } document)
                return Results.NotFound(new ErrorResponse("not_found", "Scenario not found"));

            var (model, modelVersion) = await pinned.Load<ModelState>(StreamId.From(streamId), ct);

            // Only the project's own schemas count. A reference to anything else stays
            // unresolved, and the engine reports it as an issue.
            var referenced = ContractDecider.ReferencedSchemaIds(model);
            var schemas = await db.Schemas.AsNoTracking()
                .Where(s => s.ProjectId == projectId && referenced.Contains(s.Id))
                .Select(s => new { s.Id, s.Content })
                .ToListAsync(ct);

            var result = TraceEngine.Run(
                TraceModelBuilder.Build(model, schemas.ToDictionary(s => new SchemaId(s.Id), s => s.Content)),
                document);

            return Results.Ok(new RunScenarioResponse(
                result.EngineVersion, result.Hash, modelVersion,
                result.Issues, result.Events, result.Outcomes, result.Assertions,
                TraceModelBuilder.Labels(model)));
        }, ct);
}
