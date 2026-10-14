using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using NpgsqlTypes;
using Vellum.Shared;

namespace Vellum.Modules.Ai;

/// <summary>
/// One row per call to a provider. It holds no prompt text and no response text.
/// </summary>
public class UsageRecordEntity
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid ProviderId { get; set; }
    public AiProviderKind ProviderKind { get; set; }
    /// <summary>The model name from settings.</summary>
    public string Model { get; set; } = null!;
    /// <summary>The model id or version the provider reported.</summary>
    public string? ReportedModel { get; set; }
    public AiUsageTask Task { get; set; }
    public string Purpose { get; set; } = null!;
    public string PromptTemplateVersion { get; set; } = null!;
    /// <summary>Null when the provider did not report it, or reported a number that is not a sane count.</summary>
    public int? InputTokens { get; set; }
    public int? OutputTokens { get; set; }
    /// <summary>
    /// Never negative. Null when the model has no price. Zero when the provider refused the
    /// request before it generated anything. An estimate, with <see cref="UsageNote"/> set,
    /// when the provider may have generated output and a token count is missing or not sane.
    /// </summary>
    public decimal? CostUsd { get; set; }
    /// <summary>
    /// The most the call can cost, set when the model has a price. It counts against a
    /// budget only while the call runs: every end of a call clears it.
    /// </summary>
    public decimal? ReservedUsd { get; set; }
    public int? DurationMs { get; set; }
    /// <summary>
    /// <c>started</c>, then one of <c>succeeded</c>, <c>invalid_output</c>, <c>truncated</c>,
    /// <c>refused</c>, <c>provider_error</c>, <c>rate_limited</c>, <c>timed_out</c>,
    /// <c>cancelled</c>, <c>failed</c> (an exception inside Vellum), or <c>abandoned</c> (the
    /// process died during the call and a later admission released the row).
    /// </summary>
    public AiCallOutcome Outcome { get; set; }
    /// <summary>
    /// Null, or why the cost and the counts are not the provider's own: <c>cost_estimated</c>
    /// (a count was missing), <c>usage_out_of_range</c> (a count was negative or absurd and
    /// was dropped), <c>reaped</c> (the row was abandoned) or <c>schema_rejected_fallback</c>
    /// (the provider rejected its structured-output mode, and this call sent the schema in the prompt).
    /// </summary>
    public AiUsageNote? UsageNote { get; set; }
    public string? ErrorCode { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public string? ActorId { get; set; }
}

