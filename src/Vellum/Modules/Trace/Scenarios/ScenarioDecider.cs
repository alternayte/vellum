using Vellum.Modules.Trace.Engine;
using Vellum.Shared;
using Decision = Vellum.Shared.CommandResult<System.Collections.Generic.IReadOnlyList<object>>;

namespace Vellum.Modules.Trace.Scenarios;

/// <summary>
/// A scenario is saved as given. The only conditions are the ones a list needs: a name, and
/// a project that owns it. Contract references are checked when the scenario runs, against
/// the branch it runs on.
/// </summary>
public static class ScenarioDecider
{
    public static Decision Create(ScenarioState state, Guid projectId, Scenario document)
    {
        // A deleted scenario keeps its ID: the stream holds its history.
        if (state.Document is not null || state.Deleted)
            return new Decision.Conflict("Scenario with this ID already exists");

        return Save(projectId, document);
    }

    public static Decision Update(ScenarioState state, Guid projectId, Scenario document) =>
        state.DocumentIn(projectId) is null
            ? new Decision.NotFound("Scenario not found")
            : Save(projectId, document);

    public static Decision Delete(ScenarioState state, Guid projectId) =>
        state.DocumentIn(projectId) is { } document
            ? new Decision.Success([new ScenarioEvent.ScenarioDeleted(document.Id)])
            : new Decision.NotFound("Scenario not found");

    private static Decision Save(Guid projectId, Scenario document) =>
        string.IsNullOrWhiteSpace(document.Name)
            ? new Decision.Invalid([new ValidationError("name", "Name is required")])
            : new Decision.Success([new ScenarioEvent.ScenarioSaved(projectId, document.Name, document)]);
}
