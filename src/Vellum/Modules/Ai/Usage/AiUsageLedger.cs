using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Vellum.Shared.Outbound;

namespace Vellum.Modules.Ai.Usage;

/// <param name="InputBytes">The UTF-8 byte count of the request text. The reservation estimates the input tokens from it.</param>
/// <param name="OutputTokenCeiling">The most output tokens the provider can bill for the call.</param>
public sealed record CallStart(
    Guid WorkspaceId, Guid ProviderId, string ProviderKind, string Model, string Task, string Purpose,
    string PromptTemplateVersion, string? ActorId, decimal? ProviderBudgetUsd, decimal? WorkspaceBudgetUsd,
    int? RequestsPerMinute, long InputBytes, int OutputTokenCeiling);

public sealed record CallTicket(
    Guid Id, decimal? InputUsdPerMillionTokens, decimal? OutputUsdPerMillionTokens, long StartedTimestamp,
    int EstimatedInputTokens, int OutputTokenCeiling);

/// <param name="MayHaveGenerated">
/// False when the provider refused the request before it generated anything; the call then
/// costs nothing. True when the provider answered with a success status or its stream
/// started: it may bill output, whatever happened afterwards.
/// </param>
/// <param name="Note">
/// A usage note that says how the call was made, such as <see cref="AiUsageLedger.NoteSchemaFallback"/>.
/// A note about the counts or the cost of the call takes its place.
/// </param>
public sealed record CallEnd(
    string Outcome, string? ErrorCode, int? InputTokens, int? OutputTokens, string? ReportedModel, bool MayHaveGenerated, string? Note = null);

/// <param name="CostUsd">Null only when the model has no price.</param>
/// <param name="InputTokens">The provider's count when it is within the sane range, else null.</param>
/// <param name="Estimated">The cost was computed from an estimate, because a count was missing or outside the sane range.</param>
public sealed record CallCost(decimal? CostUsd, int? InputTokens, int? OutputTokens, bool Estimated);

/// <summary>
/// Meters provider calls and keeps budgets and rate limits. A call is admitted and its row
/// written under a lock on the workspace, so calls that start at the same time see each
/// other. A budget on a model that has no price cannot be kept, so the call is refused.
/// <para>
/// <b>Reservation.</b> A budget is kept with a reservation: while a call runs, its highest
/// possible cost counts against the budget. The highest cost is the estimated input tokens
/// plus the output ceiling. The input estimate is the request's UTF-8 bytes divided by
/// <see cref="MinBytesPerToken"/>: the tokenizers of the supported vendors give about four
/// bytes per token for English text and about three for source code, so two is a bound with
/// room; text that is denser than that (some non-Latin scripts) makes the estimate low until
/// the real count replaces it.
/// </para>
/// <para>
/// <b>Release.</b> <see cref="CompleteAsync"/> ends the reservation on every outcome, and
/// <see cref="AiClient"/> calls it on every path out of a call. What takes its place:
/// the real cost, when the provider reported both token counts within the sane range;
/// zero, when the provider refused the request before it generated anything (no success
/// status arrived); and a conservative estimate, marked <c>cost_estimated</c>, when the
/// provider may have generated output and a count is missing or not sane. The estimate uses
/// each count that was reported and, for a count that was not, the reservation's own figure:
/// the input estimate, or the whole output ceiling.
/// </para>
/// <para>
/// <b>Abandoned calls.</b> A process that dies during a call leaves its row <c>started</c>.
/// No call runs longer than <see cref="MaxCallDuration"/>, so the next admission for the
/// workspace releases every older <c>started</c> row in the same transaction and marks it
/// <c>abandoned</c>. No background job is needed.
/// </para>
/// </summary>
public sealed class AiUsageLedger(AiUsageDbContext db)
{
    public const string Started = "started";
    public const string Abandoned = "abandoned";
    public const string NoteEstimated = "cost_estimated";
    public const string NoteOutOfRange = "usage_out_of_range";
    public const string NoteReaped = "reaped";
    /// <summary>The provider rejected the request in its structured-output mode, and this call sent the schema in the prompt.</summary>
    public const string NoteSchemaFallback = "schema_rejected_fallback";

    /// <summary>The fewest UTF-8 bytes that the input estimate assumes for one token.</summary>
    public const int MinBytesPerToken = 2;

    /// <summary>A token count above this, or below zero, is not a count. No supported model reads or writes this many tokens in one call.</summary>
    public const int MaxTokenCount = 100_000_000;

    /// <summary>
    /// Longer than any call can run: three attempts of the longest timeout (600 s each), the
    /// retry waits (20 s) and the longest stream (30 minutes) add up to 61 minutes.
    /// </summary>
    public static readonly TimeSpan MaxCallDuration = TimeSpan.FromHours(2);

