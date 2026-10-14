using Microsoft.EntityFrameworkCore;
using Vellum.Modules.Review.Decisions;

namespace Vellum.Modules.Review;

/// <summary>
/// One decision as lists and reads need it. <see cref="Document"/> is the decision state as
/// the events give it; the row is rebuilt from the stream and holds nothing the stream does not.
/// </summary>
public class DecisionEntity
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public int Number { get; set; }
    public string Title { get; set; } = null!;
    /// <summary>The stored status. <c>superseded</c> is derived on read and is never here.</summary>
    public string Status { get; set; } = null!;
    public DateTimeOffset UpdatedAt { get; set; }
    /// <summary>A JSON <see cref="DecisionState"/>.</summary>
    public string Document { get; set; } = null!;
    /// <summary>The version of the decision stream that this row is the fold of.</summary>
    public long Version { get; set; }
}

/// <summary>
/// The next decision number of a project. This row is not a projection: it is the allocator.
/// A number is taken under the row's lock in the transaction that creates the decision, and
/// the counter never goes back, so a number is never issued twice.
/// </summary>
public class DecisionNumberingEntity
{
    public Guid ProjectId { get; set; }
    public int NextNumber { get; set; }
}

/// <summary>
/// A person's answer to a finding that no decision settles: they will not change the model.
/// It names the rule and the subject, and it records what the finding was when they answered.
/// </summary>
public class DispositionEntity
{
    public Guid ProjectId { get; set; }
    public string RuleId { get; set; } = null!;
    public Guid SubjectId { get; set; }
    public int RuleVersion { get; set; }
    public string Fingerprint { get; set; } = null!;
    /// <summary><c>wont-fix</c>.</summary>
    public string Status { get; set; } = null!;
    public string Reason { get; set; } = null!;
    public string DecidedBy { get; set; } = null!;
    public DateTimeOffset DecidedAt { get; set; }
}

/// <summary>The findings of one pinned state, as a person saved them. A row is added and never changed.</summary>
public class SnapshotEntity
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    /// <summary>The model stream the review was made on.</summary>
    public Guid Branch { get; set; }
    public long ModelVersion { get; set; }
    public Guid? RevisionId { get; set; }
    public string InputsFingerprint { get; set; } = null!;
    public string RuleSetVersion { get; set; } = null!;
    public string Hash { get; set; } = null!;
    public string? Note { get; set; }
    /// <summary>A JSON list of the findings with their dispositions at that time.</summary>
    public string Findings { get; set; } = null!;
    public string CreatedBy { get; set; } = null!;
    public DateTimeOffset CreatedAt { get; set; }
}

public class QuestionEntity
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public string Text { get; set; } = null!;
    /// <summary>A JSON list of the IDs the question is about.</summary>
    public string About { get; set; } = "[]";
    public string Origin { get; set; } = null!;
    /// <summary><c>open</c>, <c>answered</c> or <c>dropped</c>.</summary>
    public string Status { get; set; } = null!;
    public string? Answer { get; set; }
    public string? AnsweredBy { get; set; }
    public DateTimeOffset? AnsweredAt { get; set; }
    public string CreatedBy { get; set; } = null!;
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// One answer of the assistant to "review this design", after the server checked it. It is a
/// set of suggestions that a person reads. Nothing in it is a finding or a decision.
/// </summary>
public class CritiqueEntity
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid ProjectId { get; set; }
    /// <summary>Null for the main model, else the draft or variant the critique was made on.</summary>
    public Guid? BranchId { get; set; }
    /// <summary>The person's question, or empty.</summary>
    public string Question { get; set; } = "";
    /// <summary>The critique as JSON text, written and read by this feature only.</summary>
    public string Document { get; set; } = null!;
    public string CreatedBy { get; set; } = null!;
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>The tables of the module, in the Postgres schema <c>review</c>.</summary>
public static class ReviewTables
{
    public const string Schema = "review";

    public static void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<DecisionEntity>(b =>
        {
            b.ToTable("decisions", Schema);
            b.HasKey(d => d.Id);
            b.Property(d => d.Id).ValueGeneratedNever();
            b.Property(d => d.Document).HasColumnType("jsonb");
            b.HasIndex(d => new { d.ProjectId, d.Number }).IsUnique();
        });

        modelBuilder.Entity<DecisionNumberingEntity>(b =>
        {
            b.ToTable("decision_numbering", Schema);
            b.HasKey(n => n.ProjectId);
        });

        modelBuilder.Entity<DispositionEntity>(b =>
        {
            b.ToTable("dispositions", Schema);
            b.HasKey(d => new { d.ProjectId, d.RuleId, d.SubjectId });
            b.Property(d => d.RuleId).HasMaxLength(20);
            b.Property(d => d.Status).HasMaxLength(30);
        });

        modelBuilder.Entity<SnapshotEntity>(b =>
        {
            b.ToTable("snapshots", Schema);
            // Named here: import.snapshots has a key of the same name in its own schema.
            b.HasKey(s => s.Id).HasName("pk_snapshots");
            b.Property(s => s.Id).ValueGeneratedNever();
            b.Property(s => s.Findings).HasColumnType("jsonb");
            b.HasIndex(s => new { s.ProjectId, s.CreatedAt, s.Id });
        });

        modelBuilder.Entity<QuestionEntity>(b =>
        {
            b.ToTable("questions", Schema);
            b.HasKey(q => q.Id);
            b.Property(q => q.Id).ValueGeneratedNever();
            b.Property(q => q.About).HasColumnType("jsonb");
            b.Property(q => q.Origin).HasMaxLength(30);
            b.Property(q => q.Status).HasMaxLength(20);
            b.HasIndex(q => new { q.ProjectId, q.CreatedAt, q.Id });
        });

        modelBuilder.Entity<CritiqueEntity>(b =>
        {
            b.ToTable("critiques", Schema);
            b.HasKey(c => c.Id);
            b.Property(c => c.Id).ValueGeneratedNever();
            // Text, as the design proposals: the document is read back exactly as it was written.
            b.Property(c => c.Document).HasColumnType("text");
            b.HasIndex(c => new { c.ProjectId, c.CreatedAt, c.Id });
        });
    }
}

