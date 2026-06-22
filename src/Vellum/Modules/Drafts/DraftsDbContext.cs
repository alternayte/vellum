// src/Vellum/Modules/Drafts/DraftsDbContext.cs
using Microsoft.EntityFrameworkCore;
using Vellum.Modules.Drafts.Entities;

namespace Vellum.Modules.Drafts;

public class DraftsDbContext : DbContext
{
    public DraftsDbContext(DbContextOptions<DraftsDbContext> options) : base(options) { }

    public DbSet<DraftEntity> Drafts => Set<DraftEntity>();
    public DbSet<CommentEntity> Comments => Set<CommentEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("drafts");

        modelBuilder.Entity<DraftEntity>(b =>
        {
            b.HasKey(d => d.Id);
            b.HasIndex(d => d.ProjectId);
            b.HasIndex(d => new { d.ProjectId, d.Status });
            b.Property(d => d.CreatedAt).HasDefaultValueSql("now()");
            b.Property(d => d.BaseSnapshot).HasColumnType("jsonb");
        });

        modelBuilder.Entity<CommentEntity>(b =>
        {
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
