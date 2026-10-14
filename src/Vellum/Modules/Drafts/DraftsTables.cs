// src/Vellum/Modules/Drafts/AppDbContext.cs
using Microsoft.EntityFrameworkCore;
using Vellum.Modules.Drafts.Entities;
using Vellum.Shared;

namespace Vellum.Modules.Drafts;

/// <summary>The tables of the module, in the Postgres schema <c>drafts</c>.</summary>
public static class DraftsTables
{
    public const string Schema = "drafts";

    public static void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<DraftEntity>(b =>
        {
            b.ToTable("drafts", Schema);
            b.HasKey(d => d.Id);
            b.HasIndex(d => d.ProjectId);
            b.HasIndex(d => new { d.ProjectId, d.Status });
            b.Property(d => d.CreatedAt).HasDefaultValueSql("now()");
            b.Property(d => d.BaseSnapshot).HasColumnType("jsonb");
        });

        modelBuilder.Entity<CommentEntity>(b =>
        {
            b.ToTable("comments", Schema);
            b.HasKey(c => c.Id);
            b.HasIndex(c => c.DraftId);
            b.Property(c => c.CreatedAt).HasDefaultValueSql("now()");
            b.Property(c => c.UpdatedAt).HasDefaultValueSql("now()");
            b.HasOne<DraftEntity>()
                .WithMany()
                .HasForeignKey(c => c.DraftId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
