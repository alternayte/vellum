using Microsoft.EntityFrameworkCore;
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

public class DraftEntity
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public string Name { get; set; } = null!;
    public Guid StreamId { get; set; }
    public Guid BaseStreamId { get; set; }
    public int ForkVersion { get; set; }
    public string Status { get; set; } = "open";
    public string CreatedBy { get; set; } = null!;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? MergedAt { get; set; }
    public DateTimeOffset? AbandonedAt { get; set; }
    public string BaseSnapshot { get; set; } = null!;
}

public class CommentEntity
{
    public Guid Id { get; set; }
    public Guid DraftId { get; set; }
    public Guid? EntityId { get; set; }
    public string? EntityType { get; set; }
    public string Author { get; set; } = null!;
    public string Body { get; set; } = null!;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
