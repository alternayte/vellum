// src/Vellum/Modules/Docs/DocsDbContext.cs
using Microsoft.EntityFrameworkCore;
using Vellum.Modules.Docs.Entities;

namespace Vellum.Modules.Docs;

public class DocsDbContext : DbContext
{
    public DocsDbContext(DbContextOptions<DocsDbContext> options) : base(options) { }

    public DbSet<SpaceEntity> Spaces => Set<SpaceEntity>();
    public DbSet<DocumentEntity> Documents => Set<DocumentEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("docs");

        modelBuilder.Entity<SpaceEntity>(b =>
        {
            b.HasKey(s => s.Id);
            b.HasIndex(s => s.ProjectId);
            b.Property(s => s.CreatedAt).HasDefaultValueSql("now()");
            b.Property(s => s.UpdatedAt).HasDefaultValueSql("now()");
        });

        modelBuilder.Entity<DocumentEntity>(b =>
        {
            b.HasKey(d => d.Id);
            b.HasIndex(d => d.ProjectId);
            b.HasIndex(d => d.SpaceId);
            b.HasIndex(d => d.ElementId);
            b.Property(d => d.CreatedAt).HasDefaultValueSql("now()");
            b.Property(d => d.UpdatedAt).HasDefaultValueSql("now()");
            b.HasOne<SpaceEntity>()
                .WithMany()
                .HasForeignKey(d => d.SpaceId)
                .OnDelete(DeleteBehavior.SetNull);
        });
    }
}
