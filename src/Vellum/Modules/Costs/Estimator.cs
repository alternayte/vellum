using System.Security.Cryptography;
using System.Text.Json;
using Vellum.Modules.Trace.Engine;

namespace Vellum.Modules.Costs;

/// <summary>
/// A price as the estimate reads it. <paramref name="ObservedOn"/> is the day the number was
/// last taken from its source: the day a person entered a custom price, or the day Vellum
/// fetched a provider price. The age of a price is counted from that day.
/// </summary>
public sealed record Price(
    Guid Id, string Unit, decimal UnitPrice, string Currency, DateOnly EffectiveDate, string Source, DateOnly ObservedOn);

/// <param name="Currency">The currency of the workspace. A line in another currency is computed and flagged; nothing is converted.</param>
/// <param name="StaleAfterDays">A price observed more than this many days before <paramref name="AsOf"/> is flagged.</param>
public sealed record EstimateInput(
    IReadOnlyList<CostResource> Resources,
    IReadOnlyList<CostAssumption> Assumptions,
    IReadOnlyDictionary<Guid, Price> Prices,
    DateOnly AsOf,
    string Currency,
    int StaleAfterDays);

public static class LineStatus
{
    public const string Known = "known";
    public const string Unknown = "unknown";
}

/// <summary>Why a line has no amount. A line with one of these reasons is in no total.</summary>
public static class UnknownReason
{
    /// <summary>The line names no price entry.</summary>
    public const string NoPrice = "no-price";
    /// <summary>The price entry the line names is not in the price book of the workspace.</summary>
    public const string PriceNotFound = "price-not-found";
    /// <summary>The effective date of the price is after the date of the estimate.</summary>
    public const string PriceNotEffective = "price-not-effective";
    /// <summary>A unit is not one Vellum reads, or the quantity and the price are in units that do not convert.</summary>
    public const string UnitMismatch = "unit-mismatch";
    /// <summary>The assumption the usage line names is not in the sheet.</summary>
    public const string MissingAssumption = "missing-assumption";
    /// <summary>The effective date of the assumption is after the date of the estimate.</summary>
    public const string AssumptionNotEffective = "assumption-not-effective";
}

/// <summary>A mark on a line that has an amount. The amount is in its total.</summary>
public static class LineFlag
{
    public const string StalePrice = "stale-price";
    /// <summary>The price is not in the currency of the workspace. The amount is in the total of its own currency.</summary>
    public const string CurrencyMismatch = "currency-mismatch";
}

/// <summary>The quantity of a line and where it comes from: the resource (its fact status) or an assumption (its label).</summary>
public sealed record QuantitySource(decimal Value, string Unit, string Label, Guid? AssumptionId);

public sealed record PriceSource(
    Guid PriceId, decimal UnitPrice, string Unit, string Currency, string Source, DateOnly EffectiveDate, DateOnly ObservedOn);

/// <param name="Id">The resource ID for the baseline line; the resource ID, a colon and the usage line ID for a usage line.</param>
/// <param name="Units">The number of price units the quantity is, after <paramref name="Conversion"/>.</param>
/// <param name="Amount"><paramref name="Units"/> times the unit price, rounded. Null for an unknown line.</param>
public sealed record EstimateLine(
    string Id, Guid ResourceId, Guid? UsageLineId, Guid ElementId, string ResourceName, string Name, string Kind,
    string Category, string Provider, string Region,
    string Status, string? Reason, string? Detail, IReadOnlyList<string> Flags,
    QuantitySource? Quantity, PriceSource? Price, string? Conversion, decimal? Units, decimal? Amount, string? Currency);

public sealed record Money(string Currency, decimal Amount);

public sealed record CategorySubtotal(string Category, IReadOnlyList<Money> Known, int KnownLines, int UnknownLines);

public sealed record UnknownLine(string LineId, Guid ResourceId, Guid ElementId, string ResourceName, string Name, string Category, string Reason, string Detail);

