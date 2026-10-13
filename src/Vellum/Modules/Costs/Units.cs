using System.Globalization;
using System.Text.RegularExpressions;

namespace Vellum.Modules.Costs;

/// <summary>
/// A unit as an estimate reads it: <c>[number] name [/ hour | day | month]</c>, for example
/// <c>1 Hour</c>, <c>GB/month</c>, <c>100 GB/Month</c> or <c>instances</c>. The number is the
/// amount that one unit price pays for; it is 1 when the text has none. A space stands
/// between the number and the name, so a count such as <c>10K</c> is not read as 10 of a unit.
/// </summary>
/// <param name="Size">The amount of <paramref name="Dimension"/> that one unit price pays for.</param>
/// <param name="Dimension">The name in lower case and in the singular.</param>
/// <param name="Period"><c>hour</c>, <c>day</c>, <c>month</c> or null.</param>
public sealed partial record UnitExpression(decimal Size, string Dimension, string? Period)
{
    public const int MaxChars = 50;
    public const decimal MinSize = 0.001m;
    public const decimal MaxSize = 1_000_000_000m;

    /// <summary>
    /// The length of a month in hours: the 8760 hours of a year of 365 days, divided by 12.
    /// Every conversion between a month and an hour or a day uses this one number.
    /// </summary>
    public const decimal HoursPerMonth = 730m;
    public const decimal HoursPerDay = 24m;

    private const string Hour = "hour";
    private const string Day = "day";
    private const string Month = "month";
    private const string Instance = "instance";

    /// <summary>The unit, or null when the text is not a unit that this grammar reads.</summary>
    public static UnitExpression? Parse(string? text)
    {
        if (text is null || text.Length > MaxChars || Grammar().Match(text) is not { Success: true } match)
            return null;

        var size = 1m;
        if (match.Groups["size"] is { Success: true } number
            && (!decimal.TryParse(number.Value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out size)
                || size is < MinSize or > MaxSize))
            return null;

        string? period = null;
        if (match.Groups["period"].Success && (period = Time(match.Groups["period"].Value.ToLowerInvariant())) is null)
            return null;

        var name = Spaces().Replace(match.Groups["name"].Value.Trim().ToLowerInvariant(), " ");
        return new UnitExpression(size, Time(name) ?? Singular(name), period);
    }

    private static string? Time(string name) => name switch
    {
        "h" or "hr" or "hrs" or "hour" or "hours" => Hour,
        "d" or "day" or "days" => Day,
        "mo" or "month" or "months" => Month,
        _ => null,
    };

    private static string Singular(string name) => name.Length > 3 && name[^1] == 's' && name[^2] != 's' ? name[..^1] : name;

    private bool IsTime => Dimension is Hour or Day or Month;

    private decimal Hours => Dimension switch { Hour => 1m, Day => HoursPerDay, _ => HoursPerMonth };

