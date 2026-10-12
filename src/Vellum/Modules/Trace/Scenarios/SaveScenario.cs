using System.Security.Claims;
using System.Text.Json;
using Deedbox;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;
using Vellum.Modules.Trace.Engine;
using Vellum.Modules.Workspaces.Authorization;
using Vellum.Shared;

namespace Vellum.Modules.Trace.Scenarios;

/// <summary>
/// Create and replace. The body is the scenario document itself. A document with validation
/// issues is saved (SDD §8: blocking issues prevent Build Ready, not saving); a body that
/// cannot be read as a scenario document is refused with 400.
/// </summary>
public static class SaveScenario
{
    public static async Task<IResult> Create(
        Guid projectId,
        HttpContext http,
        ClaimsPrincipal user,
        WorkspaceAuthorizationService auth,
        IEventStore store,
        IOptions<JsonOptions> json,
        CancellationToken ct)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier)!;
        await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Editor, ct);

        var (document, error) = await ReadAsync(http.Request, json.Value.SerializerOptions, ct);
        if (document is null)
            return error!;

        if (document.Id.Value == Guid.Empty)
        {
            return Results.BadRequest(new ErrorResponse("validation_error", "Validation failed",
                Errors: [new FieldError("id", "A scenario needs an ID")]));
        }

        var result = await ScenarioStreams.DecideAsync(
            store, document.Id,
            state => ScenarioDecider.Create(state, projectId, document),
            state => state.Document!,
            new CommandResult<Scenario>.Conflict("This ID is already in use"),
            ct);
        return result.ToCreatedResult($"/api/projects/{projectId}/scenarios/{document.Id}");
    }

    public static async Task<IResult> Update(
        Guid projectId, Guid scenarioId,
        HttpContext http,
        ClaimsPrincipal user,
        WorkspaceAuthorizationService auth,
        IEventStore store,
        IOptions<JsonOptions> json,
        CancellationToken ct)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier)!;
        await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Editor, ct);

        var (document, error) = await ReadAsync(http.Request, json.Value.SerializerOptions, ct);
        if (document is null)
            return error!;

        // The route names the scenario. A body may leave the ID out, but may not name another.
        if (document.Id.Value != Guid.Empty && document.Id.Value != scenarioId)
        {
            return Results.BadRequest(new ErrorResponse("validation_error", "Validation failed",
                Errors: [new FieldError("id", "The ID in the body differs from the ID in the path")]));
        }

        var replacement = document with { Id = scenarioId };
        var result = await ScenarioStreams.DecideAsync(
            store, scenarioId,
            state => ScenarioDecider.Update(state, projectId, replacement),
            state => state.Document!,
            new CommandResult<Scenario>.NotFound("Scenario not found"),
            ct);
        return result.ToHttpResult();
    }

    private static async Task<(Scenario? Document, IResult? Error)> ReadAsync(
        HttpRequest request, JsonSerializerOptions options, CancellationToken ct)
    {
        try
        {
            var document = await JsonSerializer.DeserializeAsync<Scenario>(request.Body, options, ct);
            return document is null
                ? (null, Malformed("The body is null."))
                : (document, null);
        }
        // NotSupportedException: a step or assertion without a known `type` cannot be built.
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            return (null, Malformed(ex.Message));
        }
    }

    private static IResult Malformed(string detail) =>
        Results.BadRequest(new ErrorResponse("invalid_scenario", "The body is not a scenario document", detail));
}