/// <summary>What a model costs from a date on. The instance admin writes these; none is built in.</summary>
public class ModelPriceEntity
{
    public Guid Id { get; set; }
    public AiProviderKind ProviderKind { get; set; }
    public string Model { get; set; } = null!;
    public decimal InputUsdPerMillionTokens { get; set; }
    public decimal OutputUsdPerMillionTokens { get; set; }
    public DateTimeOffset EffectiveFrom { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string CreatedBy { get; set; } = null!;
}

/// <summary>
/// One proposal: a flow (<c>flow</c>) or additions to the architecture (<c>architecture</c>).
/// The in-app assistant generates it, or a caller submits it (an agent token, over HTTP or
/// MCP). A proposal is a suggestion that a person reviews. It is not part of the model and
/// not a scenario; accepting it writes through the same decisions as a person's own edit.
/// </summary>
public class ProposalEntity
{
    public Guid Id { get; set; }
    /// <summary><c>flow</c> or <c>architecture</c>.</summary>
    public DesignProposalKind Kind { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid ProjectId { get; set; }
    /// <summary>Null for the main model, else the draft or variant the proposal was made on.</summary>
    public Guid? BranchId { get; set; }
    /// <summary>A flow: <c>draft</c>, <c>accepted</c> or <c>rejected</c>. Additions: <c>draft</c> while an item is pending, then <c>decided</c>.</summary>
    public DesignProposalStatus Status { get; set; }
    public string Name { get; set; } = null!;
    /// <summary>The person's request text.</summary>
    public string Request { get; set; } = null!;
    /// <summary>The proposal as JSON text, written and read by this feature only.</summary>
    public string Document { get; set; } = null!;
    public string CreatedBy { get; set; } = null!;
    /// <summary>
    /// Null for a proposal the in-app assistant generated. For a submitted proposal: who
    /// submitted it, <c>agent-token:&lt;id&gt;</c> or <c>user:&lt;id&gt;</c>, from the credential of the call.
    /// </summary>
    public string? Origin { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string? DecidedBy { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
}

/// <summary>
/// The kind of provider a call went to. The settings documents name a kind as text
/// (<see cref="Settings.AiProviderKinds"/>); <see cref="EnumText"/> gives that text.
/// </summary>
public enum AiProviderKind
{
    Anthropic,
    [JsonStringEnumMemberName("openai"), PgName("openai")]
    OpenAi,
    Gemini,
    [JsonStringEnumMemberName("openai-compatible"), PgName("openai_compatible")]
    OpenAiCompatible,
}

/// <summary>
/// What a call was for: an <see cref="AiTask"/>, or <see cref="Test"/> for the connection
/// test of a provider. The HTTP API writes it in camelCase (<see cref="AiUsageLedger.TaskText"/>).
/// </summary>
public enum AiUsageTask
{
    InferArchitecture,
    ProposeFlow,
    Critique,
    Explain,
    Test,
}

/// <summary>How a call ended. <see cref="Started"/> while it runs.</summary>
public enum AiCallOutcome
{
    Started,
    Succeeded,
    InvalidOutput,
    Truncated,
    Refused,
    ProviderError,
    RateLimited,
    TimedOut,
    Cancelled,
    /// <summary>An exception inside Vellum.</summary>
    Failed,
    /// <summary>The process died during the call, and a later admission released the row.</summary>
    Abandoned,
}

/// <summary>Why the cost and the counts of a call are not the provider's own, or how the call was made.</summary>
public enum AiUsageNote
{
    /// <summary>A count was missing.</summary>
    CostEstimated,
    /// <summary>A count was negative or absurd and was dropped.</summary>
    UsageOutOfRange,
    /// <summary>The row was abandoned.</summary>
    Reaped,
    /// <summary>The provider rejected its structured-output mode, and this call sent the schema in the prompt.</summary>
    SchemaRejectedFallback,
}

public enum DesignProposalKind
{
    Flow,
    Architecture,
}

/// <summary>A flow: draft, accepted or rejected. Additions: draft while an item is pending, then decided.</summary>
public enum DesignProposalStatus
{
    Draft,
    Accepted,
    Rejected,
    Decided,
}

/// <summary>The tables of the module, in the Postgres schema <c>ai</c>.</summary>
public static class AiTables
{
    public const string Schema = "ai";

    public static readonly IReadOnlyList<PostgresEnum> Enums =
    [
        PostgresEnum.Of<AiProviderKind>("ai_provider_kind", Schema),
        PostgresEnum.Of<AiUsageTask>("ai_usage_task", Schema),
        PostgresEnum.Of<AiCallOutcome>("ai_call_outcome", Schema),
        PostgresEnum.Of<AiUsageNote>("ai_usage_note", Schema),
        PostgresEnum.Of<DesignProposalKind>("design_proposal_kind", Schema),
        PostgresEnum.Of<DesignProposalStatus>("design_proposal_status", Schema),
    ];

    public static void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<UsageRecordEntity>(b =>
        {
            b.ToTable("usage_records", Schema);
            b.HasKey(r => r.Id);
            b.Property(r => r.Id).ValueGeneratedNever();
            b.Property(r => r.Model).HasMaxLength(200);
            b.Property(r => r.ReportedModel).HasMaxLength(200);
            b.Property(r => r.Purpose).HasMaxLength(100);
            b.Property(r => r.PromptTemplateVersion).HasMaxLength(100);
            b.Property(r => r.CostUsd).HasPrecision(18, 8);
            b.Property(r => r.ReservedUsd).HasPrecision(18, 8);
            b.Property(r => r.ErrorCode).HasMaxLength(60);
            b.HasIndex(r => new { r.WorkspaceId, r.StartedAt });
            b.HasIndex(r => new { r.WorkspaceId, r.ProviderId, r.StartedAt });
        });

        modelBuilder.Entity<ModelPriceEntity>(b =>
        {
            b.ToTable("model_prices", Schema);
            b.HasKey(p => p.Id);
            b.Property(p => p.Id).ValueGeneratedNever();
            b.Property(p => p.Model).HasMaxLength(200);
            b.Property(p => p.InputUsdPerMillionTokens).HasPrecision(18, 8);
            b.Property(p => p.OutputUsdPerMillionTokens).HasPrecision(18, 8);
            b.Property(p => p.CreatedAt).HasDefaultValueSql("now()");
            b.HasIndex(p => new { p.ProviderKind, p.Model, p.EffectiveFrom }).IsUnique();
        });

        modelBuilder.Entity<ProposalEntity>(b =>
        {
            b.ToTable("design_proposals", Schema);
            b.HasKey(p => p.Id);
            b.Property(p => p.Id).ValueGeneratedNever();
            b.Property(p => p.Name).HasMaxLength(200);
            b.Property(p => p.Request).HasMaxLength(4000);
            // Text, not jsonb: jsonb drops a duplicate key and rewrites a number, and a payload
            // must be accepted exactly as it was previewed.
            b.Property(p => p.Document).HasColumnType("text");
            b.Property(p => p.CreatedBy).HasMaxLength(100);
            b.Property(p => p.Origin).HasMaxLength(100);
            b.Property(p => p.DecidedBy).HasMaxLength(100);
            b.HasIndex(p => new { p.ProjectId, p.Kind, p.CreatedAt, p.Id });
        });
    }
}
