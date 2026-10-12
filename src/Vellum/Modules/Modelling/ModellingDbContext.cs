using Microsoft.EntityFrameworkCore;
using Vellum.Modules.Modelling.Entities;

namespace Vellum.Modules.Modelling;

public class ModellingDbContext : DbContext
{
    public ModellingDbContext(DbContextOptions<ModellingDbContext> options) : base(options) { }

    public DbSet<ElementEntity> Elements => Set<ElementEntity>();
    public DbSet<RelationshipEntity> Relationships => Set<RelationshipEntity>();
    public DbSet<MessageEntity> Messages => Set<MessageEntity>();
    public DbSet<RevisionEntity> Revisions => Set<RevisionEntity>();
    public DbSet<VariantEntity> Variants => Set<VariantEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("modelling");

        modelBuilder.Entity<ElementEntity>(b =>
        {
            b.HasKey(e => e.RowId);
            b.HasIndex(e => new { e.ProjectId, e.Branch, e.Id }).IsUnique();
            b.HasIndex(e => new { e.ProjectId, e.Branch, e.ParentId });
        });

        modelBuilder.Entity<RelationshipEntity>(b =>
        {
            b.HasKey(r => r.RowId);
            b.HasIndex(r => new { r.ProjectId, r.Branch, r.Id }).IsUnique();
        });

        modelBuilder.Entity<MessageEntity>(b =>
        {
            b.HasKey(m => m.RowId);
            b.HasIndex(m => new { m.ProjectId, m.Branch, m.Id }).IsUnique();
            b.HasIndex(m => new { m.ProjectId, m.Branch, m.ProducerId });
        });

        modelBuilder.Entity<RevisionEntity>(b =>
        {
            b.HasKey(r => r.Id);
            b.HasIndex(r => new { r.ProjectId, r.Name }).IsUnique();
        });

        modelBuilder.Entity<VariantEntity>(b =>
        {
            b.HasKey(v => v.Id);
            b.HasIndex(v => new { v.ProjectId, v.Name }).IsUnique();
            b.HasIndex(v => v.StreamId).IsUnique();
        });
    }
}
