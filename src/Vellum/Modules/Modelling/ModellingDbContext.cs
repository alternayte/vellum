using Microsoft.EntityFrameworkCore;
using Vellum.Modules.Modelling.Entities;

namespace Vellum.Modules.Modelling;

public class ModellingDbContext : DbContext
{
    public ModellingDbContext(DbContextOptions<ModellingDbContext> options) : base(options) { }

    public DbSet<ElementEntity> Elements => Set<ElementEntity>();
    public DbSet<RelationshipEntity> Relationships => Set<RelationshipEntity>();
    public DbSet<MessageEntity> Messages => Set<MessageEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("modelling");

        modelBuilder.Entity<ElementEntity>(b =>
        {
            b.HasKey(e => e.Id);
            b.HasIndex(e => new { e.ProjectId, e.Branch });
            b.HasIndex(e => new { e.ProjectId, e.Branch, e.ParentId });
        });

        modelBuilder.Entity<RelationshipEntity>(b =>
        {
            b.HasKey(r => r.Id);
            b.HasIndex(r => new { r.ProjectId, r.Branch });
        });

        modelBuilder.Entity<MessageEntity>(b =>
        {
            b.HasKey(m => m.Id);
            b.HasIndex(m => new { m.ProjectId, m.Branch });
            b.HasIndex(m => new { m.ProjectId, m.Branch, m.ProducerId });
        });
    }
}
