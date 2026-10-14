using Microsoft.EntityFrameworkCore;
using Vellum.Modules.Modelling.Entities;

namespace Vellum.Modules.Modelling;

/// <summary>The tables of the module, in the Postgres schema <c>modelling</c>.</summary>
public static class ModellingTables
{
    public const string Schema = "modelling";

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
