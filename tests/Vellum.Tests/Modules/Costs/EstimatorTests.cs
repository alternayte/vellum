using System.Collections.Immutable;
using System.Globalization;
using Vellum.Modules.Costs;
using Vellum.Modules.Costs.Prices;
using Vellum.Tests.Shared.Outbound;

namespace Vellum.Tests.Modules.Costs;

/// <summary>
/// The estimate function over the committed input <c>Fixtures/Costs/orders-estimate.input.json</c>.
/// Every expected number here was computed by hand from that file.
/// </summary>
public class EstimatorTests
{
    private const string PinnedHash = "c7e4f77c6c6e5715ad08f7c9633fd746f9db123abdf053c945a88680db89ec53";

    private static EstimateInput Input() => CostJson.Read<EstimateInput>(Fixtures.Read("Costs/orders-estimate.input.json"));

    private static EstimateLine Line(Estimate estimate, string resource, string name = "Baseline") =>
        estimate.Lines.Single(l => l.ResourceName == resource && l.Name == name);

    [Fact]
    public void The_same_inputs_give_the_same_estimate_and_the_same_hash()
    {
        var input = Input();
        var first = Estimator.Run(input);

        // The order of the inputs is not part of the result.
        var shuffled = input with
        {
            Resources = input.Resources.Reverse().Select(r => r with { Usage = r.Usage.Reverse().ToImmutableList() }).ToList(),
            Assumptions = input.Assumptions.Reverse().ToList(),
        };
        var second = Estimator.Run(shuffled);

        Assert.Equal(CostJson.Hash(first), CostJson.Hash(second));
        // The pinned hash of the result for the committed input. A change of the engine that
        // changes any number, text or field of the result changes it, and needs a new engine version.
        var hash = CostJson.Hash(first);
        Assert.True(hash == PinnedHash, $"The hash of the estimate is {hash}.");

        // A stored estimate keeps its hash: jsonb gives a number another spelling, the canonical form does not.
        var stored = CostJson.Read<Estimate>(CostJson.Write(first));
        Assert.Equal(CostJson.Hash(first), CostJson.Hash(stored));
    }

    [Fact]
    public void Totals_hold_known_lines_only_with_one_total_for_each_currency()
    {
        var estimate = Estimator.Run(Input());

        Assert.Equal([new Money("EUR", 0.8624m), new Money("USD", 424.0958m)], estimate.Totals);
        Assert.Equal(estimate.Totals.Single(t => t.Currency == "USD").Amount,
            estimate.Lines.Where(l => l.Currency == "USD").Sum(l => l.Amount!.Value));

        Assert.Equal(["compute", "database", "queue", "storage", "egress", "other"], estimate.Categories.Select(c => c.Category));
        var compute = estimate.Categories[0];
        Assert.Equal([new Money("USD", 214.8958m)], compute.Known);
        Assert.Equal((3, 2), (compute.KnownLines, compute.UnknownLines));
        Assert.Equal([new Money("EUR", 0.8624m), new Money("USD", 182.5m)], estimate.Categories[1].Known);
        Assert.Empty(estimate.Categories.Single(c => c.Category == "egress").Known);

        // The line in euro is computed and flagged. It is in no dollar total.
        var storage = Line(estimate, "Orders DB storage");
        Assert.Equal((0.8624m, "EUR"), (storage.Amount, storage.Currency));
        Assert.Equal([LineFlag.CurrencyMismatch], storage.Flags);
    }

