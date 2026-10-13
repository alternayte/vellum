namespace Vellum.Modules.Modelling.Entities;

public class MessageEntity
{
    /// <summary>Synthetic row identity. The architecture ID lives in <see cref="Id"/> and is unique per branch.</summary>
    public Guid RowId { get; set; } = Guid.NewGuid();
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public Guid Branch { get; set; }
    public string Name { get; set; } = null!;
    public string? Description { get; set; }
    public Guid ProducerId { get; set; }
    public Guid[] ConsumerIds { get; set; } = [];
    public Guid? SchemaId { get; set; }
    public string[] Tags { get; set; } = [];
    public string FactStatus { get; set; } = "declared";
    public string? ProvenanceSource { get; set; }
    public string? ProvenanceRevision { get; set; }
    public double? ProvenanceConfidence { get; set; }
    public Guid? ProvenanceAcceptedBy { get; set; }
    public DateTimeOffset? ProvenanceAcceptedAt { get; set; }
    /// <summary>Both stale columns are set, or both are null.</summary>
    public string? StaleSinceRevision { get; set; }
    public string? StaleReason { get; set; }
}
