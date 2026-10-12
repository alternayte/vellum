namespace Vellum.Modules.Modelling.Entities;

public class RelationshipEntity
{
    /// <summary>Synthetic row identity. The architecture ID lives in <see cref="Id"/> and is unique per branch.</summary>
    public Guid RowId { get; set; } = Guid.NewGuid();
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public Guid Branch { get; set; }
    public Guid FromId { get; set; }
    public Guid ToId { get; set; }
    public string? Label { get; set; }
    public string? Technology { get; set; }
    public Guid? MessageId { get; set; }
    public string? LineShape { get; set; }
    public string Kind { get; set; } = "structural";
    public string FactStatus { get; set; } = "declared";
    public string? ProvenanceSource { get; set; }
    public string? ProvenanceRevision { get; set; }
    public double? ProvenanceConfidence { get; set; }
    public Guid? ProvenanceAcceptedBy { get; set; }
    public DateTimeOffset? ProvenanceAcceptedAt { get; set; }
}
