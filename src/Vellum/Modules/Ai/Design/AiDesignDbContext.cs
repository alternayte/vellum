using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Vellum.Modules.Ai.Design;

/// <summary>
/// One AI proposal: a flow (<c>flow</c>) or additions to the architecture (<c>architecture</c>).
/// A proposal is a suggestion that a person reviews. It is not part of the model and not a
/// scenario; accepting it writes through the same decisions as a person's own edit.
/// </summary>
public class ProposalEntity
{
    public Guid Id { get; set; }
    /// <summary><c>flow</c> or <c>architecture</c>.</summary>
    public string Kind { get; set; } = null!;
    public Guid WorkspaceId { get; set; }
    public Guid ProjectId { get; set; }
    /// <summary>Null for the main model, else the draft or variant the proposal was made on.</summary>
    public Guid? BranchId { get; set; }
    /// <summary>A flow: <c>draft</c>, <c>accepted</c> or <c>rejected</c>. Additions: <c>draft</c> while an item is pending, then <c>decided</c>.</summary>
    public string Status { get; set; } = null!;
    public string Name { get; set; } = null!;
    /// <summary>The person's request text.</summary>
    public string Request { get; set; } = null!;
    /// <summary>The proposal as JSON text, written and read by this feature only.</summary>
    public string Document { get; set; } = null!;
    public string CreatedBy { get; set; } = null!;
    public DateTimeOffset CreatedAt { get; set; }
    public string? DecidedBy { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
}

public class AiDesignDbContext(DbContextOptions<AiDesignDbContext> options) : DbContext(options)
{
    public DbSet<ProposalEntity> Proposals => Set<ProposalEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("ai");

        modelBuilder.Entity<ProposalEntity>(b =>
        {
            b.ToTable("design_proposals");
            b.HasKey(p => p.Id);
            b.Property(p => p.Id).ValueGeneratedNever();
            b.Property(p => p.Kind).HasMaxLength(20);
            b.Property(p => p.Status).HasMaxLength(20);
            b.Property(p => p.Name).HasMaxLength(200);
            b.Property(p => p.Request).HasMaxLength(4000);
            // Text, not jsonb: jsonb drops a duplicate key and rewrites a number, and a payload
            // must be accepted exactly as it was previewed.
            b.Property(p => p.Document).HasColumnType("text");
            b.Property(p => p.CreatedBy).HasMaxLength(100);
            b.Property(p => p.DecidedBy).HasMaxLength(100);
            b.HasIndex(p => new { p.ProjectId, p.Kind, p.CreatedAt, p.Id });
        });
    }
}

public class AiDesignDbContextFactory : IDesignTimeDbContextFactory<AiDesignDbContext>
{
    public AiDesignDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<AiDesignDbContext>()
            .UseNpgsql("Host=localhost;Database=vellum;Username=vellum;Password=vellum")
            .UseSnakeCaseNamingConvention()
            .Options;

        return new AiDesignDbContext(options);
    }
}