    [Fact]
    public void A_line_shows_its_unit_conversion_and_where_each_number_comes_from()
    {
        var estimate = Estimator.Run(Input());

        // Two instances for a month of 730 hours at a price for one hour.
        var instances = Line(estimate, "API instances");
        Assert.Equal((1460m, 167.9m), (instances.Units, instances.Amount));
        Assert.Equal("2 instance × 730 hour per month = 1460 hour × 0.115 USD", instances.Conversion);
        Assert.Equal(new QuantitySource(2m, "instances", "declared", null), instances.Quantity);
        Assert.Equal((Guid.Parse("d0000000-0000-0000-0000-000000000001"), "fetched", new DateOnly(2026, 10, 1)),
            (instances.Price!.PriceId, instances.Price.Source, instances.Price.ObservedOn));

        // A usage line is one unit price times one assumption.
        var egress = Line(estimate, "API instances", "Egress");
        Assert.Equal((Estimator.Usage, 500m, 40m), (egress.Kind, egress.Units, egress.Amount));
        Assert.Equal((Guid.Parse("c0000000-0000-0000-0000-000000000001"), "declared"), (egress.Quantity!.AssumptionId, egress.Quantity.Label));

        // A price for a million messages.
        var messages = Line(estimate, "Broker", "Messages");
        Assert.Equal((12m, 4.8m, "measured"), (messages.Units, messages.Amount, messages.Quantity!.Label));
        Assert.Equal("12000000 message ÷ 1000000 × 0.4 USD", messages.Conversion);

        // A price per hour of a thing that exists all month.
        Assert.Equal((2190m, 21.9m), (Line(estimate, "Disks").Units, Line(estimate, "Disks").Amount));

        // Hours on each day: 2 × 730 ÷ 24 = 60.8333…; times 0.115 is 6.99583…, rounded to four places.
        var nightly = Line(estimate, "Nightly job");
        Assert.Equal((6.9958m, "inferred"), (nightly.Amount, nightly.Quantity!.Label));

        Assert.Equal(730m, Line(estimate, "Orders DB server").Units);
    }

    [Fact]
    public void A_line_that_cannot_be_priced_is_unknown_with_its_reason_and_is_in_no_total()
    {
        var estimate = Estimator.Run(Input());

        Assert.Equal(
        [
            ("Backups", "Baseline", UnknownReason.NoPrice),
            ("Broker", "Planned traffic", UnknownReason.AssumptionNotEffective),
            ("Broker", "Requests", UnknownReason.MissingAssumption),
            ("CDN", "Baseline", UnknownReason.PriceNotFound),
            ("Cache", "Baseline", UnknownReason.UnitMismatch),
            ("Log volume", "Baseline", UnknownReason.UnitMismatch),
            ("Next year", "Baseline", UnknownReason.PriceNotEffective),
            ("Placeholder", "Baseline", UnknownReason.NoPrice),
        ], estimate.Unknown
            .OrderBy(u => u.ResourceName, StringComparer.Ordinal).ThenBy(u => u.Name, StringComparer.Ordinal)
            .Select(u => (u.ResourceName, u.Name, u.Reason)));

        Assert.All(estimate.Lines.Where(l => l.Status == LineStatus.Unknown), l => Assert.Null(l.Amount));
        Assert.Contains("'10K'", Line(estimate, "Cache").Detail);
        Assert.Equal("The quantity is in 'gb' and the price is for 'hour'. Vellum does not convert between these units.", Line(estimate, "Log volume").Detail);
    }

    [Fact]
    public void A_price_older_than_the_staleness_age_is_computed_and_flagged()
    {
        var input = Input();
        var server = Line(Estimator.Run(input), "Orders DB server");

        // Entered on 2026-01-05, 281 days before the date of the estimate.
        Assert.Equal((LineStatus.Known, 182.5m), (server.Status, server.Amount));
        Assert.Equal([LineFlag.StalePrice], server.Flags);

        Assert.Empty(Line(Estimator.Run(input with { StaleAfterDays = 281 }), "Orders DB server").Flags);
        Assert.Equal([LineFlag.StalePrice], Line(Estimator.Run(input with { StaleAfterDays = 280 }), "Orders DB server").Flags);

        // The age counts from the day the number was read, not from the provider's effective date of 2021.
        Assert.Empty(Line(Estimator.Run(input), "API instances").Flags);
    }