/// <summary>
/// A monthly infrastructure estimate. <paramref name="Totals"/> hold known lines only, one
/// total for each currency. <paramref name="Unknown"/> lists every line that is in no total.
/// </summary>
public sealed record Estimate(
    string Engine, DateOnly AsOf, string Currency, int StaleAfterDays,
    IReadOnlyList<EstimateLine> Lines,
    IReadOnlyList<CategorySubtotal> Categories,
    IReadOnlyList<Money> Totals,
    IReadOnlyList<UnknownLine> Unknown);

/// <summary>
/// The estimate as a function of its inputs and nothing else: no clock, no database and no
/// price of its own. Arithmetic is decimal.
/// <para>
/// <b>Rounding.</b> The amount of a line is rounded to <see cref="AmountDecimals"/> decimal
/// places, half away from zero. A subtotal and a total are sums of rounded line amounts and
/// are not rounded again, so the lines add up to the totals.
/// </para>
/// </summary>
public static class Estimator
{
    public const string EngineVersion = "cost-1";
    public const int AmountDecimals = 4;

    public const string Baseline = "baseline";
    public const string Usage = "usage";

    public static Estimate Run(EstimateInput input)
    {
        var assumptions = input.Assumptions.ToDictionary(a => a.Id);
        var lines = new List<EstimateLine>();

        foreach (var resource in input.Resources
                     .OrderBy(r => r.ElementId).ThenBy(r => r.Name, StringComparer.Ordinal).ThenBy(r => r.Id))
        {
            // A resource with usage lines only has no baseline. A resource with neither has
            // nothing that could be priced, and says so with one unknown line.
            if (resource.Quantity is { } quantity && resource.Unit is { } unit)
            {
                lines.Add(Line(input, resource, null, "Baseline", Baseline,
                    new QuantitySource(quantity, unit, resource.FactStatus, null), resource.PriceRef));
            }
            else if (resource.Usage.Count == 0)
            {
                lines.Add(Unknown(resource, null, "Baseline", Baseline, null, null, UnknownReason.NoPrice,
                    "The resource has no quantity and no usage line."));
            }

            foreach (var usage in resource.Usage.OrderBy(u => u.Name, StringComparer.Ordinal).ThenBy(u => u.Id))
            {
                if (!assumptions.TryGetValue(usage.AssumptionId, out var assumption))
                {
                    lines.Add(Unknown(resource, usage, usage.Name, Usage, null, null, UnknownReason.MissingAssumption,
                        $"The assumption {usage.AssumptionId} is not in the sheet."));
                    continue;
                }

                var source = new QuantitySource(assumption.Value, assumption.Unit, assumption.Label, assumption.Id);
                lines.Add(assumption.EffectiveDate > input.AsOf
                    ? Unknown(resource, usage, usage.Name, Usage, source, null, UnknownReason.AssumptionNotEffective,
                        $"The assumption '{assumption.Name}' is effective from {assumption.EffectiveDate:yyyy-MM-dd}.")
                    : Line(input, resource, usage, usage.Name, Usage, source, usage.PriceRef));
            }
        }

        var known = lines.Where(l => l.Status == LineStatus.Known).ToList();
        var categories = CostCategory.All
            .Select(category => (Category: category, Lines: lines.Where(l => l.Category == category).ToList()))
            .Where(c => c.Lines.Count > 0)
            .Select(c => new CategorySubtotal(
                c.Category, Sum(c.Lines),
                c.Lines.Count(l => l.Status == LineStatus.Known), c.Lines.Count(l => l.Status == LineStatus.Unknown)))
            .ToList();
        var unknown = lines
            .Where(l => l.Status == LineStatus.Unknown)
            .Select(l => new UnknownLine(l.Id, l.ResourceId, l.ElementId, l.ResourceName, l.Name, l.Category, l.Reason!, l.Detail!))
            .ToList();

        return new Estimate(EngineVersion, input.AsOf, input.Currency, input.StaleAfterDays, lines, categories, Sum(known), unknown);
    }

    /// <summary>The known amounts of the lines, one sum for each currency, in currency order.</summary>
    public static IReadOnlyList<Money> Sum(IEnumerable<EstimateLine> lines) => lines
        .Where(l => l.Amount is not null)
        .GroupBy(l => l.Currency!, StringComparer.Ordinal)
        .OrderBy(g => g.Key, StringComparer.Ordinal)
        .Select(g => new Money(g.Key, g.Sum(l => l.Amount!.Value)))
        .ToList();

