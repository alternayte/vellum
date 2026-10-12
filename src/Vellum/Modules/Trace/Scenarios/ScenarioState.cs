using Deedbox;
using Vellum.Modules.Trace.Engine;

namespace Vellum.Modules.Trace.Scenarios;

/// <summary>One scenario. It belongs to the project that created it and to no other.</summary>
public sealed record ScenarioState(Guid? ProjectId, string? Name, Scenario? Document, bool Deleted)
    : IState<ScenarioState>
{
    public static ScenarioState Initial => new(null, null, null, false);

    public static ScenarioState Evolve(ScenarioState state, object @event) => @event switch
    {
        ScenarioEvent.ScenarioSaved e => state with { ProjectId = e.ProjectId, Name = e.Name, Document = e.Document },
        ScenarioEvent.ScenarioDeleted => state with { Document = null, Deleted = true },
        _ => state
    };

    /// <summary>The document, when the scenario exists in <paramref name="projectId"/>.</summary>
    public Scenario? DocumentIn(Guid projectId) =>
        !Deleted && ProjectId == projectId ? Document : null;
}