    private static readonly Guid Element = Guid.Parse("e1000000-0000-0000-0000-000000000001");
    private static readonly Guid PriceId = Guid.Parse("d0000000-0000-0000-0000-000000000001");

    private static CostResource Hours(string name, decimal quantity, string unit = "hours") => new(
        Guid.NewGuid(), Element, name, "compute", "azure", "westeurope", "", quantity, unit, PriceId, [], "declared", null);

    private static Price PriceFor(string provider, string region, string unit = "hour", decimal unitPrice = 0.5m) => new(
        PriceId, provider, region, "D2s v5", "meter-1", unit, unitPrice, "USD", new DateOnly(2026, 1, 1), "fetched", new DateOnly(2026, 10, 1));

    private static Estimate Run(Price price, bool elementInModel, params CostResource[] resources) => Estimator.Run(new EstimateInput(
        resources, [], new Dictionary<Guid, Price> { [price.Id] = price }, elementInModel ? new HashSet<Guid> { Element } : [],
        new DateOnly(2026, 10, 13), "USD", 90));

    [Fact]
    public void A_price_for_another_provider_or_region_gives_no_amount_and_a_line_says_what_its_price_is_for()
    {
        var known = Run(PriceFor("azure", "westeurope"), true, Hours("API", 10)).Lines.Single();
        Assert.Equal((LineStatus.Known, 5m), (known.Status, known.Amount));
        Assert.Equal(("azure", "westeurope", "D2s v5", "meter-1", "fetched"),
            (known.Price!.Provider, known.Price.Region, known.Price.Sku, known.Price.MeterId, known.Price.Source));

        // The same number for another provider is not a price of this resource.
        var otherProvider = Run(PriceFor("aws", "westeurope"), true, Hours("API", 10));
        Assert.Equal((LineStatus.Unknown, UnknownReason.PriceProviderMismatch, null),
            (otherProvider.Lines.Single().Status, otherProvider.Lines.Single().Reason, otherProvider.Lines.Single().Amount));
        Assert.Equal("aws", otherProvider.Lines.Single().Price!.Provider);
        Assert.Empty(otherProvider.Totals);

        var otherRegion = Run(PriceFor("azure", "eastus"), true, Hours("API", 10));
        Assert.Equal(UnknownReason.PriceRegionMismatch, otherRegion.Lines.Single().Reason);
        Assert.Empty(otherRegion.Totals);

        // A price that names no region is a price for every region of its provider.
        Assert.Equal(5m, Run(PriceFor("azure", ""), true, Hours("API", 10)).Lines.Single().Amount);
    }

    [Fact]
    public void A_resource_of_an_element_that_the_model_no_longer_has_is_unknown_and_in_no_total()
    {
        var estimate = Run(PriceFor("azure", "westeurope"), elementInModel: false, Hours("API", 10));

        var line = estimate.Lines.Single();
        Assert.Equal((LineStatus.Unknown, UnknownReason.ElementNotInModel, null), (line.Status, line.Reason, line.Amount));
        Assert.Empty(estimate.Totals);
        Assert.Equal(UnknownReason.ElementNotInModel, estimate.Unknown.Single().Reason);
    }

    [Fact]
    public void Inputs_that_pass_validation_and_multiply_past_a_decimal_give_an_unknown_line_and_not_a_failure()
    {
        // Each number is one that the sheet and the price book accept.
        const string unit = "1000000000 instances";
        const string priceUnit = "0.001 hour";
        Assert.Null(CostRules.Quantity("quantity", CostRules.MaxQuantity));
        Assert.Null(CostRules.Unit("unit", unit));
        Assert.Null(CostRules.Unit("unit", priceUnit));
        Assert.Null(PriceRules.UnitPrice("unitPrice", PriceRules.MaxUnitPrice));

        // 1e12 × 1e9 instances × 730 hours ÷ 0.001 × 1e6 is about 7e32. A decimal holds 7.9e28.
        var estimate = Run(PriceFor("azure", "westeurope", priceUnit, PriceRules.MaxUnitPrice), true,
            Hours("Huge", CostRules.MaxQuantity, unit), Hours("Small", 1, "instances"));

        var huge = estimate.Lines.Single(l => l.ResourceName == "Huge");
        Assert.Equal((LineStatus.Unknown, UnknownReason.AmountOutOfRange, null), (huge.Status, huge.Reason, huge.Amount));
        // The other line and the total are computed: 730 hours ÷ 0.001 × 1e6.
        Assert.Equal([new Money("USD", 730_000_000_000m)], estimate.Totals);
    }

