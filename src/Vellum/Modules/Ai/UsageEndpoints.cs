using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Vellum.Modules.Settings;
using Vellum.Modules.Workspaces;
using Vellum.Shared;

namespace Vellum.Modules.Ai;

/// <param name="InputTokens">The sum of the counts that providers reported. Null when no call reported one.</param>
/// <param name="CostUsd">The sum of the known costs. Null when no call has a known cost.</param>
/// <param name="CostKnown">True when every call of the row has a cost. False means the real cost is higher or unknown.</param>
/// <param name="CostEstimated">
/// True when the cost of at least one call is an estimate: the provider may have generated
/// output and did not report a sane token count, so the call is charged its highest possible cost.
/// </param>
public sealed record AiUsageTotal(
    Guid ProviderId, string ProviderKind, string Model, string Task,
    int Calls, long? InputTokens, long? OutputTokens, decimal? CostUsd, bool CostKnown, bool CostEstimated);

public sealed record AiUsageResponse(DateTimeOffset From, DateTimeOffset To, IReadOnlyList<AiUsageTotal> Totals);

public static class GetAiUsage
{
    /// <summary>One year, with room for a leap year.</summary>
    public static readonly TimeSpan MaxRange = TimeSpan.FromDays(366);

    public static async Task<IResult> Handle(
        Guid workspaceId,
        DateTimeOffset? from,
        DateTimeOffset? to,
        ClaimsPrincipal user,
        WorkspaceAuthorizationService auth,
        AppDbContext db,
        TimeProvider time,
        CancellationToken ct)
    {
        var userId = user.UserId();
        await auth.RequireRoleAsync(workspaceId, userId, WorkspaceRole.Viewer, ct);

        // Without a range: the current calendar month in UTC, which is the budget period.
        var now = time.GetUtcNow();
        var start = (from ?? new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero)).ToUniversalTime();
        var end = (to ?? now).ToUniversalTime();
        if (start >= end)
            return Results.BadRequest(new ErrorResponse("validation_error", "Validation failed",
                Errors: [new FieldError("from", "Must be earlier than 'to'.")]));
        if (end - start > MaxRange)
            return Results.BadRequest(new ErrorResponse("validation_error", "Validation failed",
                Errors: [new FieldError("to", $"The range from 'from' to 'to' must be at most {MaxRange.Days} days.")]));

        var groups = await db.UsageRecords.AsNoTracking()
            .Where(r => r.WorkspaceId == workspaceId && r.StartedAt >= start && r.StartedAt < end)
            .GroupBy(r => new { r.ProviderId, r.ProviderKind, r.Model, r.Task })
            .Select(g => new
            {
                g.Key,
                Calls = g.Count(),
                InputReported = g.Count(r => r.InputTokens != null),
                Input = g.Sum(r => (long)(r.InputTokens ?? 0)),
                OutputReported = g.Count(r => r.OutputTokens != null),
                Output = g.Sum(r => (long)(r.OutputTokens ?? 0)),
                CostKnownCalls = g.Count(r => r.CostUsd != null),
                CostEstimatedCalls = g.Count(r => r.CostUsd != null
                    && (r.UsageNote == AiUsageNote.CostEstimated || r.UsageNote == AiUsageNote.UsageOutOfRange)),
                Cost = g.Sum(r => r.CostUsd ?? 0m),
            })
            .ToListAsync(ct);

        var totals = groups
            .Select(g => (Group: g, Kind: EnumText.Of(g.Key.ProviderKind), Task: AiUsageLedger.TaskText(g.Key.Task)))
            .OrderBy(t => t.Kind, StringComparer.Ordinal).ThenBy(t => t.Group.Key.Model, StringComparer.Ordinal)
            .ThenBy(t => t.Task, StringComparer.Ordinal).ThenBy(t => t.Group.Key.ProviderId)
            .Select(t => new AiUsageTotal(
                t.Group.Key.ProviderId, t.Kind, t.Group.Key.Model, t.Task, t.Group.Calls,
                t.Group.InputReported > 0 ? t.Group.Input : null,
                t.Group.OutputReported > 0 ? t.Group.Output : null,
                t.Group.CostKnownCalls > 0 ? t.Group.Cost : null,
                t.Group.CostKnownCalls == t.Group.Calls,
                t.Group.CostEstimatedCalls > 0))
            .ToList();

        return Results.Ok(new AiUsageResponse(start, end, totals));
    }
}

public sealed record ModelPriceDto(
    string ProviderKind, string Model, decimal InputUsdPerMillionTokens, decimal OutputUsdPerMillionTokens, DateTimeOffset EffectiveFrom);

