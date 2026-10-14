// src/Vellum/Modules/Docs/AppDbContext.cs
using Microsoft.EntityFrameworkCore;
using Vellum.Modules.Docs.Entities;
using Vellum.Shared;

namespace Vellum.Modules.Docs;

/// <summary>The tables of the module, in the Postgres schema <c>docs</c>.</summary>
public static class DocsTables
{
    public const string Schema = "docs";

    public static void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SpaceEntity>(b =>
        {
            b.ToTable("spaces", Schema);
            b.HasKey(s => s.Id);
            b.HasIndex(s => s.ProjectId);
            b.Property(s => s.CreatedAt).HasDefaultValueSql("now()");
            b.Property(s => s.UpdatedAt).HasDefaultValueSql("now()");
        });

        modelBuilder.Entity<DocumentEntity>(b =>
        {
            b.ToTable("documents", Schema);
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
