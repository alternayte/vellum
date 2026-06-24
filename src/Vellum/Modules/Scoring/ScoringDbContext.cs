using Microsoft.EntityFrameworkCore;
using Vellum.Modules.Scoring.Entities;

namespace Vellum.Modules.Scoring;

public class ScoringDbContext(DbContextOptions<ScoringDbContext> options) : DbContext(options)
{
    public DbSet<ScoreEntity> Scores => Set<ScoreEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("scoring");

        modelBuilder.Entity<ScoreEntity>(b =>
        {
            b.HasKey(s => s.Id);
            b.HasIndex(s => s.DocId);
            b.HasIndex(s => s.ProjectId);
            b.Property(s => s.CreatedAt).HasDefaultValueSql("now()");
            b.Property(s => s.CriteriaResultsJson).HasColumnType("jsonb");
        });
    }
}
