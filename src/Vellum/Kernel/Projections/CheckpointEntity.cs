namespace Vellum.Kernel.Projections;

public class CheckpointEntity
{
    public string ProjectionName { get; set; } = null!;
    public long LastProcessedPosition { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
