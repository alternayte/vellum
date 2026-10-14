using System.Security.Claims;
using Deedbox;
using Vellum.Modules.Workspaces.Authorization;
using Vellum.Shared;

namespace Vellum.Modules.Trace.Scenarios;

public static class DeleteScenario
{
    public static async Task<IResult> Handle(
        Guid projectId, Guid scenarioId,
        ClaimsPrincipal user,
        WorkspaceAuthorizationService auth,
        IEventStore store,
        CancellationToken ct)
    {
        var userId = user.UserId();
        await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Editor, ct);

        var result = await ScenarioStreams.DecideAsync(
            store, scenarioId,
            state => ScenarioDecider.Delete(state, projectId),
            _ => true,
            new CommandResult<bool>.NotFound("Scenario not found"),
            ct);
        return result is CommandResult<bool>.Success ? Results.NoContent() : result.ToHttpResult();
    }
}
