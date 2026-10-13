namespace Vellum.Modules.Modelling.Entities;

public class ElementEntity
{
    /// <summary>Synthetic row identity. The architecture ID lives in <see cref="Id"/> and is unique per branch.</summary>
    public Guid RowId { get; set; } = Guid.NewGuid();
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public Guid Branch { get; set; }
    public string Kind { get; set; } = null!;
    public string Name { get; set; } = null!;
    public string? Description { get; set; }
    public string? Technology { get; set; }
    public Guid? OwnerId { get; set; }
    public string Status { get; set; } = "current";
    public Guid? ParentId { get; set; }
    public string[] Tags { get; set; } = [];
    public string? Icon { get; set; }
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
