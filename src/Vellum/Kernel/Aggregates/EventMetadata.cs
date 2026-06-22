namespace Vellum.Kernel.Aggregates;

public sealed record EventMetadata
{
    public required Guid ActorId { get; init; }
    public required Guid CorrelationId { get; init; }
    public Guid? DraftId { get; init; }
    public Guid? MergeCorrelationId { get; init; }
}
