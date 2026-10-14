using Microsoft.EntityFrameworkCore;

namespace Vellum.Modules.Ai.Usage;

/// <summary>
/// One row per call to a provider. It holds no prompt text and no response text.
/// </summary>
public class UsageRecordEntity
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid ProviderId { get; set; }
    public string ProviderKind { get; set; } = null!;
    /// <summary>The model name from settings.</summary>
    public string Model { get; set; } = null!;
    /// <summary>The model id or version the provider reported.</summary>
    public string? ReportedModel { get; set; }
    public string Task { get; set; } = null!;
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
    public string Outcome { get; set; } = null!;
    /// <summary>
    /// Null, or why the cost and the counts are not the provider's own: <c>cost_estimated</c>
    /// (a count was missing), <c>usage_out_of_range</c> (a count was negative or absurd and
    /// was dropped), <c>reaped</c> (the row was abandoned) or <c>schema_rejected_fallback</c>
    /// (the provider rejected its structured-output mode, and this call sent the schema in the prompt).
    /// </summary>
    public string? UsageNote { get; set; }
    public string? ErrorCode { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public string? ActorId { get; set; }
}

/// <summary>What a model costs from a date on. The instance admin writes these; none is built in.</summary>
public class ModelPriceEntity
{
    public Guid Id { get; set; }
    public string ProviderKind { get; set; } = null!;
    public string Model { get; set; } = null!;
    public decimal InputUsdPerMillionTokens { get; set; }
    public decimal OutputUsdPerMillionTokens { get; set; }
    public DateTimeOffset EffectiveFrom { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string CreatedBy { get; set; } = null!;
}

/// <summary>The tables of the module, in the Postgres schema <c>ai</c>.</summary>
public static class AiUsageTables
{
    public const string Schema = "ai";

    public static void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<UsageRecordEntity>(b =>
        {
            b.ToTable("usage_records", Schema);
            b.HasKey(r => r.Id);
            b.Property(r => r.Id).ValueGeneratedNever();
            b.Property(r => r.ProviderKind).HasMaxLength(40);
            b.Property(r => r.Model).HasMaxLength(200);
            b.Property(r => r.ReportedModel).HasMaxLength(200);
            b.Property(r => r.Task).HasMaxLength(40);
            b.Property(r => r.Purpose).HasMaxLength(100);
            b.Property(r => r.PromptTemplateVersion).HasMaxLength(100);
            b.Property(r => r.CostUsd).HasPrecision(18, 8);
            b.Property(r => r.ReservedUsd).HasPrecision(18, 8);
            b.Property(r => r.Outcome).HasMaxLength(40);
            b.Property(r => r.ErrorCode).HasMaxLength(60);
            b.Property(r => r.UsageNote).HasMaxLength(40);
            b.HasIndex(r => new { r.WorkspaceId, r.StartedAt });
            b.HasIndex(r => new { r.WorkspaceId, r.ProviderId, r.StartedAt });
        });

        modelBuilder.Entity<ModelPriceEntity>(b =>
        {
            b.ToTable("model_prices", Schema);
            b.HasKey(p => p.Id);
            b.Property(p => p.Id).ValueGeneratedNever();
            b.Property(p => p.ProviderKind).HasMaxLength(40);
            b.Property(p => p.Model).HasMaxLength(200);
            b.Property(p => p.InputUsdPerMillionTokens).HasPrecision(18, 8);
            b.Property(p => p.OutputUsdPerMillionTokens).HasPrecision(18, 8);
            b.Property(p => p.CreatedAt).HasDefaultValueSql("now()");
            b.HasIndex(p => new { p.ProviderKind, p.Model, p.EffectiveFrom }).IsUnique();
        });
    }
}

