using Deedbox;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Vellum.Modules.LoadTests;

/// <summary>
/// The read model of one load-test target. The target is the stream with this ID;
/// <see cref="Document"/> is its <see cref="TargetState"/> after <see cref="Version"/> events.
/// </summary>
public class TargetEntity
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public string Name { get; set; } = null!;
    public long Version { get; set; }
    /// <summary>A removed target keeps its row, so the delete of its project finds its stream.</summary>
    public bool Removed { get; set; }
    /// <summary>A JSON <see cref="TargetState"/>.</summary>
    public string Document { get; set; } = null!;
}

/// <summary>
/// One k6 script that Vellum generated. Rows are added and never changed. A measured result
/// is accepted only when it names the <see cref="Hash"/> of a row of the same project, so the
/// row is what binds a result to its scenario, model revision and target.
/// </summary>
public class ScriptEntity
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    /// <summary>SHA-256 of the script text with the hash constant set to zeros, in lower-case hex.</summary>
    public string Hash { get; set; } = null!;
    public Guid ScenarioId { get; set; }
    public long ScenarioVersion { get; set; }
    public Guid ModelStreamId { get; set; }
    public long ModelVersion { get; set; }
    public Guid ProfileId { get; set; }
    public int ProfileVersion { get; set; }
    public Guid TargetId { get; set; }
    /// <summary>The target as it was when the script was generated.</summary>
    public string TargetName { get; set; } = null!;
    public string BaseUrl { get; set; } = null!;
    public string Environment { get; set; } = null!;
    public string AuthorisedBy { get; set; } = null!;
    public DateTimeOffset AuthorisedAt { get; set; }
    /// <summary>The text of the script, as the person downloads it.</summary>
    public string Content { get; set; } = null!;
    /// <summary>A JSON <see cref="K6Plan"/>: what the script drives and what it leaves out.</summary>
    public string Plan { get; set; } = null!;
    public string CreatedBy { get; set; } = null!;
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// One imported result of a k6 run. Rows are added and never changed. Every number in
/// <see cref="Document"/> came from the imported file; none comes from the simulator.
/// </summary>
public class ResultEntity
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public Guid ScriptId { get; set; }
    public string ScriptHash { get; set; } = null!;
    public Guid ScenarioId { get; set; }
    public long ScenarioVersion { get; set; }
    public Guid ModelStreamId { get; set; }
    public long ModelVersion { get; set; }
    public Guid TargetId { get; set; }
    public string Environment { get; set; } = null!;
    /// <summary><c>vellum-k6-result/1</c> or <c>k6-summary-export</c>.</summary>
    public string Format { get; set; } = null!;
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset EndedAt { get; set; }
    /// <summary>SHA-256 of the imported bytes. The same file is imported once.</summary>
    public string ContentHash { get; set; } = null!;
    /// <summary>A JSON <see cref="MeasuredRun"/>.</summary>
    public string Document { get; set; } = null!;
    public string ImportedBy { get; set; } = null!;
    /// <summary>The agent token that sent the file, or null when a person did.</summary>
    public string? ImportedVia { get; set; }
    public DateTimeOffset ImportedAt { get; set; }
}

public class LoadTestsDbContext(DbContextOptions<LoadTestsDbContext> options) : DbContext(options)
{
    public DbSet<TargetEntity> Targets => Set<TargetEntity>();
    public DbSet<ScriptEntity> Scripts => Set<ScriptEntity>();
    public DbSet<ResultEntity> Results => Set<ResultEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("loadtests");

        modelBuilder.Entity<TargetEntity>(b =>
        {
            b.ToTable("targets");
            b.HasKey(t => t.Id);
            b.Property(t => t.Document).HasColumnType("jsonb");
            b.HasIndex(t => new { t.ProjectId, t.Name });
        });

        modelBuilder.Entity<ScriptEntity>(b =>
        {
            b.ToTable("scripts");
            b.HasKey(s => s.Id);
            // The same inputs give the same hash, and a project keeps one row for it.
            b.HasIndex(s => new { s.ProjectId, s.Hash }).IsUnique();
            b.HasIndex(s => new { s.ProjectId, s.CreatedAt });
        });

        modelBuilder.Entity<ResultEntity>(b =>
        {
            b.ToTable("results");
            b.HasKey(r => r.Id);
            b.Property(r => r.Document).HasColumnType("jsonb");
            b.HasIndex(r => new { r.ProjectId, r.ContentHash }).IsUnique();
            b.HasIndex(r => new { r.ProjectId, r.ScenarioId, r.TargetId, r.StartedAt });
            // A script that has a result stays: the result is read through it.
            b.HasOne<ScriptEntity>().WithMany().HasForeignKey(r => r.ScriptId).OnDelete(DeleteBehavior.Restrict);
        });
    }
}

public class LoadTestsDbContextFactory : IDesignTimeDbContextFactory<LoadTestsDbContext>
{
    public LoadTestsDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<LoadTestsDbContext>()
            .UseNpgsql("Host=localhost")
            .UseSnakeCaseNamingConvention()
            .Options;
        return new LoadTestsDbContext(options);
    }
}

/// <summary>Keeps the target read model. It runs inline, so a row commits with its event.</summary>
public sealed class TargetProjection : Projection<LoadTestsDbContext>
{
    public TargetProjection()
    {
        On<TargetEvent.TargetAdded>(async (added, ctx) =>
        {
            var id = Guid.Parse(ctx.StreamId);
            var row = await ctx.Db.Targets.FindAsync([id], ctx.CancellationToken);
            if (row is null)
            {
                row = new TargetEntity { Id = id, ProjectId = added.ProjectId };
                ctx.Db.Targets.Add(row);
            }
            Write(row, TargetState.Evolve(TargetState.Initial, added), ctx.Version);
        });

        Fold<TargetEvent.TargetChanged>();
        Fold<TargetEvent.TargetAuthorised>();
        Fold<TargetEvent.AuthorisationVoided>();
        Fold<TargetEvent.OverrideSet>();
        Fold<TargetEvent.OverrideCleared>();
        Fold<TargetEvent.TargetRemoved>();

        // The target streams of a deleted project are deleted with it. The tombstone of any
        // stream comes here; an ID that is no target matches no row.
        On<StreamDeleted>(async (_, ctx) => await RemoveAsync(ctx));
    }

    private void Fold<TEvent>() where TEvent : class =>
        On<TEvent>(async (@event, ctx) =>
        {
            if (await ctx.Db.Targets.FindAsync([Guid.Parse(ctx.StreamId)], ctx.CancellationToken) is { } row)
                Write(row, TargetState.Evolve(TargetJson.Read(row.Document), @event), ctx.Version);
        });

    private static async Task RemoveAsync(ProjectionContext<LoadTestsDbContext> ctx)
    {
        if (Guid.TryParse(ctx.StreamId, out var id) && await ctx.Db.Targets.FindAsync([id], ctx.CancellationToken) is { } row)
            ctx.Db.Targets.Remove(row);
    }

    private static void Write(TargetEntity row, TargetState state, long version)
    {
        row.Name = state.Name!;
        row.Version = version;
        row.Removed = state.Removed;
        row.Document = TargetJson.Write(state);
    }

    protected override async Task ResetAsync(WriteContext<LoadTestsDbContext> context) =>
        await context.Db.Targets.ExecuteDeleteAsync(context.CancellationToken);
}
