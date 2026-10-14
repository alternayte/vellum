using Microsoft.EntityFrameworkCore;
using Vellum.Shared;

namespace Vellum.Modules.Comments;

/// <summary>
/// The tables of the module, in the Postgres schema <c>comments</c>. Comments are
/// collaboration data: no row here is in a model stream, makes a revision or reaches an export.
/// </summary>
public static class CommentsTables
{
    public const string Schema = "comments";
    public const int MaxTargetName = 1_000;

    public static readonly IReadOnlyList<PostgresEnum> Enums =
    [
        PostgresEnum.Of<TargetKind>("target_kind", Schema),
        PostgresEnum.Of<ThreadStatus>("thread_status", Schema),
        PostgresEnum.Of<InboxReason>("inbox_reason", Schema),
        PostgresEnum.Of<DeliveryStatus>("delivery_status", Schema),
    ];

    public static void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ThreadEntity>(b =>
        {
            b.ToTable("threads", Schema);
            b.HasKey(t => t.Id);
            b.HasIndex(t => new { t.ProjectId, t.DraftId, t.TargetId });
            // A relationship without a label is named by both ends: two names and an arrow.
            b.Property(t => t.TargetName).HasMaxLength(MaxTargetName);
            b.Property(t => t.StepId).HasMaxLength(TextRule.MaxIdentifier);
        });

        modelBuilder.Entity<CommentEntity>(b =>
        {
            b.ToTable("comments", Schema);
            b.HasKey(c => c.Id);
            b.HasIndex(c => new { c.ThreadId, c.CreatedAt });
            b.HasOne<ThreadEntity>()
                .WithMany()
                .HasForeignKey(c => c.ThreadId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<InboxItemEntity>(b =>
        {
            b.ToTable("inbox_items", Schema);
            b.HasKey(i => i.Id);
            b.HasIndex(i => new { i.UserId, i.CreatedAt });
            // One entry per person and comment: a mention that is also a reply is one entry.
            b.HasIndex(i => new { i.UserId, i.CommentId }).IsUnique();
            b.HasOne<CommentEntity>()
                .WithMany()
                .HasForeignKey(i => i.CommentId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<DeliveryEntity>(b =>
        {
            b.ToTable("webhook_deliveries", Schema);
            b.HasKey(d => d.Id);
            b.HasIndex(d => new { d.Status, d.NextAttemptAt });
            b.HasIndex(d => d.ProjectId);
            b.Property(d => d.Payload).HasColumnType("jsonb");
        });
    }
}

/// <summary>
/// A discussion about one target of a project, on main or on one draft. The target is named by
/// its stable ID and never by a canvas position.
/// </summary>
public class ThreadEntity
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid ProjectId { get; set; }
    /// <summary>The draft the thread belongs to, or null for main. A merge moves open threads to main.</summary>
    public Guid? DraftId { get; set; }
    public TargetKind TargetKind { get; set; }
    /// <summary>The stable ID of the target. For a project it is the project ID; for a flow step it is the scenario ID.</summary>
    public Guid TargetId { get; set; }
    /// <summary>The ID of the step inside the scenario, for a flow step only.</summary>
    public string? StepId { get; set; }
    /// <summary>
    /// The last name the target was known by. Written at creation, refreshed by every read that
    /// finds the target, and set from the stream history when a model target is gone.
    /// </summary>
    public string TargetName { get; set; } = null!;
    /// <summary>The version of <see cref="ModelStreamId"/> when the thread was created.</summary>
    public long ModelVersion { get; set; }
    /// <summary>
    /// The model stream that <see cref="ModelVersion"/> counts: main, or the draft the thread was
    /// made on. A merge moves the thread to main and keeps this, so the version keeps its meaning.
    /// </summary>
    public Guid ModelStreamId { get; set; }
    public ThreadStatus Status { get; set; } = ThreadStatus.Open;
    /// <summary>True while the target does not exist. A read sets it; the API then reports the status <c>detached</c>.</summary>
    public bool Detached { get; set; }
    public DateTimeOffset? DetachedAt { get; set; }
    public string CreatedBy { get; set; } = null!;
    public DateTimeOffset CreatedAt { get; set; }
    public string? ResolvedBy { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }
    /// <summary>The open question of the Review module that this thread was raised as.</summary>
    public Guid? RaisedQuestionId { get; set; }
    /// <summary>The proposed decision (ADR) of the Review module that this thread was raised as.</summary>
    public Guid? RaisedDecisionId { get; set; }
}

/// <summary>One comment of a thread. The first one opens the thread; the others are its replies, on one level.</summary>
public class CommentEntity
{
    public Guid Id { get; set; }
    public Guid ThreadId { get; set; }
    public string Author { get; set; } = null!;
    /// <summary>Empty once the comment is deleted: the row stays as a tombstone.</summary>
    public string Body { get; set; } = null!;
    /// <summary>The user IDs of the workspace members the comment mentions.</summary>
    public string[] Mentions { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; }
    /// <summary>When the author last changed the text. No earlier text is kept.</summary>
    public DateTimeOffset? EditedAt { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
    public string? DeletedBy { get; set; }
}

/// <summary>An entry of a person's inbox: a mention of them, or a reply on a thread they started or replied to.</summary>
public class InboxItemEntity
{
    public Guid Id { get; set; }
    public string UserId { get; set; } = null!;
    public Guid WorkspaceId { get; set; }
    public Guid ProjectId { get; set; }
    public Guid ThreadId { get; set; }
    public Guid CommentId { get; set; }
    public InboxReason Reason { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ReadAt { get; set; }
}

/// <summary>
/// One webhook event of the workspace, waiting to be sent or sent. The payload holds IDs,
/// names and a link; it never holds comment text or source text.
/// </summary>
public class DeliveryEntity
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid ProjectId { get; set; }
    /// <summary><c>comment.created</c>, <c>comment.resolved</c> or <c>comment.mentioned</c>.</summary>
    public string Event { get; set; } = null!;
    public string Payload { get; set; } = null!;
    public DeliveryStatus Status { get; set; } = DeliveryStatus.Pending;
    public int Attempts { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; }
    public DateTimeOffset? SentAt { get; set; }
    /// <summary>The HTTP status of the last answer, or a fixed word for a transport failure.</summary>
    public string? LastResult { get; set; }
}

/// <summary>What a thread is about. Every kind is named by a stable ID.</summary>
public enum TargetKind
{
    Project,
    Element,
    Relationship,
    Message,
    Operation,
    Channel,
    Step,
    Promise,
    Document,
    Decision,
}

/// <summary>The stored status. <c>detached</c> is reported when <see cref="ThreadEntity.Detached"/> is true.</summary>
public enum ThreadStatus
{
    Open,
    Resolved,
}

public enum InboxReason
{
    Mention,
    Reply,
}

public enum DeliveryStatus
{
    Pending,
    Delivered,
    Failed,
}