    /// <summary>
    /// How many price units one month of <paramref name="value"/> <paramref name="of"/> is,
    /// with the conversion as text. Null with <paramref name="mismatch"/> set when the two
    /// units do not convert. The rules:
    /// <list type="bullet">
    /// <item>Two time units convert through hours: a day is 24 hours and a month is 730. Hours
    /// per day are hours on each day of the month.</item>
    /// <item>The same name on both sides: a quantity with no period, or per month, is the
    /// amount of one month; a quantity per day or per hour is multiplied up to a month. A
    /// price per hour or per day charges a quantity with no period for every hour or day of
    /// the month.</item>
    /// <item>A price per month is a price for an amount that is held for a month, such as
    /// stored GB. A quantity per day or per hour is multiplied up against it only when
    /// <c>flow</c> is true: 5 GB of new data on each day is not 152 GB held all month.</item>
    /// <item>A count of instances against a price per time unit: each instance runs for the whole month.</item>
    /// </list>
    /// </summary>
    /// <param name="flow">True when the quantity is an amount that passes in a period, such as egress or requests, and not an amount that is held.</param>
    public static decimal? MonthlyUnits(decimal value, UnitExpression of, UnitExpression price, bool flow, out string conversion, out string? mismatch)
    {
        mismatch = null;
        decimal amount;
        var quantity = value * of.Size;
        if (of.IsTime && price.IsTime && of.Period is null or Month && price.Period is null)
        {
            amount = quantity * of.Hours / price.Hours;
            conversion = of.Dimension == price.Dimension
                ? $"{Text(quantity)} {of.Dimension}"
                : $"{Text(quantity)} {of.Dimension} × {Text(of.Hours)} hour ÷ {Text(price.Hours)} hour = {Text(amount)} {price.Dimension}";
        }
        else if (of is { Dimension: Hour, Period: Day } && price is { IsTime: true, Period: null })
        {
            // Hours of use on each day of the month.
            amount = quantity * PeriodsPerMonth(Day) / price.Hours;
            conversion = $"{Text(quantity)} hour per day × {Text(PeriodsPerMonth(Day))} day per month = {Text(amount)} {price.Dimension}";
        }
        else if (!of.IsTime && !price.IsTime && of.Dimension == price.Dimension)
        {
            if (price.Period is Hour or Day)
            {
                if (of.Period is not null)
                    return Refuse(of, price, out conversion, out mismatch);
                amount = quantity * PeriodsPerMonth(price.Period);
                conversion = $"{Text(quantity)} {of.Dimension} × {Text(PeriodsPerMonth(price.Period))} {price.Period} per month = {Text(amount)} {price.Dimension}-{price.Period}";
            }
            else if (of.Period is Hour or Day)
            {
                if (price.Period is Month && !flow)
                {
                    conversion = "";
                    mismatch = $"The quantity is for each {of.Period} ('{of}') and the price is for an amount that is held for a month ('{price}'). "
                               + "Vellum multiplies a quantity for each hour or day up to a month only for a flow: a resource of the category egress, "
                               + "or an assumption of the kind requests, egress or messages. Enter the amount that is held in one month.";
                    return null;
                }
                amount = quantity * PeriodsPerMonth(of.Period);
                conversion = $"{Text(quantity)} {of.Dimension} per {of.Period} × {Text(PeriodsPerMonth(of.Period))} {of.Period} per month = {Text(amount)} {of.Dimension}";
            }
            else
            {
                amount = quantity;
                conversion = $"{Text(quantity)} {of.Dimension}";
            }
        }
        else if (of is { Dimension: Instance, Period: null } && price is { IsTime: true, Period: null })
        {
            amount = quantity * HoursPerMonth / price.Hours;
            conversion = $"{Text(quantity)} instance × {Text(HoursPerMonth / price.Hours)} {price.Dimension} per month = {Text(amount)} {price.Dimension}";
        }
        else
        {
            return Refuse(of, price, out conversion, out mismatch);
        }

        if (price.Size != 1m)
            conversion += $" ÷ {Text(price.Size)}";
        return amount / price.Size;
    }

    private static decimal PeriodsPerMonth(string period) => period == Hour ? HoursPerMonth : HoursPerMonth / HoursPerDay;

    private static decimal? Refuse(UnitExpression of, UnitExpression price, out string conversion, out string? mismatch)
    {
        conversion = "";
        mismatch = $"The quantity is in '{of}' and the price is for '{price}'. Vellum does not convert between these units.";
        return null;
    }

    public override string ToString() =>
        $"{(Size == 1m ? "" : Text(Size) + " ")}{Dimension}{(Period is null ? "" : "/" + Period)}";

    /// <summary>A decimal with no trailing zeros and no exponent, the same on every server.</summary>
    public static string Text(decimal value) => (value / 1.0000000000000000000000000000m).ToString(CultureInfo.InvariantCulture);

    [GeneratedRegex(@"\A\s*(?:(?<size>[0-9]{1,12}(?:\.[0-9]{1,6})?)\s+)?(?<name>[A-Za-z][A-Za-z0-9 -]{0,39}?)\s*(?:/\s*(?<period>[A-Za-z]{1,10})\s*)?\z")]
    private static partial Regex Grammar();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
