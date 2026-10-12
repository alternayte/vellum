using Vellum.Modules.Modelling.Model;
using Vellum.Modules.Trace.Engine;

namespace Vellum.Modules.Trace.Scenarios;

/// <summary>The events of a scenario stream. Plain records; the stream registers every nested type.</summary>
public static class ScenarioEvent
{
    /// <summary>
    /// The whole document as the author saved it. A scenario is saved whether or not it is
    /// valid against the model; validity is a result of a run, not a condition of saving.
    /// </summary>
    public sealed record ScenarioSaved(Guid ProjectId, string Name, Scenario Document);

    public sealed record ScenarioDeleted(ScenarioId ScenarioId);
}
