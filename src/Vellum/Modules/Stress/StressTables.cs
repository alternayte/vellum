using Microsoft.EntityFrameworkCore;

namespace Vellum.Modules.Stress;

/// <summary>
/// One stress profile of a project. <see cref="Version"/> goes up by one with every save, so
/// a stored run names the exact inputs it used.
/// </summary>
public class StressProfileEntity
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public Guid ScenarioId { get; set; }
    public string Name { get; set; } = null!;
    public int Version { get; set; }
    /// <summary>A JSON <see cref="Engine.StressProfile"/>.</summary>
    public string Document { get; set; } = null!;
    public DateTimeOffset UpdatedAt { get; set; }
    public string UpdatedBy { get; set; } = null!;
}

/// <summary>
/// One stored run. Rows are added and never changed. The documents are text, not
/// <c>jsonb</c>: the result is kept byte for byte as the engine wrote it.
/// </summary>
public class StressRunEntity
{
    public Guid Id { get; set; }
    public Guid ProfileId { get; set; }
    public Guid ProjectId { get; set; }
    public int ProfileVersion { get; set; }
    /// <summary>The model stream the run was pinned to: the main stream, a draft or a variant.</summary>
    public Guid ModelStreamId { get; set; }
    public long ModelVersion { get; set; }
    public Guid ScenarioId { get; set; }
    public long ScenarioVersion { get; set; }
    public string Seed { get; set; } = null!;
    public string EngineVersion { get; set; } = null!;
    public string Hash { get; set; } = null!;
    public DateTimeOffset At { get; set; }
    public string RanBy { get; set; } = null!;
    /// <summary>A JSON <see cref="StressRunSummary"/>.</summary>
    public string Summary { get; set; } = null!;
    /// <summary>The profile as it was when the run started.</summary>
    public string Profile { get; set; } = null!;
    /// <summary>A JSON <see cref="Engine.StressResult"/>.</summary>
    public string Result { get; set; } = null!;
}

/// <summary>The tables of the module, in the Postgres schema <c>stress</c>.</summary>
public static class StressTables
{
    public const string Schema = "stress";

    public static void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<StressProfileEntity>(b =>
        {
            b.ToTable("profiles", Schema);
            b.HasKey(p => p.Id);
            b.Property(p => p.Version).IsConcurrencyToken();
            b.HasIndex(p => new { p.ProjectId, p.Name });
        });

        modelBuilder.Entity<StressRunEntity>(b =>
        {
            b.ToTable("runs", Schema);
            // Named here: promises.runs has a key of the same name in its own schema.
            b.HasKey(r => r.Id).HasName("pk_runs");
            b.HasIndex(r => new { r.ProfileId, r.At });
            b.HasIndex(r => r.ProjectId);
            // The runs of a profile go with it.
            b.HasOne<StressProfileEntity>().WithMany().HasForeignKey(r => r.ProfileId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}

