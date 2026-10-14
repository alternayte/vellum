using Microsoft.EntityFrameworkCore;
using Vellum.Modules.Views.Entities;

namespace Vellum.Modules.Views;

/// <summary>The tables of the module, in the Postgres schema <c>views</c>.</summary>
public static class ViewsTables
{
    public const string Schema = "views";

    public static void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ViewEntity>(b =>
        {
            b.ToTable("views", Schema);
            b.HasKey(v => v.Id);
            b.HasIndex(v => v.ProjectId);
            b.Property(v => v.CreatedAt).HasDefaultValueSql("now()");
            b.Property(v => v.UpdatedAt).HasDefaultValueSql("now()");
            b.Property(v => v.VisibleElementIds).HasColumnType("uuid[]");
        });

        modelBuilder.Entity<LayoutPositionEntity>(b =>
        {
            b.ToTable("layout_positions", Schema);
            b.HasKey(l => l.Id);
            b.HasIndex(l => new { l.ViewId, l.ElementId }).IsUnique();
            b.HasOne<ViewEntity>()
                .WithMany()
                .HasForeignKey(p => p.ViewId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<LayoutEdgeEntity>(b =>
        {
            b.ToTable("layout_edges", Schema);
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
