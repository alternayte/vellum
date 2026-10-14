using Microsoft.EntityFrameworkCore;
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

public class SpaceEntity
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public string Name { get; set; } = null!;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public class DocumentEntity
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public Guid? SpaceId { get; set; }
    public Guid? ElementId { get; set; }
    public string Title { get; set; } = null!;
    public string Content { get; set; } = string.Empty;
    public string CreatedBy { get; set; } = null!;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public Guid? DraftId { get; set; }
    public string? Type { get; set; }
}
