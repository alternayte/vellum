using System.Diagnostics;
using Microsoft.EntityFrameworkCore;

namespace Vellum.Modules.Ai.Usage;

/// <param name="InputTokenBound">
/// An upper bound of the input tokens: the UTF-8 byte count of the request text. A token is
/// never shorter than one byte.
/// </param>
public sealed record CallStart(
    Guid WorkspaceId, Guid ProviderId, string ProviderKind, string Model, string Task, string Purpose,
    string PromptTemplateVersion, string? ActorId, decimal? ProviderBudgetUsd, decimal? WorkspaceBudgetUsd,
    int? RequestsPerMinute, long InputTokenBound, int MaxOutputTokens);

public sealed record CallTicket(Guid Id, decimal? InputUsdPerMillionTokens, decimal? OutputUsdPerMillionTokens, long StartedTimestamp);

public sealed record CallEnd(string Outcome, string? ErrorCode, int? InputTokens, int? OutputTokens, string? ReportedModel, bool Rejected);

/// <summary>
/// Meters provider calls and keeps budgets and rate limits. A call is admitted and its row
/// written under a lock on the workspace, so calls that start at the same time see each
/// other. A budget is kept with a reservation: the call's highest possible cost is counted
/// until its real cost is known. A budget on a model that has no price cannot be kept, so
/// the call is refused.
/// </summary>
public sealed class AiUsageLedger(AiUsageDbContext db)
{
    public const string Started = "started";

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

        var price = await db.ModelPrices.AsNoTracking()
            .Where(p => p.ProviderKind == call.ProviderKind && p.Model == call.Model && p.EffectiveFrom <= now)
            .OrderByDescending(p => p.EffectiveFrom)
            .FirstOrDefaultAsync(ct);
        decimal? reserve = price is null
            ? null
            : (call.InputTokenBound * price.InputUsdPerMillionTokens + call.MaxOutputTokens * price.OutputUsdPerMillionTokens) / 1_000_000m;

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

        return (new CallTicket(id, price?.InputUsdPerMillionTokens, price?.OutputUsdPerMillionTokens, Stopwatch.GetTimestamp()), null);
    }

    /// <summary>
    /// Writes the end of a call. It does not take the request's cancellation token: a
    /// cancelled call is recorded too.
    /// </summary>
    /// <returns>The cost, or null when it is unknown.</returns>
    public async Task<decimal?> CompleteAsync(CallTicket ticket, CallEnd end)
    {
        decimal? cost = end.Rejected
            ? 0m
            : ticket is { InputUsdPerMillionTokens: { } inputPrice, OutputUsdPerMillionTokens: { } outputPrice }
              && end is { InputTokens: { } input, OutputTokens: { } output }
                ? (input * inputPrice + output * outputPrice) / 1_000_000m
                : null;
        var duration = (int)Math.Min(int.MaxValue, Stopwatch.GetElapsedTime(ticket.StartedTimestamp).TotalMilliseconds);
        var reportedModel = end.ReportedModel is { Length: > 200 } longName ? longName[..200] : end.ReportedModel;

        await db.UsageRecords.Where(r => r.Id == ticket.Id).ExecuteUpdateAsync(set =>
        {
            set.SetProperty(r => r.Outcome, end.Outcome)
                .SetProperty(r => r.ErrorCode, end.ErrorCode)
                .SetProperty(r => r.InputTokens, end.InputTokens)
                .SetProperty(r => r.OutputTokens, end.OutputTokens)
                .SetProperty(r => r.ReportedModel, reportedModel)
                .SetProperty(r => r.CostUsd, cost)
                .SetProperty(r => r.DurationMs, duration);

            // While the cost is unknown the reservation stays, so the budget still counts the call.
            if (cost is not null)
                set.SetProperty(r => r.ReservedUsd, (decimal?)null);
        });
        return cost;
    }

    private static async Task<decimal> SpentAsync(IQueryable<UsageRecordEntity> rows, CancellationToken ct) =>
        await rows.SumAsync(r => r.CostUsd ?? r.ReservedUsd ?? 0m, ct);
}
