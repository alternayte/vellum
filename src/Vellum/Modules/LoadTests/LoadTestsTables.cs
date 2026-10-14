using Microsoft.EntityFrameworkCore;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure;
using Vellum.Shared;

namespace Vellum.Modules.LoadTests;

/// <summary>
/// One load-test target. <see cref="Document"/> is its <see cref="TargetState"/>;
/// <see cref="Version"/> counts the changes that made it.
/// </summary>
public class TargetEntity
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public string Name { get; set; } = null!;
    public long Version { get; set; }
    /// <summary>A removed target keeps its row; no read finds it.</summary>
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
    public StoredTargetEnvironment Environment { get; set; }
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
    public StoredTargetEnvironment Environment { get; set; }
    /// <summary><c>vellum-k6-result/1</c> or <c>k6-summary-export</c>.</summary>
    public string Format { get; set; } = null!;
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset EndedAt { get; set; }
    /// <summary>
    /// The length of the run in milliseconds, as the file states it. With the script and the
    /// start it names one run: the Vellum result and the summary export of one run have the
    /// same three. Null in a row from before the column, when another row has the same three.
    /// </summary>
    public double? ElapsedMs { get; set; }
    /// <summary>SHA-256 of the imported bytes. The same file is imported once.</summary>
    public string ContentHash { get; set; } = null!;
    /// <summary>A JSON <see cref="MeasuredRun"/>.</summary>
    public string Document { get; set; } = null!;
    public string ImportedBy { get; set; } = null!;
    /// <summary>The agent token that sent the file, or null when a person did.</summary>
    public string? ImportedVia { get; set; }
    public DateTimeOffset ImportedAt { get; set; }
}

/// <summary>
/// The class of a target as the script and result rows store it. The target itself, its
/// events and the API name the class as text (<see cref="TargetEnvironment"/>).
/// </summary>
public enum StoredTargetEnvironment
{
    Development,
    Test,
    Staging,
    Production,
}

/// <summary>The tables of the module, in the Postgres schema <c>loadtests</c>.</summary>
public static class LoadTestsTables
{
    public const string Schema = "loadtests";

    public static void MapEnums(NpgsqlDbContextOptionsBuilder npgsql)
    {
        npgsql.MapEnum<StoredTargetEnvironment>("target_environment", Schema, AppDatabase.EnumLabels);
    }

    public static void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<TargetEntity>(b =>
        {
            b.ToTable("targets", Schema);
            b.HasKey(t => t.Id);
            b.Property(t => t.Document).HasColumnType("jsonb");
            b.HasIndex(t => new { t.ProjectId, t.Name });
        });

        modelBuilder.Entity<ScriptEntity>(b =>
        {
            b.ToTable("scripts", Schema);
            b.HasKey(s => s.Id);
            // The same inputs give the same hash, and a project keeps one row for it.
            b.HasIndex(s => new { s.ProjectId, s.Hash }).IsUnique();
            b.HasIndex(s => new { s.ProjectId, s.CreatedAt });
        });

        modelBuilder.Entity<ResultEntity>(b =>
        {
            b.ToTable("results", Schema);
            b.HasKey(r => r.Id);
            b.Property(r => r.Document).HasColumnType("jsonb");
            b.HasIndex(r => new { r.ProjectId, r.ContentHash }).IsUnique();
            // One run is imported once, in either format.
            b.HasIndex(r => new { r.ScriptId, r.StartedAt, r.ElapsedMs }).IsUnique();
            b.HasIndex(r => new { r.ProjectId, r.ScenarioId, r.TargetId, r.StartedAt });
            // A script that has a result stays: the result is read through it.
            b.HasOne<ScriptEntity>().WithMany().HasForeignKey(r => r.ScriptId).OnDelete(DeleteBehavior.Restrict);
        });
    }
}
