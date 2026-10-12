using Vellum.Modules.Trace.Engine;
using Vellum.Shared;

namespace Vellum.Modules.Trace.Scenarios;

public static class ScenarioEndpoints
{
    public static WebApplication MapScenarioEndpoints(this WebApplication app)
    {
        var scenarios = app.MapGroup("/api/projects/{projectId}/scenarios")
            .RequireAuthorization()
            .WithTags("Scenarios");

        scenarios.MapGet("/", ReadScenarios.List)
            .Produces<Page<ScenarioSummaryDto>>();

        scenarios.MapPost("/", SaveScenario.Create)
            .Accepts<Scenario>("application/json")
            .Produces<Scenario>(StatusCodes.Status201Created);

        scenarios.MapGet("/{scenarioId}", ReadScenarios.Get)
            .Produces<Scenario>();

        scenarios.MapPut("/{scenarioId}", SaveScenario.Update)
            .Accepts<Scenario>("application/json")
            .Produces<Scenario>();

        scenarios.MapDelete("/{scenarioId}", DeleteScenario.Handle)
            .Produces(StatusCodes.Status204NoContent);

        // A run reads a pinned model and writes nothing, so a Viewer may run.
        scenarios.MapPost("/{scenarioId}/run", RunScenario.Handle)
            .Produces<RunScenarioResponse>();

        return app;
    }
}
