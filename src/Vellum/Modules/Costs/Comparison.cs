namespace Vellum.Modules.Costs;

/// <summary>
/// One currency of one row. <paramref name="Left"/> and <paramref name="Right"/> are the sums
/// of the known lines, or null where the side has no known line in the currency.
/// <paramref name="Delta"/> is right minus left, and null when a line of the row is unknown
/// on either side: a difference of two incomplete sums says nothing.
/// </summary>
public sealed record AmountDelta(string Currency, decimal? Left, decimal? Right, decimal? Delta);

public sealed record CategoryDelta(string Category, IReadOnlyList<AmountDelta> Amounts, int LeftUnknownLines, int RightUnknownLines);

public static class ResourceChange
{
    public const string Added = "added";
    public const string Removed = "removed";
    public const string Changed = "changed";
    public const string Same = "same";
}

/// <summary>A resource of the left side and the resource of the right side with the same element and the same name.</summary>
public sealed record ResourceDelta(
    Guid ElementId, string Name, string Change, Guid? LeftResourceId, Guid? RightResourceId,
    IReadOnlyList<AmountDelta> Amounts, int LeftUnknownLines, int RightUnknownLines);

public static class NotComparable
{
    public const string LeftHasUnknown = "left-has-unknown-lines";
    public const string RightHasUnknown = "right-has-unknown-lines";
    public const string BothHaveUnknown = "both-sides-have-unknown-lines";
    /// <summary>The known amounts are not all in one currency, and Vellum converts no currency.</summary>
    public const string SeveralCurrencies = "several-currencies";
}

public static class Cheaper
{
    public const string Left = "left";
    public const string Right = "right";
    public const string Equal = "equal";
}

/// <summary>
/// The difference between two estimates. <paramref name="Cheaper"/> and
/// <paramref name="Difference"/> are set only when <paramref name="Comparable"/> is true:
/// both sides have no unknown line and every amount is in one currency.
/// </summary>
public sealed record Comparison(
    bool Comparable, string? Reason, string? Cheaper, Money? Difference,
    IReadOnlyList<AmountDelta> Totals,
    IReadOnlyList<CategoryDelta> Categories,
    IReadOnlyList<ResourceDelta> Resources,
    IReadOnlyList<UnknownLine> LeftUnknown,
    IReadOnlyList<UnknownLine> RightUnknown);

public static class Comparer
{
    public static Comparison Run(Estimate left, Estimate right)
    {
        var totals = Deltas(left.Totals, right.Totals, complete: left.Unknown.Count == 0 && right.Unknown.Count == 0);

        var categories = CostCategory.All
            .Select(category => (
                Category: category,
                Left: left.Categories.FirstOrDefault(c => c.Category == category),
                Right: right.Categories.FirstOrDefault(c => c.Category == category)))
            .Where(c => c.Left is not null || c.Right is not null)
            .Select(c => new CategoryDelta(
                c.Category,
                Deltas(c.Left?.Known ?? [], c.Right?.Known ?? [], (c.Left?.UnknownLines ?? 0) + (c.Right?.UnknownLines ?? 0) == 0),
                c.Left?.UnknownLines ?? 0, c.Right?.UnknownLines ?? 0))
            .ToList();

        var leftResources = ByResource(left);
        var rightResources = ByResource(right);
        var resources = leftResources.Keys.Union(rightResources.Keys)
            .OrderBy(k => k.ElementId).ThenBy(k => k.Name, StringComparer.Ordinal)
            .Select(key =>
            {
                var l = leftResources.GetValueOrDefault(key);
                var r = rightResources.GetValueOrDefault(key);
                var leftUnknown = l?.Count(x => x.Status == LineStatus.Unknown) ?? 0;
                var rightUnknown = r?.Count(x => x.Status == LineStatus.Unknown) ?? 0;
                var amounts = Deltas(Estimator.Sum(l ?? []), Estimator.Sum(r ?? []), leftUnknown + rightUnknown == 0);
                var change = l is null ? ResourceChange.Added
                    : r is null ? ResourceChange.Removed
                    : SameLines(l, r) ? ResourceChange.Same
                    : ResourceChange.Changed;
                return new ResourceDelta(key.ElementId, key.Name, change, l?[0].ResourceId, r?[0].ResourceId, amounts, leftUnknown, rightUnknown);
            })
            .ToList();

        var reason = (left.Unknown.Count > 0, right.Unknown.Count > 0) switch
        {
            (true, true) => NotComparable.BothHaveUnknown,
            (true, false) => NotComparable.LeftHasUnknown,
            (false, true) => NotComparable.RightHasUnknown,
            _ => totals.Count > 1 ? NotComparable.SeveralCurrencies : null,
        };
        if (reason is not null)
            return new Comparison(false, reason, null, null, totals, categories, resources, left.Unknown, right.Unknown);

        // No unknown line and at most one currency. Two empty estimates are equal.
        var delta = totals.Count == 0 ? 0m : totals[0].Delta!.Value;
        return new Comparison(
            true, null,
            delta < 0 ? Cheaper.Right : delta > 0 ? Cheaper.Left : Cheaper.Equal,
            totals.Count == 0 ? null : new Money(totals[0].Currency, Math.Abs(delta)),
            totals, categories, resources, left.Unknown, right.Unknown);
    }

    /// <summary>Resources are matched by element and name: the same resource added to two branches by hand has two IDs.</summary>
    private static Dictionary<(Guid ElementId, string Name), List<EstimateLine>> ByResource(Estimate estimate) => estimate.Lines
        .GroupBy(l => (l.ElementId, Name: l.ResourceName))
        .ToDictionary(g => g.Key, g => g.ToList());

    private static bool SameLines(List<EstimateLine> left, List<EstimateLine> right) =>
        left.Count == right.Count
        && left.Zip(right).All(pair =>
            pair.First.Name == pair.Second.Name && pair.First.Kind == pair.Second.Kind
            && pair.First.Status == pair.Second.Status && pair.First.Reason == pair.Second.Reason
            && pair.First.Amount == pair.Second.Amount && pair.First.Currency == pair.Second.Currency);

    private static IReadOnlyList<AmountDelta> Deltas(IReadOnlyList<Money> left, IReadOnlyList<Money> right, bool complete) => left
        .Select(m => m.Currency).Union(right.Select(m => m.Currency))
        .Order(StringComparer.Ordinal)
        .Select(currency =>
        {
            var l = left.FirstOrDefault(m => m.Currency == currency)?.Amount;
            var r = right.FirstOrDefault(m => m.Currency == currency)?.Amount;
            return new AmountDelta(currency, l, r, complete ? (r ?? 0m) - (l ?? 0m) : null);
        })
        .ToList();
}