    [Fact]
    public void The_estimate_and_its_hash_do_not_depend_on_the_culture_of_the_server()
    {
        var before = CultureInfo.CurrentCulture;
        try
        {
            // The Thai calendar counts the year 2026 as 2569.
            CultureInfo.CurrentCulture = new CultureInfo("th-TH");
            var estimate = Estimator.Run(Input());

            Assert.Contains("2027-01-01", Line(estimate, "Next year").Detail);
            Assert.Equal(PinnedHash, CostJson.Hash(estimate));
        }
        finally
        {
            CultureInfo.CurrentCulture = before;
        }
    }

    [Theory]
    [InlineData("1 Hour", 1, "hour", null)]
    [InlineData("hrs", 1, "hour", null)]
    [InlineData("100 GB/Month", 100, "gb", "month")]
    [InlineData("requests / day", 1, "request", "day")]
    [InlineData(" 0.5 vCPU ", 0.5, "vcpu", null)]
    public void A_unit_is_read_as_a_size_a_name_and_a_period(string text, double size, string dimension, string? period) =>
        Assert.Equal(new UnitExpression((decimal)size, dimension, period), UnitExpression.Parse(text));

    [Theory]
    [InlineData("10K")]
    [InlineData("100GB")]
    [InlineData("1/Month")]
    [InlineData("GB/year")]
    [InlineData("0 GB")]
    [InlineData("")]
    public void Text_that_is_not_a_unit_is_not_read(string text) => Assert.Null(UnitExpression.Parse(text));
}

/// <summary>Comparison of two estimates. A verdict needs both sides complete and one currency.</summary>
public class ComparerTests
{
    private static readonly Guid Element = Guid.Parse("e1000000-0000-0000-0000-000000000001");
    private static readonly Guid Hourly = Guid.Parse("d0000000-0000-0000-0000-000000000001");
    private static readonly Guid Euro = Guid.Parse("d0000000-0000-0000-0000-000000000002");

    private static CostResource Resource(string name, decimal quantity, Guid? price, string category = "compute") => new(
        Guid.NewGuid(), Element, name, category, "azure", "westeurope", "", quantity, "hours", price, [], "declared", null);

    private static Estimate Run(params CostResource[] resources) => Estimator.Run(new EstimateInput(
        resources, [],
        new Dictionary<Guid, Price>
        {
            [Hourly] = new(Hourly, "azure", "westeurope", "small", null, "hour", 0.5m, "USD", new DateOnly(2026, 1, 1), "custom", new DateOnly(2026, 10, 1)),
            [Euro] = new(Euro, "azure", "westeurope", "small", null, "hour", 0.4m, "EUR", new DateOnly(2026, 1, 1), "custom", new DateOnly(2026, 10, 1)),
        },
        new HashSet<Guid> { Element },
        new DateOnly(2026, 10, 13), "USD", 90));