    private static EstimateLine Line(
        EstimateInput input, CostResource resource, UsageLine? usage, string name, string kind, QuantitySource quantity, Guid? priceRef)
    {
        if (priceRef is not { } priceId)
            return Unknown(resource, usage, name, kind, quantity, null, UnknownReason.NoPrice, "The line names no price entry.");
        if (!input.Prices.TryGetValue(priceId, out var price))
        {
            return Unknown(resource, usage, name, kind, quantity, null, UnknownReason.PriceNotFound,
                $"The price entry {priceId} is not in the price book of the workspace.");
        }

        var source = new PriceSource(price.Id, price.UnitPrice, price.Unit, price.Currency, price.Source, price.EffectiveDate, price.ObservedOn);
        if (price.EffectiveDate > input.AsOf)
        {
            return Unknown(resource, usage, name, kind, quantity, source, UnknownReason.PriceNotEffective,
                $"The price is effective from {price.EffectiveDate:yyyy-MM-dd}.");
        }
        if (UnitExpression.Parse(quantity.Unit) is not { } quantityUnit)
        {
            return Unknown(resource, usage, name, kind, quantity, source, UnknownReason.UnitMismatch,
                $"Vellum does not read the unit '{quantity.Unit}' of the quantity.");
        }
        if (UnitExpression.Parse(price.Unit) is not { } priceUnit)
        {
            return Unknown(resource, usage, name, kind, quantity, source, UnknownReason.UnitMismatch,
                $"Vellum does not read the unit '{price.Unit}' of the price.");
        }
        if (UnitExpression.MonthlyUnits(quantity.Value, quantityUnit, priceUnit, out var conversion, out var mismatch) is not { } units)
            return Unknown(resource, usage, name, kind, quantity, source, UnknownReason.UnitMismatch, mismatch!);

        var flags = new List<string>();
        if (input.AsOf.DayNumber - price.ObservedOn.DayNumber > input.StaleAfterDays)
            flags.Add(LineFlag.StalePrice);
        if (!string.Equals(price.Currency, input.Currency, StringComparison.Ordinal))
            flags.Add(LineFlag.CurrencyMismatch);

        var amount = Math.Round(units * price.UnitPrice, AmountDecimals, MidpointRounding.AwayFromZero);
        return new EstimateLine(
            LineId(resource, usage), resource.Id, usage?.Id, resource.ElementId, resource.Name, name, kind,
            resource.Category, resource.Provider, resource.Region,
            LineStatus.Known, null, null, flags, quantity, source,
            $"{conversion} × {UnitExpression.Text(price.UnitPrice)} {price.Currency}", units, amount, price.Currency);
    }

    private static EstimateLine Unknown(
        CostResource resource, UsageLine? usage, string name, string kind, QuantitySource? quantity, PriceSource? price,
        string reason, string detail) => new(
        LineId(resource, usage), resource.Id, usage?.Id, resource.ElementId, resource.Name, name, kind,
        resource.Category, resource.Provider, resource.Region,
        LineStatus.Unknown, reason, detail, [], quantity, price, null, null, null, null);

    private static string LineId(CostResource resource, UsageLine? usage) =>
        usage is null ? resource.Id.ToString() : $"{resource.Id}:{usage.Id}";
}

/// <summary>
/// The JSON of the cost tables and of every hash this module makes: one set of options, so
/// what is written reads back.
/// </summary>
internal static class CostJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static string Write<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T Read<T>(string json) => JsonSerializer.Deserialize<T>(json, Options)!;

    /// <summary>
    /// SHA-256 of the canonical JSON of a value, in lower-case hex. The canonical form gives
    /// a number one spelling, so <c>1.50</c> and <c>1.5</c> hash alike and a stored estimate
    /// keeps its hash.
    /// </summary>
    public static string Hash<T>(T value)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
            CanonicalJson.Write(writer, JsonSerializer.SerializeToElement(value, Options));
        return Convert.ToHexStringLower(SHA256.HashData(buffer.GetBuffer().AsSpan(0, (int)buffer.Length)));
    }
}