    /// <returns>A ticket for the admitted call, or the reason it was refused. A refused call has no row.</returns>
    public async Task<(CallTicket? Ticket, AiFailure? Refused)> BeginAsync(CallStart call, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var lockKey = BitConverter.ToInt64(call.WorkspaceId.ToByteArray(), 0);
        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({lockKey})", ct);

        var now = DateTimeOffset.UtcNow;
        if (call.RequestsPerMinute is { } perMinute)
        {
            var windowStart = now.AddMinutes(-1);
            var recent = await db.UsageRecords.AsNoTracking()
                .Where(r => r.WorkspaceId == call.WorkspaceId && r.ProviderId == call.ProviderId && r.StartedAt > windowStart)
                .OrderBy(r => r.StartedAt)
                .Select(r => r.StartedAt)
                .ToListAsync(ct);
            if (recent.Count >= perMinute)
                return (null, new AiFailure.RateLimited(recent[recent.Count - perMinute].AddMinutes(1) - now));
        }

        // A row that is still "started" after the longest possible call belongs to a process
        // that died. Its reservation ends here, under the same lock as the budget check below.
        var abandonedBefore = now - MaxCallDuration;
        await db.UsageRecords
            .Where(r => r.WorkspaceId == call.WorkspaceId && r.Outcome == Started && r.StartedAt < abandonedBefore)
            .ExecuteUpdateAsync(set => set
                .SetProperty(r => r.Outcome, Abandoned)
                .SetProperty(r => r.ReservedUsd, (decimal?)null)
                .SetProperty(r => r.UsageNote, NoteReaped), ct);

        var price = await db.ModelPrices.AsNoTracking()
            .Where(p => p.ProviderKind == call.ProviderKind && p.Model == call.Model && p.EffectiveFrom <= now)
            .OrderByDescending(p => p.EffectiveFrom)
            .FirstOrDefaultAsync(ct);
        var estimatedInput = (int)Math.Min(MaxTokenCount, (Math.Max(0, call.InputBytes) + MinBytesPerToken - 1) / MinBytesPerToken);
        var ceiling = Math.Clamp(call.OutputTokenCeiling, 0, MaxTokenCount);
        decimal? reserve = price is null ? null : Cost(estimatedInput, ceiling, price.InputUsdPerMillionTokens, price.OutputUsdPerMillionTokens);

        if (call.ProviderBudgetUsd is not null || call.WorkspaceBudgetUsd is not null)
        {
            if (reserve is null)
                return (null, new AiFailure.BudgetExceeded(call.ProviderBudgetUsd is not null ? "provider" : "workspace", "price_unknown"));

            var monthStart = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero);
            var month = db.UsageRecords.AsNoTracking().Where(r => r.WorkspaceId == call.WorkspaceId && r.StartedAt >= monthStart);

            if (call.ProviderBudgetUsd is { } providerBudget
                && await SpentAsync(month.Where(r => r.ProviderId == call.ProviderId), ct) + reserve > providerBudget)
                return (null, new AiFailure.BudgetExceeded("provider", "limit_reached"));

            if (call.WorkspaceBudgetUsd is { } workspaceBudget && await SpentAsync(month, ct) + reserve > workspaceBudget)
                return (null, new AiFailure.BudgetExceeded("workspace", "limit_reached"));
        }

        var id = Guid.NewGuid();
        db.UsageRecords.Add(new UsageRecordEntity
        {
            Id = id,
            WorkspaceId = call.WorkspaceId,
            ProviderId = call.ProviderId,
            ProviderKind = call.ProviderKind,
            Model = call.Model,
            Task = call.Task,
            Purpose = call.Purpose,
            PromptTemplateVersion = call.PromptTemplateVersion,
            ReservedUsd = reserve,
            Outcome = Started,
            StartedAt = now,
            ActorId = call.ActorId,
        });
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        db.ChangeTracker.Clear();

        return (new CallTicket(id, price?.InputUsdPerMillionTokens, price?.OutputUsdPerMillionTokens, Stopwatch.GetTimestamp(), estimatedInput, ceiling), null);
    }

    /// <summary>
    /// Writes the end of a call and ends its reservation, whatever the outcome. It does not
    /// take the request's cancellation token: a cancelled call is recorded too.
    /// </summary>
    public async Task<CallCost> CompleteAsync(CallTicket ticket, CallEnd end)
    {
        var input = Sane(end.InputTokens);
        var output = Sane(end.OutputTokens);
        var outOfRange = input != end.InputTokens || output != end.OutputTokens;

        decimal? cost;
        var estimated = false;
        if (!end.MayHaveGenerated)
        {
            cost = 0m;
        }
        else if (ticket is { InputUsdPerMillionTokens: { } inputPrice, OutputUsdPerMillionTokens: { } outputPrice })
        {
            estimated = input is null || output is null;
            cost = Cost(input ?? ticket.EstimatedInputTokens, output ?? ticket.OutputTokenCeiling, inputPrice, outputPrice);
        }
        else
        {
            cost = null;
        }

        var note = outOfRange ? NoteOutOfRange : estimated ? NoteEstimated : end.Note;
        var duration = (int)Math.Min(int.MaxValue, Stopwatch.GetElapsedTime(ticket.StartedTimestamp).TotalMilliseconds);
        var reportedModel = end.ReportedModel is null ? null : OutboundText.Clean(end.ReportedModel, 200);

        await db.UsageRecords.Where(r => r.Id == ticket.Id).ExecuteUpdateAsync(set => set
            .SetProperty(r => r.Outcome, end.Outcome)
            .SetProperty(r => r.ErrorCode, end.ErrorCode)
            .SetProperty(r => r.InputTokens, input)
            .SetProperty(r => r.OutputTokens, output)
            .SetProperty(r => r.ReportedModel, reportedModel)
            .SetProperty(r => r.CostUsd, cost)
            .SetProperty(r => r.ReservedUsd, (decimal?)null)
            .SetProperty(r => r.UsageNote, note)
            .SetProperty(r => r.DurationMs, duration));
        return new CallCost(cost, input, output, estimated);
    }

    private static int? Sane(int? tokens) => tokens is >= 0 and <= MaxTokenCount ? tokens : null;

    // Counts are at most 10^8 and a price is at most 10^5, so the products stay far inside the range of decimal.
    private static decimal Cost(int inputTokens, int outputTokens, decimal inputPrice, decimal outputPrice) =>
        checked(inputTokens * inputPrice + outputTokens * outputPrice) / 1_000_000m;

    private static async Task<decimal> SpentAsync(IQueryable<UsageRecordEntity> rows, CancellationToken ct) =>
        await rows.SumAsync(r => r.CostUsd ?? r.ReservedUsd ?? 0m, ct);
}
