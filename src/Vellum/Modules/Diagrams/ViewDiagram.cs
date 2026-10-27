using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Vellum.Modules.Modelling;
using Vellum.Modules.Promises;
using Vellum.Modules.Views;

namespace Vellum.Modules.Diagrams;

/// <summary>One element of a view, with the box it has on the canvas.</summary>
public sealed record DiagramElement(ElementState Element, double X, double Y, double Width, double Height, int ChildCount, bool LaidOut);

/// <summary>A side of a card where a relationship attaches.</summary>
public enum CardSide { Left, Right, Top, Bottom }

/// <summary>The saved sides of one relationship in a view. A null end is auto: it faces the other card.</summary>
public sealed record RelationshipSides(CardSide? Source, CardSide? Target);

/// <summary>
/// One view of a model as the canvas shows it with every card closed: the elements of the view's
/// level, the frame of the element whose inside the level is, the relationships between two shown
/// elements, and the derived lines of the relationships whose ends are hidden inside shown
/// elements (<see cref="Vellum.Modules.Diagrams.DerivedLines"/>), less the lines the view hides
/// (<see cref="LineVisibility"/>). Every list has a fixed order, so the same model and the
/// same layout give the same diagram.
/// </summary>
public sealed record ViewDiagram(
    string ViewName,
    ElementState? Root,
    IReadOnlyList<DiagramElement> Elements,
    IReadOnlyList<RelationshipState> Relationships,
    ModelPin Pin,
    string Layout,
    IReadOnlyDictionary<RelationshipId, RelationshipSides> Sides,
    IReadOnlyList<DerivedLine> DerivedLines,
    IReadOnlyDictionary<RelationshipId, LayoutRoute> Routes,
    IReadOnlyDictionary<(ElementId From, ElementId To), LayoutRoute> LineRoutes)
{
    /// <summary>The height of every card on the canvas.</summary>
    public const double CardHeight = 128;

    /// <summary>The fallback grid of the canvas: three columns, 340 apart, rows 200 apart.</summary>
    public const int GridColumns = 3;
    public const double GridX = 340;
    public const double GridY = 200;

    /// <summary>The card width by kind, as <c>KIND_WIDTHS</c> in the web client.</summary>
    public static double CardWidth(ElementKind kind) => kind switch
    {
        ElementKind.Actor => 248,
        ElementKind.System => 288,
        ElementKind.Component => 248,
        _ => 264,
    };

    /// <summary>
    /// The level of a view is its root element: the children of the root, or the top-level
    /// elements when the view has none. The elements are in ordinal order of name, then of ID.
    /// An element with a saved place stands there. The others stand on a grid of three columns,
    /// 340 apart, with rows 200 apart, in that order:
    /// <list type="bullet">
    /// <item>When no element of the level has a saved place, the grid starts at 0,0 and an
    /// element's cell is its index, as on the canvas of a view that was never arranged.</item>
    /// <item>Otherwise the grid starts at the left edge of the placed cards, 80 below the lowest
    /// of them, so an element without a place never covers one with a place.</item>
    /// </list>
    /// The saved routes of the relationships and of the derived lines come with the diagram; a
    /// writer draws a route only when it fits the boxes of both ends (<see cref="LayoutRoute.Fits"/>).
    /// </summary>
    public static ViewDiagram Project(
        ViewEntity view, IEnumerable<LayoutPositionEntity> positions, PinnedModel model, IEnumerable<LayoutEdgeEntity>? edges = null,
        IEnumerable<LayoutLineEntity>? lines = null, LineVisibility? visibility = null)
    {
        visibility ??= LineVisibility.AllShown;
        var edgeList = edges?.ToList() ?? [];
        var lineList = lines?.ToList() ?? [];
        var state = model.State;
        ElementId? rootId = view.RootElementId is { } id ? new ElementId(id) : null;
        var root = rootId is { } r ? state.Elements.GetValueOrDefault(r) : null;
        var placed = positions.GroupBy(p => p.ElementId).ToDictionary(g => g.Key, g => g.First());

        var childCounts = state.Elements.Values
            .Where(e => e.ParentId is not null)
            .GroupBy(e => e.ParentId!.Value)
            .ToDictionary(g => g.Key, g => g.Count());

        var level = state.Elements.Values
            .Where(e => e.ParentId == rootId)
            .OrderBy(e => e.Name, StringComparer.Ordinal).ThenBy(e => e.Id.Value)
            .ToList();

        var laidOut = level.Where(e => placed.ContainsKey(e.Id.Value)).ToList();
        var (originX, originY) = laidOut.Count == 0
            ? (0.0, 0.0)
            : (laidOut.Min(e => Finite(placed[e.Id.Value].X)), laidOut.Max(e => Finite(placed[e.Id.Value].Y)) + CardHeight + 80);

        var shown = new List<DiagramElement>(level.Count);
        var cell = 0;
        foreach (var e in level)
        {
            double x, y;
            var hasPlace = placed.TryGetValue(e.Id.Value, out var p);
            if (hasPlace)
                (x, y) = (Finite(p!.X), Finite(p.Y));
            else
            {
                (x, y) = (originX + cell % GridColumns * GridX, originY + cell / GridColumns * GridY);
                cell++;
            }
            shown.Add(new DiagramElement(e, x, y, CardWidth(e.Kind), CardHeight, childCounts.GetValueOrDefault(e.Id), hasPlace));
        }

        var ids = shown.Select(e => e.Element.Id).ToHashSet();
        var (direct, derived) = Diagrams.DerivedLines.Project(state.Elements, state.Relationships.Values, ids, visibility);
        var relationships = direct.OrderBy(rel => rel.Id.Value).ToList();

        var sides = new Dictionary<RelationshipId, RelationshipSides>();
        foreach (var edge in edgeList)
        {
            var saved = new RelationshipSides(ParseSide(edge.SourceSide), ParseSide(edge.TargetSide));
            if (saved.Source is not null || saved.Target is not null)
                sides[new RelationshipId(edge.RelationshipId)] = saved;
        }

        var routes = new Dictionary<RelationshipId, LayoutRoute>();
        foreach (var edge in edgeList)
            if (LayoutRoute.Read(edge.RoutePoints) is { } route)
                routes[new RelationshipId(edge.RelationshipId)] = route;
        var lineRoutes = new Dictionary<(ElementId, ElementId), LayoutRoute>();
        foreach (var line in lineList)
            if (LayoutRoute.Read(line.Route) is { } route)
                lineRoutes[(new ElementId(line.FromId), new ElementId(line.ToId))] = route;

        return new ViewDiagram(view.Name, root, shown, relationships, model.Pin, LayoutFingerprint(view, placed.Values, edgeList, lineList, visibility),
            sides, derived, routes, lineRoutes);
    }

    /// <summary>
    /// The layout a diagram was drawn from, which a revision does not pin: <c>sha256:</c> and the
    /// SHA-256 of the view name, the root element, each saved place (element ID, x, y) in order of
    /// element ID, then each relationship with a saved side (<c>side</c>, relationship ID, source
    /// side, target side, an empty word for auto) in order of relationship ID, then each
    /// relationship with a saved route (<c>route</c>, relationship ID, <see cref="LayoutRoute.Text"/>)
    /// in order of relationship ID, then each derived line with a saved route (<c>line</c>, from ID,
    /// to ID, the route) in order of from ID and to ID, then, when the view hides a line,
    /// <c>visibility</c> and <see cref="LineVisibility.Text"/>. A view without a saved side, route or
    /// hidden line has the fingerprint it had before those existed. The same revision and the same
    /// fingerprint give the same bytes.
    /// </summary>
    public static string LayoutFingerprint(
        ViewEntity view, IEnumerable<LayoutPositionEntity> positions, IEnumerable<LayoutEdgeEntity>? edges = null,
        IEnumerable<LayoutLineEntity>? lines = null, LineVisibility? visibility = null)
    {
        var text = new StringBuilder();
        text.Append(view.Name).Append('\n').Append(view.RootElementId?.ToString() ?? "").Append('\n');
        foreach (var p in positions.OrderBy(p => p.ElementId))
            text.Append(p.ElementId).Append(',').Append(DiagramText.Number(p.X)).Append(',').Append(DiagramText.Number(p.Y)).Append('\n');
        foreach (var e in (edges ?? []).Where(e => ParseSide(e.SourceSide) is not null || ParseSide(e.TargetSide) is not null).OrderBy(e => e.RelationshipId))
            text.Append("side,").Append(e.RelationshipId).Append(',')
                .Append(SideWord(ParseSide(e.SourceSide))).Append(',').Append(SideWord(ParseSide(e.TargetSide))).Append('\n');
        foreach (var (id, route) in (edges ?? []).Select(e => (e.RelationshipId, Route: LayoutRoute.Read(e.RoutePoints))).Where(e => e.Route is not null).OrderBy(e => e.RelationshipId))
            text.Append("route,").Append(id).Append(',').Append(route!.Text()).Append('\n');
        foreach (var (from, to, route) in (lines ?? []).Select(l => (l.FromId, l.ToId, Route: LayoutRoute.Read(l.Route))).Where(l => l.Route is not null).OrderBy(l => l.FromId).ThenBy(l => l.ToId))
            text.Append("line,").Append(from).Append(',').Append(to).Append(',').Append(route!.Text()).Append('\n');
        if (visibility is { HidesAnything: true })
            text.Append("visibility,").Append(visibility.Text()).Append('\n');
        return "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    /// <summary>The side a saved word names, or null for auto and for a word that names no side.</summary>
    public static CardSide? ParseSide(string? word) => word switch
    {
        EdgeSides.Top => CardSide.Top,
        EdgeSides.Right => CardSide.Right,
        EdgeSides.Bottom => CardSide.Bottom,
        EdgeSides.Left => CardSide.Left,
        _ => null,
    };

    private static string SideWord(CardSide? side) => side?.ToString().ToLowerInvariant() ?? "";

    private static double Finite(double value) => double.IsFinite(value) ? value : 0;

    /// <summary>The words of an element kind in exports: actor, system, app, store, component.</summary>
    public static string KindWord(ElementKind kind) => kind.ToString().ToLowerInvariant();

    /// <summary>A sentence that names the model a file was made from, for the header of a file.</summary>
    public static string PinText(ModelPin pin) =>
        pin.RevisionId is { } revision
            ? string.Create(CultureInfo.InvariantCulture, $"revision \"{pin.RevisionName}\" ({revision}), stream version {pin.Version}")
            : string.Create(CultureInfo.InvariantCulture, $"stream {pin.StreamId} at version {pin.Version}");
}

/// <summary>Text helpers that every diagram format shares.</summary>
public static class DiagramText
{
    /// <summary>
    /// A number with at most two decimals, in the invariant culture, without a negative zero.
    /// The same double always gives the same text.
    /// </summary>
    public static string Number(double value)
    {
        if (!double.IsFinite(value)) value = 0;
        var rounded = Math.Round(value, 2, MidpointRounding.AwayFromZero);
        if (rounded == 0) rounded = 0; // drops the sign of -0
        return rounded.ToString("0.##", CultureInfo.InvariantCulture);
    }

    /// <summary>The text on one line: each run of line breaks, tabs and other control characters becomes one space.</summary>
    public static string OneLine(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var builder = new StringBuilder(text.Length);
        var space = false;
        foreach (var c in text)
        {
            if (char.IsControl(c) || c is '\u2028' or '\u2029')
            {
                space = true;
                continue;
            }
            if (space && builder.Length > 0) builder.Append(' ');
            space = false;
            builder.Append(c);
        }
        return builder.ToString().Trim();
    }

    /// <summary>
    /// Identifiers for a set of items, unique among themselves and against <paramref name="reserved"/>:
    /// the name in lower case with every run of characters other than a-z and 0-9 as one
    /// underscore. A name that gives nothing becomes <paramref name="fallback"/>, a name that starts
    /// with a digit gets <paramref name="fallback"/> and an underscore in front, a reserved word
    /// gets an underscore at the end, and a second use of an identifier gets _2, then _3. The items
    /// are taken in ordinal order of name, then of ID, so the same names give the same identifiers.
    /// </summary>
    public static IReadOnlyDictionary<Guid, string> Identifiers(
        IEnumerable<(Guid Id, string Name)> items, string fallback, IReadOnlySet<string> reserved, ISet<string>? taken = null)
    {
        taken ??= new HashSet<string>(StringComparer.Ordinal);
        var result = new Dictionary<Guid, string>();
        var next = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (id, name) in items.OrderBy(i => i.Name, StringComparer.Ordinal).ThenBy(i => i.Id))
        {
            var stem = Sanitise(name);
            if (stem.Length == 0) stem = fallback;
            else if (char.IsAsciiDigit(stem[0])) stem = $"{fallback}_{stem}";
            if (reserved.Contains(stem)) stem += "_";
            var candidate = stem;
            // Each stem keeps its next number, so many equal names cost linear time.
            var n = next.GetValueOrDefault(stem, 2);
            while (taken.Contains(candidate) || reserved.Contains(candidate))
                candidate = string.Create(CultureInfo.InvariantCulture, $"{stem}_{n++}");
            next[stem] = n;
            taken.Add(candidate);
            result[id] = candidate;
        }
        return result;
    }

    private static string Sanitise(string? name)
    {
        var builder = new StringBuilder();
        var gap = false;
        foreach (var c in name ?? "")
        {
            if (char.IsAsciiLetterOrDigit(c))
            {
                if (gap && builder.Length > 0) builder.Append('_');
                gap = false;
                builder.Append(char.ToLowerInvariant(c));
            }
            else
                gap = true;
        }
        return builder.ToString();
    }
}
