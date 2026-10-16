using Microsoft.EntityFrameworkCore;
using Vellum.Modules.Modelling;

namespace Vellum.Modules.Schemas;

/// <summary>The tables of the module, in the Postgres schema <c>schemas</c>.</summary>
public static class SchemasTables
{
    public const string Schema = "schemas";

    public static void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SchemaEntity>(b =>
        {
            b.ToTable("schemas", Schema);
            b.HasKey(s => s.Id);
            b.HasIndex(s => s.ProjectId);
        });
    }
}

public class SchemaEntity
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public string Name { get; set; } = null!;
    public string? Description { get; set; }
    public string Content { get; set; } = null!;
    public int Version { get; set; }
    /// <summary>The truth label. A row of a stream without a recorded label is <c>declared</c>.</summary>
    public FactStatus FactStatus { get; set; } = FactStatus.Declared;
    public string? ProvenanceSource { get; set; }
    public string? ProvenanceRevision { get; set; }
    public double? ProvenanceConfidence { get; set; }
    public Guid? ProvenanceAcceptedBy { get; set; }
    public DateTimeOffset? ProvenanceAcceptedAt { get; set; }
    public bool ProvenanceContentNotHeld { get; set; }
}
