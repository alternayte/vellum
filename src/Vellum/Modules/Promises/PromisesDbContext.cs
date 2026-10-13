using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Vellum.Modules.Promises;

/// <summary>
/// One promise as lists and reads need it. <see cref="Document"/> is the promise state as the
/// events give it; the row is rebuilt from the stream and holds nothing the stream does not.
/// </summary>
public class PromiseEntity
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public string RequirementId { get; set; } = null!;
    public int Number { get; set; }
    public string Title { get; set; } = null!;
    public string Lifecycle { get; set; } = null!;
    public string Origin { get; set; } = null!;
    public DateTimeOffset UpdatedAt { get; set; }
    /// <summary>A JSON <see cref="PromiseState"/>.</summary>
    public string Document { get; set; } = null!;
    /// <summary>A JSON object: the newest run of each obligation of each revision, by <c>revision:obligation ID</c>.</summary>
    public string LatestRuns { get; set; } = "{}";
    /// <summary>
    /// The version of the promise stream that this row is the fold of. 0 in a row written
    /// before the column existed: a reader that needs the version then folds the stream.
    /// </summary>
    public long Version { get; set; }
}

/// <summary>One verification run. Rows are added and never changed.</summary>
public class RunEntity
{
    public Guid Id { get; set; }
    public Guid PromiseId { get; set; }
    public Guid ProjectId { get; set; }
    /// <summary>The version of the promise stream at which the run was recorded: the order of the runs of a promise.</summary>
    public long Version { get; set; }
    public string ObligationId { get; set; } = null!;
    public int PromiseRevision { get; set; }
    public string Result { get; set; } = null!;
    public DateTimeOffset At { get; set; }
    /// <summary>A JSON <see cref="VerificationRun"/>.</summary>
    public string Document { get; set; } = null!;
}

/// <summary>
/// The requirement ID prefix of a project and the next number. This row is not a projection:
/// it is the allocator. A number is taken under the row's lock in the transaction that
/// creates the promise, and the counter never goes back, so a number is never issued twice.
/// </summary>
public class NumberingEntity
{
    public Guid ProjectId { get; set; }
    public string Prefix { get; set; } = null!;
    public int NextNumber { get; set; }
}

public class PromisesDbContext(DbContextOptions<PromisesDbContext> options) : DbContext(options)
{
    public DbSet<PromiseEntity> Promises => Set<PromiseEntity>();
    public DbSet<RunEntity> Runs => Set<RunEntity>();
    public DbSet<NumberingEntity> Numbering => Set<NumberingEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("promises");

        modelBuilder.Entity<PromiseEntity>(b =>
        {
            b.ToTable("promises");
            b.HasKey(p => p.Id);
            b.Property(p => p.Document).HasColumnType("jsonb");
            b.Property(p => p.LatestRuns).HasColumnType("jsonb");
            b.HasIndex(p => new { p.ProjectId, p.Number }).IsUnique();
        });

        modelBuilder.Entity<RunEntity>(b =>
        {
            b.ToTable("runs");
            b.HasKey(r => r.Id);
            b.Property(r => r.Document).HasColumnType("jsonb");
            b.HasIndex(r => new { r.PromiseId, r.Version });
        });

        modelBuilder.Entity<NumberingEntity>(b =>
        {
            b.ToTable("numbering");
            b.HasKey(n => n.ProjectId);
        });
    }
}

public class PromisesDbContextFactory : IDesignTimeDbContextFactory<PromisesDbContext>
{
    public PromisesDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<PromisesDbContext>()
            .UseNpgsql("Host=localhost")
            .UseSnakeCaseNamingConvention()
            .Options;
        return new PromisesDbContext(options);
    }
}