    [Fact]
    public void Two_complete_sides_in_one_currency_get_deltas_and_a_verdict()
    {
        var comparison = Comparer.Run(
            Run(Resource("API", 100, Hourly), Resource("Worker", 10, Hourly), Resource("Old", 4, Hourly, "storage")),
            Run(Resource("API", 60, Hourly), Resource("Worker", 10, Hourly), Resource("New", 2, Hourly, "queue")));

        Assert.True(comparison.Comparable);
        Assert.Equal((Cheaper.Right, new Money("USD", 21m)), (comparison.Cheaper, comparison.Difference));
        Assert.Equal([new AmountDelta("USD", 57m, 36m, -21m)], comparison.Totals);
        Assert.Equal(
        [
            ("API", ResourceChange.Changed, -20m), ("New", ResourceChange.Added, 1m),
            ("Old", ResourceChange.Removed, -2m), ("Worker", ResourceChange.Same, 0m),
        ], comparison.Resources.Select(r => (r.Name, r.Change, r.Amounts.Single().Delta!.Value)));
        Assert.Equal(
            [("compute", (decimal?)-20m), ("queue", 1m), ("storage", -2m)],
            comparison.Categories.Select(c => (c.Category, c.Amounts.Single().Delta)));
    }

    [Theory]
    [InlineData(true, false, NotComparable.LeftHasUnknown)]
    [InlineData(false, true, NotComparable.RightHasUnknown)]
    [InlineData(true, true, NotComparable.BothHaveUnknown)]
    public void A_side_with_an_unknown_line_gets_no_verdict(bool leftUnknown, bool rightUnknown, string reason)
    {
        // The left side is the cheaper one by its known lines. That says nothing: a line has no price.
        var comparison = Comparer.Run(
            Run(Resource("API", 10, Hourly), Resource("Database", 1, leftUnknown ? null : Hourly, "database")),
            Run(Resource("API", 100, Hourly), Resource("Database", 1, rightUnknown ? null : Hourly, "database")));

        Assert.False(comparison.Comparable);
        Assert.Equal(reason, comparison.Reason);
        Assert.Null(comparison.Cheaper);
        Assert.Null(comparison.Difference);
        Assert.Equal(leftUnknown ? 1 : 0, comparison.LeftUnknown.Count);
        Assert.Equal(rightUnknown ? 1 : 0, comparison.RightUnknown.Count);

        // No delta where a sum is incomplete: the total and the database rows. The API row is complete on both sides.
        Assert.Null(comparison.Totals.Single().Delta);
        Assert.Null(comparison.Categories.Single(c => c.Category == "database").Amounts.SingleOrDefault()?.Delta);
        Assert.Equal(45m, comparison.Resources.Single(r => r.Name == "API").Amounts.Single().Delta);
        Assert.Equal(45m, comparison.Categories.Single(c => c.Category == "compute").Amounts.Single().Delta);
    }

    [Fact]
    public void A_side_with_an_empty_sheet_is_not_the_cheaper_side_and_gets_no_difference()
    {
        var comparison = Comparer.Run(Run(Resource("API", 100, Hourly)), Run());

        Assert.False(comparison.Comparable);
        Assert.Equal(NotComparable.RightEmpty, comparison.Reason);
        Assert.Null(comparison.Cheaper);
        Assert.Null(comparison.Difference);
        // The left amount is shown. No row says that the empty side costs 50 less.
        Assert.Equal([new AmountDelta("USD", 50m, null, null)], comparison.Totals);
        Assert.Null(comparison.Resources.Single().Amounts.Single().Delta);
        Assert.Null(comparison.Categories.Single().Amounts.Single().Delta);

        Assert.Equal(NotComparable.LeftEmpty, Comparer.Run(Run(), Run(Resource("API", 100, Hourly))).Reason);
        Assert.Equal(NotComparable.BothEmpty, Comparer.Run(Run(), Run()).Reason);
    }

    [Fact]
    public void Sides_in_two_currencies_get_no_verdict_and_nothing_is_converted()
    {
        var comparison = Comparer.Run(Run(Resource("API", 10, Hourly)), Run(Resource("API", 10, Euro)));

        Assert.False(comparison.Comparable);
        Assert.Equal(NotComparable.SeveralCurrencies, comparison.Reason);
        Assert.Equal([new AmountDelta("EUR", null, 4m, 4m), new AmountDelta("USD", 5m, null, -5m)], comparison.Totals);
    }
}
