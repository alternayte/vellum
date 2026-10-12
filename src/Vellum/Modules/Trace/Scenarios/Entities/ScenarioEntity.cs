namespace Vellum.Modules.Trace.Scenarios.Entities;

public class ScenarioEntity
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public string Name { get; set; } = null!;
}
