using Microsoft.EntityFrameworkCore;
using Vellum.Modules.Views.Entities;

namespace Vellum.Modules.Views;

public class ViewsDbContext : DbContext
{
    public ViewsDbContext(DbContextOptions<ViewsDbContext> options) : base(options) { }

    public DbSet<ViewEntity> Views => Set<ViewEntity>();
    public DbSet<LayoutPositionEntity> LayoutPositions => Set<LayoutPositionEntity>();
    public DbSet<LayoutEdgeEntity> LayoutEdges => Set<LayoutEdgeEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("views");

        modelBuilder.Entity<ViewEntity>(b =>
        {
            b.HasKey(v => v.Id);
            b.HasIndex(v => v.ProjectId);
            b.Property(v => v.CreatedAt).HasDefaultValueSql("now()");
            b.Property(v => v.UpdatedAt).HasDefaultValueSql("now()");
            b.Property(v => v.VisibleElementIds).HasColumnType("uuid[]");
        });

        modelBuilder.Entity<LayoutPositionEntity>(b =>
        {
            b.HasKey(l => l.Id);
            b.HasIndex(l => new { l.ViewId, l.ElementId }).IsUnique();
            b.HasOne<ViewEntity>()
                .WithMany()
                .HasForeignKey(p => p.ViewId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<LayoutEdgeEntity>(b =>
        {
            b.HasKey(l => l.Id);
            b.HasIndex(l => new { l.ViewId, l.RelationshipId }).IsUnique();
            b.Property(l => l.RoutePoints).HasColumnType("jsonb");
            b.HasOne<ViewEntity>()
                .WithMany()
                .HasForeignKey(e => e.ViewId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