/// <param name="EffectiveFrom">Null means from now on.</param>
public sealed record SetModelPriceRequest(
    string? ProviderKind, string? Model, decimal? InputUsdPerMillionTokens, decimal? OutputUsdPerMillionTokens, DateTimeOffset? EffectiveFrom);

/// <summary>
/// The price table that turns token counts into an estimated cost. The instance admin keeps
/// it; Vellum ships no price. A call is costed with the latest price that is in effect when
/// the call starts.
/// </summary>
public static class ModelPrices
{
    private const decimal MaxPrice = 100_000m;
    private const int StoredDecimals = 8;
    private const decimal MinPrice = 0.00000001m;
    private const string PriceRule =
        "Must be from 0.00000001 to 100000. A price is stored with 8 decimals, and a price that is stored as 0 makes every call free for the budget.";

    /// <summary>
    /// The price as the column holds it, or null when it is missing, negative, above the
    /// maximum, or so small that it is stored as 0. The JSON reader already refuses text that
    /// is not a number in the range of <see cref="decimal"/> (NaN, Infinity, 1e400).
    /// </summary>
    private static decimal? Stored(decimal? price) =>
        price is >= 0 and <= MaxPrice && Math.Round(price.Value, StoredDecimals, MidpointRounding.AwayFromZero) is >= MinPrice and var stored
            ? stored
            : null;

    public static async Task<IResult> List(ClaimsPrincipal user, InstanceAuthorization admins, AppDbContext db, CancellationToken ct)
    {
        await admins.RequireAdminAsync(user, ct);
        // The labels of the kind sort as the text of the kind does.
        var rows = await db.ModelPrices.AsNoTracking()
            .OrderBy(p => p.ProviderKind).ThenBy(p => p.Model).ThenByDescending(p => p.EffectiveFrom)
            .ToListAsync(ct);
        return Results.Ok(rows
            .Select(p => new ModelPriceDto(EnumText.Of(p.ProviderKind), p.Model, p.InputUsdPerMillionTokens, p.OutputUsdPerMillionTokens, p.EffectiveFrom))
            .ToList());
    }

    public static async Task<IResult> Set(
        SetModelPriceRequest request, ClaimsPrincipal user, InstanceAuthorization admins, AppDbContext db, TimeProvider time, CancellationToken ct)
    {
        var adminId = await admins.RequireAdminAsync(user, ct);

        var errors = new List<FieldError>();
        if (request.ProviderKind is null || !AiProviderKinds.All.Contains(request.ProviderKind))
            errors.Add(new("providerKind", $"Must be one of: {string.Join(", ", AiProviderKinds.All)}."));
        if (request.Model is not { Length: >= 1 and <= 200 } || request.Model.Any(char.IsControl))
            errors.Add(new("model", "Must be 1 to 200 characters."));
        if (Stored(request.InputUsdPerMillionTokens) is null)
            errors.Add(new("inputUsdPerMillionTokens", PriceRule));
        if (Stored(request.OutputUsdPerMillionTokens) is null)
            errors.Add(new("outputUsdPerMillionTokens", PriceRule));
        if (errors.Count > 0)
            return Results.BadRequest(new ErrorResponse("validation_error", "Validation failed", Errors: errors));

        // The answer holds the prices as they are stored.
        var price = new ModelPriceDto(
            request.ProviderKind!, request.Model!, Stored(request.InputUsdPerMillionTokens)!.Value, Stored(request.OutputUsdPerMillionTokens)!.Value,
            (request.EffectiveFrom ?? time.GetUtcNow()).ToUniversalTime());
        var id = Guid.NewGuid();
        var kind = EnumText.Parse<AiProviderKind>(price.ProviderKind);

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO ai.model_prices
                (id, provider_kind, model, input_usd_per_million_tokens, output_usd_per_million_tokens, effective_from, created_at, created_by)
            VALUES
                ({id}, {kind}, {price.Model}, {price.InputUsdPerMillionTokens}, {price.OutputUsdPerMillionTokens},
                 {price.EffectiveFrom}, now(), {adminId})
            ON CONFLICT (provider_kind, model, effective_from) DO UPDATE
            SET input_usd_per_million_tokens = excluded.input_usd_per_million_tokens,
                output_usd_per_million_tokens = excluded.output_usd_per_million_tokens,
                created_at = excluded.created_at, created_by = excluded.created_by
            """, ct);
        await AuditLog.AppendAsync(db, null, adminId, "ai.price.set", $"{price.ProviderKind}/{price.Model}", ct);
        await transaction.CommitAsync(ct);

        return Results.Ok(price);
    }
}
