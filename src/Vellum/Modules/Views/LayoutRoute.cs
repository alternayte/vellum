using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Vellum.Modules.Views;

/// <summary>A point of a route, in the coordinates of the level its two cards are on.</summary>
public readonly record struct RoutePoint(double X, double Y);

/// <summary>The box of a card when the layout ran: its saved place and its size on the canvas.</summary>
public readonly record struct RouteBox(double X, double Y, double Width, double Height);

/// <summary>
/// The path a line takes in a view, from the auto layout (Tidy): the points from the source card
/// to the target card and the centre of its label, in the coordinates of the level the two cards
/// are on. <see cref="From"/> and <see cref="To"/> are the boxes of the two cards when the layout
/// ran. A route holds only while both cards keep those boxes (<see cref="Fits"/>): a card that
/// moves or changes size makes the line fall back to its auto sides. The web client reads and
/// checks a route by the same rules (edge-utils.ts). It is presentation, never architecture.
/// </summary>
public sealed record LayoutRoute(RouteBox From, RouteBox To, IReadOnlyList<RoutePoint> Points, RoutePoint? Label)
{
    /// <summary>The most points a route holds.</summary>
    public const int MaxPoints = 200;

    /// <summary>How far a card may be from the box of a route, in pixels, and the route still holds.</summary>
    public const double Tolerance = 1;

    /// <summary>The words of a refused route, for a validation error.</summary>
    public const string Shape = "must be a route: from and to (x, y, width, height), 2 to 200 points (x, y), and a label (x, y) or null";

    /// <summary>True when both cards have the boxes the route was laid out for.</summary>
    public bool Fits(RouteBox source, RouteBox target) => Same(From, source) && Same(To, target);

    private static bool Same(RouteBox a, RouteBox b) =>
        Math.Abs(a.X - b.X) <= Tolerance && Math.Abs(a.Y - b.Y) <= Tolerance
        && Math.Abs(a.Width - b.Width) <= Tolerance && Math.Abs(a.Height - b.Height) <= Tolerance;

    /// <summary>True for no value: a missing one, or JSON <c>null</c>, which a request body reads as a document.</summary>
    public static bool IsAbsent(JsonDocument? document) => document is null || document.RootElement.ValueKind == JsonValueKind.Null;

    /// <summary>The route in a saved value, or null when the value is not a whole route.</summary>
    public static LayoutRoute? Read(JsonDocument? document) => document is null ? null : Read(document.RootElement);

    /// <summary>The route in a JSON value, or null when the value is not a whole route.</summary>
    public static LayoutRoute? Read(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) return null;
        if (Box(value, "from") is not { } from || Box(value, "to") is not { } to) return null;
        if (!value.TryGetProperty("points", out var points) || points.ValueKind != JsonValueKind.Array) return null;
        var count = points.GetArrayLength();
        if (count is < 2 or > MaxPoints) return null;
        var list = new List<RoutePoint>(count);
        foreach (var item in points.EnumerateArray())
        {
            if (Point(item) is not { } p) return null;
            list.Add(p);
        }
        RoutePoint? label = null;
        if (value.TryGetProperty("label", out var l) && l.ValueKind != JsonValueKind.Null)
        {
            if (Point(l) is not { } at) return null;
            label = at;
        }
        return new LayoutRoute(from, to, list, label);
    }

    /// <summary>
    /// The route as one line of text for a fingerprint: the two boxes, the points and the label,
    /// each number with at most two decimals. The same route always gives the same text.
    /// </summary>
    public string Text()
    {
        var text = new StringBuilder();
        static string N(double v)
        {
            var rounded = Math.Round(v, 2, MidpointRounding.AwayFromZero);
            return (rounded == 0 ? 0 : rounded).ToString("0.##", CultureInfo.InvariantCulture);
        }
        text.Append(N(From.X)).Append(' ').Append(N(From.Y)).Append(' ').Append(N(From.Width)).Append(' ').Append(N(From.Height)).Append(';')
            .Append(N(To.X)).Append(' ').Append(N(To.Y)).Append(' ').Append(N(To.Width)).Append(' ').Append(N(To.Height)).Append(';');
        foreach (var p in Points) text.Append(N(p.X)).Append(' ').Append(N(p.Y)).Append(',');
        text.Append(';');
        if (Label is { } label) text.Append(N(label.X)).Append(' ').Append(N(label.Y));
        return text.ToString();
    }

    private static RoutePoint? Point(JsonElement value) =>
        value.ValueKind == JsonValueKind.Object && Number(value, "x") is { } x && Number(value, "y") is { } y ? new RoutePoint(x, y) : null;

    private static RouteBox? Box(JsonElement value, string name) =>
        value.TryGetProperty(name, out var b) && Point(b) is { } p && Number(b, "width") is { } w && Number(b, "height") is { } h && w > 0 && h > 0
            ? new RouteBox(p.X, p.Y, w, h)
            : null;

    private static double? Number(JsonElement value, string name) =>
        value.TryGetProperty(name, out var n) && n.ValueKind == JsonValueKind.Number && n.TryGetDouble(out var d) && double.IsFinite(d) ? d : null;
}
