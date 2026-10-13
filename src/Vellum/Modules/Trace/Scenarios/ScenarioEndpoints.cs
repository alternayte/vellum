using Vellum.Modules.Identity.AgentTokens;
using Vellum.Modules.Trace.Engine;
using Vellum.Shared;

namespace Vellum.Modules.Trace.Scenarios;

public static class ScenarioEndpoints
{
    public static WebApplication MapScenarioEndpoints(this WebApplication app)
    {
        var scenarios = app.MapGroup("/api/projects/{projectId}/scenarios")
            .RequireAuthorization()
            // A scenario is stored in at most ScenarioShape.MaxDocumentBytes. The request may be
            // larger, because an author's indentation is not stored.
            .WithBodyLimit(RequestBodyLimit.Document)
            .WithTags("Scenarios");

        scenarios.MapGet("/", ReadScenarios.List).AllowAgent(AgentScope.Read)
            .Produces<Page<ScenarioSummaryDto>>();

        scenarios.MapPost("/", SaveScenario.Create)
            .Accepts<Scenario>("application/json")
            .Produces<Scenario>(StatusCodes.Status201Created);

        scenarios.MapGet("/{scenarioId}", ReadScenarios.Get).AllowAgent(AgentScope.Read)
            .Produces<ScenarioDto>();

        scenarios.MapPut("/{scenarioId}", SaveScenario.Update)
            .Accepts<Scenario>("application/json")
            .Produces<Scenario>();

        scenarios.MapDelete("/{scenarioId}", DeleteScenario.Handle)
            .Produces(StatusCodes.Status204NoContent);

        // A run reads a pinned model and writes nothing, so a Viewer may run, and so may an
        // agent token that only reads.
        scenarios.MapPost("/{scenarioId}/run", RunScenario.Handle).AllowAgent(AgentScope.Read)
            .Produces<RunScenarioResponse>();

        return app;
    }
}
