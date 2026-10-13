using Vellum.Modules.Trace.Engine;
using Vellum.Shared;
using Decision = Vellum.Shared.CommandResult<System.Collections.Generic.IReadOnlyList<object>>;

namespace Vellum.Modules.Trace.Scenarios;

/// <summary>
/// A scenario is saved as given, inside the limits of <see cref="ScenarioShape"/>. The other
/// conditions are the ones a list needs: a name, and a project that owns it. Contract
/// references are checked when the scenario runs, against the branch it runs on.
/// </summary>
public static class ScenarioDecider
{
    /// <summary>
    /// The one answer to a create with an ID that is taken, whatever holds it: a scenario of
    /// this project or of another, a deleted scenario, a model branch or a schema. A second
    /// wording would tell the caller what an ID of another workspace names.
    /// </summary>
    public const string IdInUse = "This ID is already in use. Choose another ID.";

    /// <summary>The most scenarios one project holds.</summary>
    public const int MaxPerProject = 2_000;

    /// <param name="origin">Set when the document is an accepted AI flow proposal.</param>
    public static Decision Create(ScenarioState state, Guid projectId, Scenario document, ScenarioOrigin? origin = null)
    {
        // A deleted scenario keeps its ID: the stream holds its history.
        if (state.Document is not null || state.Deleted)
            return new Decision.Conflict(IdInUse);

        return Save(projectId, document, origin);
    }

    public static Decision Update(ScenarioState state, Guid projectId, Scenario document, ScenarioOrigin? origin = null) =>
        state.DocumentIn(projectId) is null
            ? new Decision.NotFound("Scenario not found")
            : Save(projectId, document, origin);

    public static Decision Delete(ScenarioState state, Guid projectId) =>
        state.DocumentIn(projectId) is { } document
            ? new Decision.Success([new ScenarioEvent.ScenarioDeleted(document.Id)])
            : new Decision.NotFound("Scenario not found");

    private static Decision Save(Guid projectId, Scenario document, ScenarioOrigin? origin)
    {
        if (string.IsNullOrWhiteSpace(document.Name))
            return new Decision.Invalid([new ValidationError("name", "Name is required")]);
        if (ScenarioShape.Check(document) is { } shapeError)
            return new Decision.Invalid([shapeError]);

        return new Decision.Success([new ScenarioEvent.ScenarioSaved(projectId, document.Name, document, origin)]);
    }
}
