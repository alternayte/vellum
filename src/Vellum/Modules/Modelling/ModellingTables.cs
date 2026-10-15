using Microsoft.EntityFrameworkCore;
using Vellum.Shared;

namespace Vellum.Modules.Modelling;

/// <summary>The tables of the module, in the Postgres schema <c>modelling</c>.</summary>
public static class ModellingTables
{
    public const string Schema = "modelling";

    public static readonly IReadOnlyList<PostgresEnum> Enums =
    [
        PostgresEnum.Of<ElementKind>("element_kind", Schema),
        PostgresEnum.Of<ElementStatus>("element_status", Schema),
        PostgresEnum.Of<RelationshipKind>("relationship_kind", Schema),
        PostgresEnum.Of<FactStatus>("fact_status", Schema),
        PostgresEnum.Of<VariantStatus>("variant_status", Schema),
    ];

    public static void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ElementEntity>(b =>
        {
            b.ToTable("elements", Schema);
            b.HasKey(e => e.RowId);
            b.HasIndex(e => new { e.ProjectId, e.Branch, e.Id }).IsUnique();
            b.HasIndex(e => new { e.ProjectId, e.Branch, e.ParentId });
        });

        modelBuilder.Entity<RelationshipEntity>(b =>
        {
            b.ToTable("relationships", Schema);
            b.HasKey(r => r.RowId);
            b.HasIndex(r => new { r.ProjectId, r.Branch, r.Id }).IsUnique();
        });

        modelBuilder.Entity<MessageEntity>(b =>
        {
            b.ToTable("messages", Schema);
            b.HasKey(m => m.RowId);
            b.HasIndex(m => new { m.ProjectId, m.Branch, m.Id }).IsUnique();
            b.HasIndex(m => new { m.ProjectId, m.Branch, m.ProducerId });
        });

        modelBuilder.Entity<RevisionEntity>(b =>
        {
            b.ToTable("revisions", Schema);
            b.HasKey(r => r.Id);
            b.HasIndex(r => new { r.ProjectId, r.Name }).IsUnique();
        });

        modelBuilder.Entity<VariantEntity>(b =>
        {
            b.ToTable("variants", Schema);
            b.HasKey(v => v.Id);
            b.HasIndex(v => new { v.ProjectId, v.Name }).IsUnique();
            b.HasIndex(v => v.StreamId).IsUnique();
        });
    }
}

public class ElementEntity
{
    /// <summary>Synthetic row identity. The architecture ID lives in <see cref="Id"/> and is unique per branch.</summary>
    public Guid RowId { get; set; } = Guid.NewGuid();
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public Guid Branch { get; set; }
    public ElementKind Kind { get; set; }
    public string Name { get; set; } = null!;
    public string? Description { get; set; }
    public string? Technology { get; set; }
    public Guid? OwnerId { get; set; }
    public ElementStatus Status { get; set; } = ElementStatus.Current;
    public Guid? ParentId { get; set; }
    public string[] Tags { get; set; } = [];
    public string? Icon { get; set; }
    public FactStatus FactStatus { get; set; } = FactStatus.Declared;
    public string? ProvenanceSource { get; set; }
    public string? ProvenanceRevision { get; set; }
    public double? ProvenanceConfidence { get; set; }
    public Guid? ProvenanceAcceptedBy { get; set; }
    public DateTimeOffset? ProvenanceAcceptedAt { get; set; }
    public long? ProvenanceAcceptedAtVersion { get; set; }
    public Guid? ProvenanceAcceptedOnBranch { get; set; }
    public bool ProvenanceContentNotHeld { get; set; }
    /// <summary>Both stale columns are set, or both are null.</summary>
    public string? StaleSinceRevision { get; set; }
    public string? StaleReason { get; set; }
}

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
    public RelationshipKind Kind { get; set; } = RelationshipKind.Structural;
    public FactStatus FactStatus { get; set; } = FactStatus.Declared;
    public string? ProvenanceSource { get; set; }
    public string? ProvenanceRevision { get; set; }
    public double? ProvenanceConfidence { get; set; }
    public Guid? ProvenanceAcceptedBy { get; set; }
    public DateTimeOffset? ProvenanceAcceptedAt { get; set; }
    public long? ProvenanceAcceptedAtVersion { get; set; }
    public Guid? ProvenanceAcceptedOnBranch { get; set; }
    public bool ProvenanceContentNotHeld { get; set; }
    /// <summary>Both stale columns are set, or both are null.</summary>
    public string? StaleSinceRevision { get; set; }
    public string? StaleReason { get; set; }
}

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
    public FactStatus FactStatus { get; set; } = FactStatus.Declared;
    public string? ProvenanceSource { get; set; }
    public string? ProvenanceRevision { get; set; }
    public double? ProvenanceConfidence { get; set; }
    public Guid? ProvenanceAcceptedBy { get; set; }
    public DateTimeOffset? ProvenanceAcceptedAt { get; set; }
    public long? ProvenanceAcceptedAtVersion { get; set; }
    public Guid? ProvenanceAcceptedOnBranch { get; set; }
    public bool ProvenanceContentNotHeld { get; set; }
    /// <summary>Both stale columns are set, or both are null.</summary>
    public string? StaleSinceRevision { get; set; }
    public string? StaleReason { get; set; }
}

/// <summary>
/// A named, immutable snapshot of a model stream at a pinned version. The state is stored
/// with the revision so downstream features (diff, impact, agent briefs) can compare
/// pinned revisions without replaying the event log.
/// </summary>
public class RevisionEntity
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public string Name { get; set; } = null!;
    public string? Note { get; set; }
    public Guid StreamId { get; set; }
    public int Version { get; set; }
    public string State { get; set; } = null!;
    public string CreatedBy { get; set; } = null!;
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// A forked model stream used to compare architecture alternatives. Forking copies the
/// pinned state into a new event stream and materialises complete read-model rows for the
/// variant branch.
/// </summary>
public class VariantEntity
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public string Name { get; set; } = null!;
    public Guid StreamId { get; set; }
    public Guid BaseStreamId { get; set; }
    public Guid? BaseRevisionId { get; set; }
    public int ForkVersion { get; set; }
    public VariantStatus Status { get; set; } = VariantStatus.Open;
    public string CreatedBy { get; set; } = null!;
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>A variant is open while its row exists; deleting the variant deletes the row.</summary>
public enum VariantStatus
{
    Open,
}
