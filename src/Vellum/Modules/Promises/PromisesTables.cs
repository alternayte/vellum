using Microsoft.EntityFrameworkCore;
using Vellum.Shared;

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
    public StoredPromiseLifecycle Lifecycle { get; set; }
    public StoredPromiseOrigin Origin { get; set; }
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
    public StoredRunResult Result { get; set; }
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

/// <summary>
/// The gate policy of the impact report of a project (<see cref="ImpactGatePolicy"/>). No row
/// means <c>off</c>. Only an owner changes it, and the audit log records each change.
/// </summary>
public class ImpactGateEntity
{
    public Guid ProjectId { get; set; }
    public ImpactGatePolicy Policy { get; set; }
    /// <summary>The user who set the policy.</summary>
    public string ChangedBy { get; set; } = null!;
    public DateTimeOffset ChangedAt { get; set; }
}

/// <summary>The lifecycle of a promise as its row stores it: <see cref="PromiseLifecycle"/>, the text of the state and the API.</summary>
public enum StoredPromiseLifecycle
{
    Draft,
    Approved,
    Retired,
}

/// <summary>Who first wrote a promise, as its row stores it: <see cref="PromiseOrigin"/>, the text of the events and the API.</summary>
public enum StoredPromiseOrigin
{
    Human,
    AiSuggested,
    AgentSuggested,
}

/// <summary>The result of a run as its row stores it: <see cref="RunResult"/>, the text of the events and the API.</summary>
public enum StoredRunResult
{
    Passed,
    Failed,
    NotRun,
    Unsupported,
}

/// <summary>The tables of the module, in the Postgres schema <c>promises</c>.</summary>
public static class PromisesTables
{
    public const string Schema = "promises";

    public static readonly IReadOnlyList<PostgresEnum> Enums =
    [
        PostgresEnum.Of<StoredPromiseLifecycle>("promise_lifecycle", Schema),
        PostgresEnum.Of<StoredPromiseOrigin>("promise_origin", Schema),
        PostgresEnum.Of<StoredRunResult>("run_result", Schema),
        PostgresEnum.Of<ImpactGatePolicy>("impact_gate_policy", Schema),
    ];

    public static void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<PromiseEntity>(b =>
        {
            b.ToTable("promises", Schema);
            b.HasKey(p => p.Id);
            b.Property(p => p.Document).HasColumnType("jsonb");
            b.Property(p => p.LatestRuns).HasColumnType("jsonb");
            b.HasIndex(p => new { p.ProjectId, p.Number }).IsUnique();
        });

        modelBuilder.Entity<RunEntity>(b =>
        {
            b.ToTable("runs", Schema);
            // Named here: stress.runs has a key of the same name in its own schema.
            b.HasKey(r => r.Id).HasName("pk_runs");
            b.Property(r => r.Document).HasColumnType("jsonb");
            b.HasIndex(r => new { r.PromiseId, r.Version });
        });

        modelBuilder.Entity<NumberingEntity>(b =>
        {
            b.ToTable("numbering", Schema);
            b.HasKey(n => n.ProjectId);
        });

        modelBuilder.Entity<ImpactGateEntity>(b =>
        {
            b.ToTable("impact_gates", Schema);
            b.HasKey(g => g.ProjectId);
        });
    }
}

