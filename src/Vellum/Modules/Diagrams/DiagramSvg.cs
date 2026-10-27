using System.Globalization;
using System.Text;
using Vellum.Modules.Modelling;
using Vellum.Modules.Views;

namespace Vellum.Modules.Diagrams;

/// <summary>
/// A view as one SVG document that looks like the canvas in its dark theme: the cards, the frame
/// of the level's root, the relationships with their labels, and the derived lines with their
/// count badges. The same diagram always gives the
/// same bytes: the lists are ordered, numbers are written in the invariant culture with two
/// decimals at most, every ID in the file is a fixed word, and nothing holds a time. Fonts are
/// named by family, not embedded, so a viewer without them shows a fallback font.
/// </summary>
public static class DiagramSvg
{
    // The dark theme of the web client (tailwind.css :root), as hex.
    internal const string Background = "#15191e";
    internal const string Card = "#1e232a";
    internal const string Foreground = "#f4f5f5";
    internal const string MutedForeground = "#9aa8b7";
    internal const string Border = "#303a45";
    internal const string Muted = "#2b3440";
    internal const string Behavioural = "#2dd4bf";
    private const string Stale = "#f6a823";

    internal const string DisplayFont = "'Space Grotesk', sans-serif";
    internal const string BodyFont = "Inter, sans-serif";
    internal const string MonoFont = "'JetBrains Mono', monospace";

    // Text is not measured on the server. These are average advance widths per character, a
    // little wide, so truncated text fits its box in the fonts above and in common fallbacks.
    internal const double NameChar = 9;
    internal const double BodyChar = 6.7;
    internal const double MonoChar = 6.6;
    internal const double LabelChar = 6.8;

    private const double Padding = 40;
    private const double ParallelOffset = 30;
    private static readonly (double X, double Top, double Bottom) FrameInset = (40, 64, 40);

    /// <summary>What the picture does not show. The file says it in its description.</summary>
    public const string Dropped =
        "provenance of truth labels (a marker shows a label other than declared), element status, " +
        "descriptions beyond two lines, tags, owners, elements on other levels, messages, contracts " +
        "(operations, channels, subscriptions, bindings), schemas, scenarios, promises, costs and comments";

    public static string Write(ViewDiagram diagram)
    {
        var body = new StringBuilder();
        var bounds = new Bounds();

        var boxes = diagram.Elements.ToDictionary(e => e.Element.Id, e => new Rect(e.X, e.Y, e.Width, e.Height));
        foreach (var box in boxes.Values) bounds.Add(box);
        var routes = Routed(diagram, boxes);

        if (diagram.Root is { } root && boxes.Count > 0)
        {
            // The frame holds the cards and the routed lines between them with their labels: a
            // route may pass above or beside the cards.
            var frame = FrameRect([.. boxes.Values, .. routes.Areas]);
            bounds.Add(frame);
            WriteFrame(body, root, frame);
        }

        var labels = new StringBuilder();
        WriteRelationships(body, labels, diagram, boxes, routes, bounds);

        foreach (var element in diagram.Elements)
            WriteCard(body, element);

        body.Append(labels);

        if (diagram.Elements.Count == 0)
        {
            bounds.Add(new Rect(0, 0, 320, 80));
            body.Append(CultureInfo.InvariantCulture,
                $"<text x=\"160\" y=\"45\" text-anchor=\"middle\" font-family=\"{BodyFont}\" font-size=\"13\" fill=\"{MutedForeground}\">No elements on this level</text>\n");
        }

        var (minX, minY, maxX, maxY) = bounds.Value;
        minX -= Padding; minY -= Padding; maxX += Padding; maxY += Padding;
        var width = maxX - minX;
        var height = maxY - minY;

        var svg = new StringBuilder();
        svg.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
        svg.Append(CultureInfo.InvariantCulture,
            $"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{N(width)}\" height=\"{N(height)}\" viewBox=\"{N(minX)} {N(minY)} {N(width)} {N(height)}\" font-family=\"{BodyFont}\">\n");
        svg.Append("<title>").Append(Xml(DiagramText.OneLine(diagram.ViewName))).Append("</title>\n");
        svg.Append("<desc>")
            .Append(Xml($"Vellum view \"{DiagramText.OneLine(diagram.ViewName)}\", from {ViewDiagram.PinText(diagram.Pin)}, " +
                        $"with layout {diagram.Layout} (the layout is not part of a revision). " +
                        $"One-way export of a diagram. It does not show: {Dropped}."))
            .Append("</desc>\n");
        svg.Append("<defs>\n");
        Marker(svg, "vellum-arrow-structural", MutedForeground);
        Marker(svg, "vellum-arrow-behavioural", Behavioural);
        // Only a picture with a derived line holds its marker, so the bytes of one without stay as they were.
        if (diagram.DerivedLines.Count > 0) Marker(svg, "vellum-arrow-derived", MutedForeground);
        svg.Append("</defs>\n");
        svg.Append(CultureInfo.InvariantCulture,
            $"<rect x=\"{N(minX)}\" y=\"{N(minY)}\" width=\"{N(width)}\" height=\"{N(height)}\" fill=\"{Background}\"/>\n");
        svg.Append(body);
        svg.Append("</svg>\n");
        return svg.ToString();
    }

    private static void Marker(StringBuilder svg, string id, string color) =>
        // The closed arrow of React Flow (MarkerType.ArrowClosed), sized by the stroke width.
        svg.Append(CultureInfo.InvariantCulture,
            $"<marker id=\"{id}\" markerWidth=\"12.5\" markerHeight=\"12.5\" viewBox=\"-10 -10 20 20\" markerUnits=\"strokeWidth\" orient=\"auto-start-reverse\" refX=\"0\" refY=\"0\">" +
            $"<polyline points=\"-5,-4 0,0 -5,4 -5,-4\" fill=\"{color}\" stroke=\"{color}\" stroke-width=\"1\" stroke-linecap=\"round\" stroke-linejoin=\"round\"/></marker>\n");

    private static Rect FrameRect(IEnumerable<Rect> boxes)
    {
        var b = new Bounds();
        foreach (var box in boxes) b.Add(box);
        var (minX, minY, maxX, maxY) = b.Value;
        return new Rect(minX - FrameInset.X, minY - FrameInset.Top,
            maxX - minX + FrameInset.X * 2, maxY - minY + FrameInset.Top + FrameInset.Bottom);
    }

    private static void WriteFrame(StringBuilder svg, ElementState root, Rect frame)
    {
        var color = ElementColor(root);
        svg.Append(CultureInfo.InvariantCulture,
            $"<g class=\"frame\"><rect x=\"{N(frame.X)}\" y=\"{N(frame.Y)}\" width=\"{N(frame.Width)}\" height=\"{N(frame.Height)}\" rx=\"14.4\" fill=\"{color}\" fill-opacity=\"0.04\" stroke=\"{color}\" stroke-opacity=\"0.55\" stroke-width=\"1.5\"/>");
        Icon(svg, root, frame.X + 16, frame.Y + 14, 16, color);

        // One text with three runs, so the kind and the technology follow the name as drawn.
        var room = frame.Width - 40 - 16;
        var name = Truncate(DiagramText.OneLine(root.Name), room * 0.6, 8.4);
        var kind = ViewDiagram.KindWord(root.Kind).ToUpperInvariant();
        svg.Append(CultureInfo.InvariantCulture,
            $"<text x=\"{N(frame.X + 40)}\" y=\"{N(frame.Y + 27)}\" font-size=\"11\" fill=\"{MutedForeground}\">" +
            $"<tspan font-family=\"{DisplayFont}\" font-size=\"14\" font-weight=\"600\" fill=\"{Foreground}\">{Xml(name)}</tspan>" +
            $"<tspan dx=\"8\" font-family=\"{MonoFont}\" font-size=\"10\" letter-spacing=\"1\">{kind}</tspan>");
        var technology = DiagramText.OneLine(root.Technology);
        var left = room - Length(name) * 8.4 - 8 - kind.Length * 7 - 8;
        if (technology.Length > 0 && left > 40)
            svg.Append(CultureInfo.InvariantCulture, $"<tspan dx=\"8\">{Xml(Truncate("· " + technology, left, BodyChar))}</tspan>");
        svg.Append("</text>");
        svg.Append("</g>\n");
    }

    private static void WriteCard(StringBuilder svg, DiagramElement item)
    {
        var e = item.Element;
        var (x, y, w, h) = (item.X, item.Y, item.Width, item.Height);
        var color = ElementColor(e);
        var radius = e.Kind == ElementKind.Actor ? 24 : 12;

        svg.Append(CultureInfo.InvariantCulture,
            $"<g class=\"element element-{ViewDiagram.KindWord(e.Kind)}\"><rect x=\"{N(x)}\" y=\"{N(y)}\" width=\"{N(w)}\" height=\"{N(h)}\" rx=\"{radius}\" fill=\"{Card}\" stroke=\"{Border}\"/>");
        svg.Append(CultureInfo.InvariantCulture,
            $"<rect x=\"{N(x + 14)}\" y=\"{N(y + 14)}\" width=\"32\" height=\"32\" rx=\"8\" fill=\"{color}\" fill-opacity=\"0.14\" stroke=\"{color}\" stroke-opacity=\"0.4\"/>");
        Icon(svg, e, x + 22, y + 22, 16, color);

        // The badges at the top right, from the right: the count of elements inside, the truth
        // label marker, the stale mark, the external chip.
        var right = x + w - 14;
        if (item.ChildCount > 0)
        {
            var count = item.ChildCount.ToString(CultureInfo.InvariantCulture);
            var bw = count.Length * 6 + 12;
            right -= bw;
            svg.Append(CultureInfo.InvariantCulture,
                $"<rect x=\"{N(right)}\" y=\"{N(y + 14)}\" width=\"{N(bw)}\" height=\"16\" rx=\"6\" fill=\"{Muted}\"/>" +
                $"<text x=\"{N(right + bw / 2.0)}\" y=\"{N(y + 25.5)}\" text-anchor=\"middle\" font-family=\"{MonoFont}\" font-size=\"10\" fill=\"{MutedForeground}\">{count}</text>");
            right -= 4;
        }
        if (e.FactStatus != FactStatus.Declared)
        {
            right -= 8;
            svg.Append(CultureInfo.InvariantCulture,
                $"<circle cx=\"{N(right + 4)}\" cy=\"{N(y + 22)}\" r=\"4\" fill=\"{FactColor(e.FactStatus)}\"><title>{Xml(FactLabel(e.FactStatus))}</title></circle>");
            right -= 4;
        }
        if (e.Stale is not null)
        {
            right -= 38;
            svg.Append(CultureInfo.InvariantCulture,
                $"<rect x=\"{N(right)}\" y=\"{N(y + 15)}\" width=\"38\" height=\"14\" rx=\"4\" fill=\"none\" stroke=\"{Stale}\" stroke-dasharray=\"3 2\"/>" +
                $"<text x=\"{N(right + 19)}\" y=\"{N(y + 25)}\" text-anchor=\"middle\" font-family=\"{MonoFont}\" font-size=\"9\" letter-spacing=\"0.9\" fill=\"{Stale}\">STALE</text>");
            right -= 4;
        }
        if (e.IsExternal)
        {
            right -= 58;
            svg.Append(CultureInfo.InvariantCulture,
                $"<rect class=\"external\" x=\"{N(right)}\" y=\"{N(y + 15)}\" width=\"58\" height=\"14\" rx=\"4\" fill=\"{Muted}\" stroke=\"{Border}\"/>" +
                $"<text x=\"{N(right + 29)}\" y=\"{N(y + 25)}\" text-anchor=\"middle\" font-family=\"{MonoFont}\" font-size=\"9\" letter-spacing=\"0.9\" fill=\"{MutedForeground}\">EXTERNAL</text>");
            right -= 4;
        }

        var textX = x + 56;
        var name = Truncate(DiagramText.OneLine(e.Name), right - 4 - textX, NameChar);
        svg.Append(CultureInfo.InvariantCulture,
            $"<text x=\"{N(textX)}\" y=\"{N(y + 27)}\" font-family=\"{DisplayFont}\" font-size=\"15\" font-weight=\"600\" fill=\"{Foreground}\">{Xml(name)}</text>");

        var kind = ViewDiagram.KindWord(e.Kind).ToUpperInvariant();
        var technology = DiagramText.OneLine(e.Technology);
        svg.Append(CultureInfo.InvariantCulture,
            $"<text x=\"{N(textX)}\" y=\"{N(y + 46)}\" font-size=\"11\" fill=\"{MutedForeground}\"><tspan font-family=\"{MonoFont}\" letter-spacing=\"0.55\">{kind}</tspan>");
        if (technology.Length > 0)
        {
            var room = x + w - 14 - textX - kind.Length * (MonoChar + 0.55) - 20;
            svg.Append(CultureInfo.InvariantCulture,
                $"<tspan dx=\"6\" fill-opacity=\"0.6\">·</tspan><tspan dx=\"6\">{Xml(Truncate(technology, room, BodyChar))}</tspan>");
        }
        svg.Append("</text>");

        var lines = Wrap(DiagramText.OneLine(e.Description), w - 28, BodyChar, 2);
        for (var i = 0; i < lines.Count; i++)
        {
            svg.Append(CultureInfo.InvariantCulture,
                $"<text x=\"{N(x + 14)}\" y=\"{N(y + 71 + i * 17.5)}\" font-size=\"12.5\" fill=\"{MutedForeground}\">{Xml(lines[i])}</text>");
        }
        svg.Append("</g>\n");
    }

    /// <summary>The saved routes that a diagram draws, and the area each takes with its label.</summary>
    private sealed record RoutedLines(
        Dictionary<RelationshipId, LayoutRoute> Relationships,
        Dictionary<(ElementId, ElementId), LayoutRoute> Derived,
        List<Rect> Areas);

    /// <summary>
    /// The routes that hold, by the rule of the canvas (routeApplies and routeFits in
    /// edge-utils.ts): a relationship without a saved side and with a curved or stepped line, or a
    /// derived line, whose route has the boxes both its ends have now. Any other line falls back
    /// to its auto sides.
    /// </summary>
    private static RoutedLines Routed(ViewDiagram diagram, Dictionary<ElementId, Rect> boxes)
    {
        var result = new RoutedLines([], [], []);
        static RouteBox Box(Rect r) => new(r.X, r.Y, r.Width, r.Height);
        foreach (var r in diagram.Relationships)
        {
            if (r.FromId == r.ToId || r.LineShape == "straight" || diagram.Sides.ContainsKey(r.Id)) continue;
            if (!diagram.Routes.TryGetValue(r.Id, out var route) || !route.Fits(Box(boxes[r.FromId]), Box(boxes[r.ToId]))) continue;
            result.Relationships[r.Id] = route;
            result.Areas.Add(Hull([.. route.Points.Select(p => (p.X, p.Y))]));
            if (LabelSize(r) is { } size && route.Label is { } at)
                result.Areas.Add(new Rect(at.X - size.Width / 2, at.Y - size.Height / 2, size.Width, size.Height));
        }
        foreach (var line in diagram.DerivedLines)
        {
            if (!diagram.LineRoutes.TryGetValue((line.FromId, line.ToId), out var route) || !route.Fits(Box(boxes[line.FromId]), Box(boxes[line.ToId]))) continue;
            result.Derived[(line.FromId, line.ToId)] = route;
            result.Areas.Add(Hull([.. route.Points.Select(p => (p.X, p.Y))]));
            if (route.Label is { } at)
            {
                var size = DerivedLabelSize(line);
                result.Areas.Add(new Rect(at.X - size.Width / 2, at.Y - size.Height / 2, size.Width, size.Height));
            }
        }
        return result;
    }

    /// <summary>The middle of a route, for a label the layout gave no place.</summary>
    private static (double X, double Y) Middle(LayoutRoute route)
    {
        var p = route.Points[route.Points.Count / 2];
        var q = route.Points[(route.Points.Count - 1) / 2];
        return ((p.X + q.X) / 2, (p.Y + q.Y) / 2);
    }

    /// <summary>The radius of the corners of a drawn route, as ROUTE_CORNER in edge-utils.ts.</summary>
    private const double RouteCorner = 10;

    /// <summary>
    /// The path of a route: straight runs with rounded corners, a corner's radius at most half of
    /// each run next to it. The same path as routePath in edge-utils.ts.
    /// </summary>
    private static string RoutePath(LayoutRoute route, Bounds bounds)
    {
        var at = route.Points;
        bounds.Add(Hull([.. at.Select(p => (p.X, p.Y))]));
        var path = new StringBuilder($"M{N(at[0].X)},{N(at[0].Y)}");
        for (var i = 1; i < at.Count - 1; i++)
        {
            var (a, b, c) = (at[i - 1], at[i], at[i + 1]);
            var before = Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));
            var after = Math.Sqrt((c.X - b.X) * (c.X - b.X) + (c.Y - b.Y) * (c.Y - b.Y));
            var r = Math.Min(RouteCorner, Math.Min(before / 2, after / 2));
            if (r <= 0)
            {
                path.Append(CultureInfo.InvariantCulture, $" L{N(b.X)},{N(b.Y)}");
                continue;
            }
            var (x1, y1) = (b.X + (a.X - b.X) / before * r, b.Y + (a.Y - b.Y) / before * r);
            var (x2, y2) = (b.X + (c.X - b.X) / after * r, b.Y + (c.Y - b.Y) / after * r);
            path.Append(CultureInfo.InvariantCulture, $" L{N(x1)},{N(y1)} Q{N(b.X)},{N(b.Y)} {N(x2)},{N(y2)}");
        }
        path.Append(CultureInfo.InvariantCulture, $" L{N(at[^1].X)},{N(at[^1].Y)}");
        return path.ToString();
    }

    private static void WriteRelationships(
        StringBuilder svg, StringBuilder labels, ViewDiagram diagram, Dictionary<ElementId, Rect> boxes, RoutedLines routed, Bounds bounds)
    {
        // Parallel lines between one pair of elements are pushed apart: the relationships in ID
        // order, then the derived lines in their order, as on the canvas.
        static (Guid, Guid) Pair(ElementId from, ElementId to) =>
            from.Value.CompareTo(to.Value) <= 0 ? (from.Value, to.Value) : (to.Value, from.Value);
        var totals = diagram.Relationships.Select(r => Pair(r.FromId, r.ToId))
            .Concat(diagram.DerivedLines.Select(d => Pair(d.FromId, d.ToId)))
            .GroupBy(p => p).ToDictionary(g => g.Key, g => g.Count());
        var seen = new Dictionary<(Guid, Guid), int>();

        (int Index, int Total, (Guid, Guid) Pair, double Offset) Place(ElementId from, ElementId to)
        {
            var pair = Pair(from, to);
            var total = totals[pair];
            var index = seen.GetValueOrDefault(pair);
            seen[pair] = index + 1;
            return (index, total, pair, total <= 1 ? 0 : (index - (total - 1) / 2.0) * ParallelOffset);
        }

        // The labels of parallel lines move along the path, apart from each other. The canvas
        // moves them less; a picture has no hover to tell two labels apart. The shift is measured
        // in the direction of the pair, so two opposite lines move apart too.
        static (double X, double Y) LabelPoint(Ends ends, double centerX, double centerY, int index, int total, bool forward)
        {
            var labelT = total <= 1 ? 0.5 : Math.Clamp(0.5 + (index - (total - 1) / 2.0) * 0.3, 0.2, 0.8);
            var shift = (labelT - 0.5) * (forward ? 1 : -1);
            return (centerX + (ends.TargetX - ends.SourceX) * shift, centerY + (ends.TargetY - ends.SourceY) * shift);
        }

        foreach (var r in diagram.Relationships)
        {
            var (index, total, pair, offset) = Place(r.FromId, r.ToId);

            var source = boxes[r.FromId];
            var target = boxes[r.ToId];
            if (r.FromId == r.ToId)
                continue; // a loop has no facing sides; the canvas does not draw one either

            var behavioural = r.Kind == RelationshipKind.Behavioural;
            if (routed.Relationships.TryGetValue(r.Id, out var route))
            {
                // The route of the auto layout, its label where the layout made room for it.
                svg.Append(CultureInfo.InvariantCulture,
                    $"<path class=\"relationship\" d=\"{RoutePath(route, bounds)}\" fill=\"none\" stroke=\"{(behavioural ? Behavioural : MutedForeground)}\" stroke-width=\"{(behavioural ? "2" : "1.5")}\" marker-end=\"url(#vellum-arrow-{(behavioural ? "behavioural" : "structural")})\"/>\n");
                var (rx, ry) = route.Label is { } at ? (at.X, at.Y) : Middle(route);
                WriteLabel(labels, r, rx, ry, bounds);
                continue;
            }

            var ends = Attach(source, target, offset, diagram.Sides.GetValueOrDefault(r.Id));
            var (path, centerX, centerY) = r.LineShape switch
            {
                "straight" => Straight(ends),
                "step" => Step(ends, bounds),
                _ => Bezier(ends, bounds),
            };

            svg.Append(CultureInfo.InvariantCulture,
                $"<path class=\"relationship\" d=\"{path}\" fill=\"none\" stroke=\"{(behavioural ? Behavioural : MutedForeground)}\" stroke-width=\"{(behavioural ? "2" : "1.5")}\" marker-end=\"url(#vellum-arrow-{(behavioural ? "behavioural" : "structural")})\"/>\n");

            var (lx, ly) = LabelPoint(ends, centerX, centerY, index, total, r.FromId.Value == pair.Item1);
            WriteLabel(labels, r, lx, ly, bounds);
        }

        // A derived line is lighter than a relationship and carries a count badge. It follows
        // the auto sides and the default curve: it has no saved sides and no line shape.
        foreach (var line in diagram.DerivedLines)
        {
            var (index, total, pair, offset) = Place(line.FromId, line.ToId);
            if (routed.Derived.TryGetValue((line.FromId, line.ToId), out var route))
            {
                svg.Append(CultureInfo.InvariantCulture,
                    $"<path class=\"derived-line\" d=\"{RoutePath(route, bounds)}\" fill=\"none\" stroke=\"{MutedForeground}\" stroke-width=\"1\" stroke-opacity=\"0.7\" marker-end=\"url(#vellum-arrow-derived)\"/>\n");
                var (rx, ry) = route.Label is { } at ? (at.X, at.Y) : Middle(route);
                WriteDerivedLabel(labels, line, rx, ry, bounds);
                continue;
            }
            var ends = Attach(boxes[line.FromId], boxes[line.ToId], offset, null);
            var (path, centerX, centerY) = Bezier(ends, bounds);
            svg.Append(CultureInfo.InvariantCulture,
                $"<path class=\"derived-line\" d=\"{path}\" fill=\"none\" stroke=\"{MutedForeground}\" stroke-width=\"1\" stroke-opacity=\"0.7\" marker-end=\"url(#vellum-arrow-derived)\"/>\n");
            var (lx, ly) = LabelPoint(ends, centerX, centerY, index, total, line.FromId.Value == pair.Item1);
            WriteDerivedLabel(labels, line, lx, ly, bounds);
        }
    }

    // As on the canvas: a line of one relationship reads its label with no count; a merged line
    // carries the count in a badge and reads "relationships", not "3 3 relationships".
    private static string DerivedText(DerivedLine line) => line.Relationships.Count == 1
        ? Truncate(DiagramText.OneLine(line.Label is { Length: > 0 } one ? one : "relationship"), 240, LabelChar)
        : "relationships";

    private static double DerivedBadgeWidth(DerivedLine line) => line.Relationships.Count == 1
        ? 0
        : Math.Max(16, line.Relationships.Count.ToString(CultureInfo.InvariantCulture).Length * 6.6 + 8);

    private static (double Width, double Height) DerivedLabelSize(DerivedLine line)
    {
        var badgeWidth = DerivedBadgeWidth(line);
        return (6 + (badgeWidth > 0 ? badgeWidth + 6 : 2) + Length(DerivedText(line)) * LabelChar + 6, 24);
    }

    private static (double Width, double Height)? LabelSize(RelationshipState r)
    {
        var label = Truncate(DiagramText.OneLine(r.Label), 240, LabelChar);
        var technology = Truncate(DiagramText.OneLine(r.Technology), 240, 6.2);
        if (label.Length == 0 && technology.Length == 0) return null;
        return (Math.Max(Length(label) * LabelChar, Length(technology) * 6.2) + 20, 8 + (label.Length > 0 ? 16 : 0) + (technology.Length > 0 ? 14 : 0));
    }

    private static void WriteDerivedLabel(StringBuilder svg, DerivedLine line, double cx, double cy, Bounds bounds)
    {
        var count = line.Relationships.Count.ToString(CultureInfo.InvariantCulture);
        var text = DerivedText(line);
        const double badge = 16;
        var badgeWidth = DerivedBadgeWidth(line);
        var (width, height) = DerivedLabelSize(line);
        var x = cx - width / 2;
        var y = cy - height / 2;
        bounds.Add(new Rect(x, y, width, height));

        svg.Append(CultureInfo.InvariantCulture,
            $"<g class=\"derived-line-label\"><rect x=\"{N(x)}\" y=\"{N(y)}\" width=\"{N(width)}\" height=\"{N(height)}\" rx=\"12\" fill=\"{Card}\" fill-opacity=\"0.95\" stroke=\"{Border}\" stroke-dasharray=\"3 2\"/>");
        if (badgeWidth > 0)
        {
            svg.Append(CultureInfo.InvariantCulture,
                $"<rect x=\"{N(x + 6)}\" y=\"{N(cy - badge / 2)}\" width=\"{N(badgeWidth)}\" height=\"{N(badge)}\" rx=\"8\" fill=\"{Muted}\"/>");
            svg.Append(CultureInfo.InvariantCulture,
                $"<text x=\"{N(x + 6 + badgeWidth / 2)}\" y=\"{N(cy + 3.5)}\" text-anchor=\"middle\" font-family=\"{MonoFont}\" font-size=\"10\" fill=\"{Foreground}\">{count}</text>");
        }
        var textX = badgeWidth > 0 ? x + 6 + badgeWidth + 6 : x + 8;
        svg.Append(CultureInfo.InvariantCulture,
            $"<text x=\"{N(textX)}\" y=\"{N(cy + 4)}\" font-size=\"12\" fill=\"{MutedForeground}\">{Xml(text)}</text>");
        svg.Append("</g>\n");
    }

    private static void WriteLabel(StringBuilder svg, RelationshipState r, double cx, double cy, Bounds bounds)
    {
        var label = Truncate(DiagramText.OneLine(r.Label), 240, LabelChar);
        var technology = Truncate(DiagramText.OneLine(r.Technology), 240, 6.2);
        if (LabelSize(r) is not { } size) return;
        var (width, height) = size;
        var x = cx - width / 2;
        var y = cy - height / 2;
        bounds.Add(new Rect(x, y, width, height));

        svg.Append(CultureInfo.InvariantCulture,
            $"<g class=\"relationship-label\"><rect x=\"{N(x)}\" y=\"{N(y)}\" width=\"{N(width)}\" height=\"{N(height)}\" rx=\"6\" fill=\"{Card}\" fill-opacity=\"0.95\" stroke=\"{Border}\"/>");
        var baseline = y + 4;
        if (label.Length > 0)
        {
            baseline += 12.5;
            svg.Append(CultureInfo.InvariantCulture,
                $"<text x=\"{N(cx)}\" y=\"{N(baseline)}\" text-anchor=\"middle\" font-size=\"12\" fill=\"{Foreground}\">{Xml(label)}</text>");
            baseline += 3.5;
        }
        if (technology.Length > 0)
        {
            baseline += 11;
            svg.Append(CultureInfo.InvariantCulture,
                $"<text x=\"{N(cx)}\" y=\"{N(baseline)}\" text-anchor=\"middle\" font-family=\"{MonoFont}\" font-size=\"10\" fill=\"{MutedForeground}\">{Xml(technology)}</text>");
        }
        svg.Append("</g>\n");
    }

    private static void Icon(StringBuilder svg, ElementState e, double x, double y, double size, string color)
    {
        var parts = DiagramIcons.For(e.Icon, e.Kind);
        svg.Append(CultureInfo.InvariantCulture,
            $"<g transform=\"translate({N(x)} {N(y)}) scale({N(size / 24)})\" fill=\"none\" stroke=\"{color}\" stroke-width=\"2\" stroke-linecap=\"round\" stroke-linejoin=\"round\">{parts}</g>");
    }

    // ---- Geometry: the same rules as edge-utils.ts and React Flow's path functions ----

    private readonly record struct Rect(double X, double Y, double Width, double Height);

    private readonly record struct Ends(double SourceX, double SourceY, CardSide SourceSide, double TargetX, double TargetY, CardSide TargetSide);

    /// <summary>
    /// Attaches a relationship to two boxes, as relationshipEnds in edge-utils.ts: a saved side
    /// stays, and an auto end takes the side of its box that faces the other box. The facing
    /// sides are those across the larger gap between the boxes, horizontal on a tie.
    /// </summary>
    private static Ends Attach(Rect source, Rect target, double offset, RelationshipSides? saved)
    {
        var dx = target.X + target.Width / 2 - (source.X + source.Width / 2);
        var dy = target.Y + target.Height / 2 - (source.Y + source.Height / 2);
        var gapX = Math.Abs(dx) - (source.Width + target.Width) / 2;
        var gapY = Math.Abs(dy) - (source.Height + target.Height) / 2;
        var horizontal = gapX >= gapY;

        var sourceSide = saved?.Source ?? (horizontal ? (dx >= 0 ? CardSide.Right : CardSide.Left) : (dy >= 0 ? CardSide.Bottom : CardSide.Top));
        var targetSide = saved?.Target ?? (horizontal ? (dx >= 0 ? CardSide.Left : CardSide.Right) : (dy >= 0 ? CardSide.Top : CardSide.Bottom));
        var (sx, sy) = SidePoint(source, sourceSide, offset);
        var (tx, ty) = SidePoint(target, targetSide, offset);
        return new Ends(sx, sy, sourceSide, tx, ty, targetSide);
    }

    private static (double, double) SidePoint(Rect r, CardSide side, double offset)
    {
        var cx = r.X + r.Width / 2;
        var cy = r.Y + r.Height / 2;
        return side switch
        {
            CardSide.Left => (r.X, cy + offset),
            CardSide.Right => (r.X + r.Width, cy + offset),
            CardSide.Top => (cx + offset, r.Y),
            _ => (cx + offset, r.Y + r.Height),
        };
    }

    // A curve and a step add their points to the bounds: a saved side can route a line around a
    // card, outside the boxes of the cards. A straight line runs between two cards.
    private static (string, double, double) Straight(Ends e) =>
        ($"M{N(e.SourceX)},{N(e.SourceY)} L{N(e.TargetX)},{N(e.TargetY)}", (e.SourceX + e.TargetX) / 2, (e.SourceY + e.TargetY) / 2);

    /// <summary>React Flow's getBezierPath with its default curvature of 0.25.</summary>
    private static (string, double, double) Bezier(Ends e, Bounds bounds)
    {
        var (scx, scy) = Control(e.SourceSide, e.SourceX, e.SourceY, e.TargetX, e.TargetY);
        var (tcx, tcy) = Control(e.TargetSide, e.TargetX, e.TargetY, e.SourceX, e.SourceY);
        // The curve lies inside the hull of its four points.
        bounds.Add(Hull([(e.SourceX, e.SourceY), (scx, scy), (tcx, tcy), (e.TargetX, e.TargetY)]));
        var cx = e.SourceX * 0.125 + scx * 0.375 + tcx * 0.375 + e.TargetX * 0.125;
        var cy = e.SourceY * 0.125 + scy * 0.375 + tcy * 0.375 + e.TargetY * 0.125;
        return ($"M{N(e.SourceX)},{N(e.SourceY)} C{N(scx)},{N(scy)} {N(tcx)},{N(tcy)} {N(e.TargetX)},{N(e.TargetY)}", cx, cy);

        static double Offset(double distance) => distance >= 0 ? 0.5 * distance : 0.25 * 25 * Math.Sqrt(-distance);

        static (double, double) Control(CardSide side, double x1, double y1, double x2, double y2) => side switch
        {
            CardSide.Left => (x1 - Offset(x1 - x2), y1),
            CardSide.Right => (x1 + Offset(x2 - x1), y1),
            CardSide.Top => (x1, y1 - Offset(y1 - y2)),
            _ => (x1, y1 + Offset(y2 - y1)),
        };
    }

    /// <summary>
    /// An orthogonal path with corners of radius 5 through the points of React Flow's
    /// getSmoothStepPath (getPoints in @xyflow/system, offset 20, step in the middle), so the
    /// picture matches the canvas for any pair of sides.
    /// </summary>
    private static (string, double, double) Step(Ends e, Bounds bounds)
    {
        const double gap = 20;
        var source = (X: e.SourceX, Y: e.SourceY);
        var target = (X: e.TargetX, Y: e.TargetY);
        var sourceDir = Direction(e.SourceSide);
        var targetDir = Direction(e.TargetSide);
        var sourceGapped = (X: source.X + sourceDir.X * gap, Y: source.Y + sourceDir.Y * gap);
        var targetGapped = (X: target.X + targetDir.X * gap, Y: target.Y + targetDir.Y * gap);

        // The main axis is the axis of the source side; the direction runs toward the target.
        var alongX = e.SourceSide is CardSide.Left or CardSide.Right;
        double Axis((double X, double Y) p) => alongX ? p.X : p.Y;
        double Cross((double X, double Y) p) => alongX ? p.Y : p.X;
        var currDir = alongX
            ? (sourceGapped.X < targetGapped.X ? 1.0 : -1.0)
            : (sourceGapped.Y < targetGapped.Y ? 1.0 : -1.0);

        List<(double X, double Y)> points;
        double cx, cy;
        var sourceGapOffset = (X: 0.0, Y: 0.0);
        var targetGapOffset = (X: 0.0, Y: 0.0);
        if (Axis(sourceDir) * Axis(targetDir) == -1)
        {
            // Opposite sides: one step in the middle.
            cx = (sourceGapped.X + targetGapped.X) / 2;
            cy = (sourceGapped.Y + targetGapped.Y) / 2;
            List<(double X, double Y)> verticalSplit = [(cx, sourceGapped.Y), (cx, targetGapped.Y)];
            List<(double X, double Y)> horizontalSplit = [(sourceGapped.X, cy), (targetGapped.X, cy)];
            points = Axis(sourceDir) == currDir
                ? (alongX ? verticalSplit : horizontalSplit)
                : (alongX ? horizontalSplit : verticalSplit);
        }
        else
        {
            List<(double X, double Y)> sourceTarget = [(sourceGapped.X, targetGapped.Y)];
            List<(double X, double Y)> targetSource = [(targetGapped.X, sourceGapped.Y)];
            points = alongX
                ? (sourceDir.X == currDir ? targetSource : sourceTarget)
                : (sourceDir.Y == currDir ? sourceTarget : targetSource);

            if (e.SourceSide == e.TargetSide)
            {
                // The same side on both ends, closer than the gap: one gapped end moves back.
                var diff = Math.Abs(Axis(source) - Axis(target));
                if (diff <= gap)
                {
                    var gapOffset = Math.Min(gap - 1, gap - diff);
                    if (Axis(sourceDir) == currDir)
                    {
                        var v = (Axis(sourceGapped) > Axis(source) ? -1 : 1) * gapOffset;
                        sourceGapOffset = alongX ? (v, 0) : (0, v);
                    }
                    else
                    {
                        var v = (Axis(targetGapped) > Axis(target) ? -1 : 1) * gapOffset;
                        targetGapOffset = alongX ? (v, 0) : (0, v);
                    }
                }
            }
            else
            {
                // Sides at a right angle, such as right to bottom.
                var isSameDir = Axis(sourceDir) == Cross(targetDir);
                var sourceGt = Cross(sourceGapped) > Cross(targetGapped);
                var sourceLt = Cross(sourceGapped) < Cross(targetGapped);
                var flip = (Axis(sourceDir) == 1 && ((!isSameDir && sourceGt) || (isSameDir && sourceLt)))
                           || (Axis(sourceDir) != 1 && ((!isSameDir && sourceLt) || (isSameDir && sourceGt)));
                if (flip) points = alongX ? sourceTarget : targetSource;
            }

            var sourceGapPoint = (X: sourceGapped.X + sourceGapOffset.X, Y: sourceGapped.Y + sourceGapOffset.Y);
            var targetGapPoint = (X: targetGapped.X + targetGapOffset.X, Y: targetGapped.Y + targetGapOffset.Y);
            var maxX = Math.Max(Math.Abs(sourceGapPoint.X - points[0].X), Math.Abs(targetGapPoint.X - points[0].X));
            var maxY = Math.Max(Math.Abs(sourceGapPoint.Y - points[0].Y), Math.Abs(targetGapPoint.Y - points[0].Y));
            // The label goes on the longest run.
            (cx, cy) = maxX >= maxY
                ? ((sourceGapPoint.X + targetGapPoint.X) / 2, points[0].Y)
                : (points[0].X, (sourceGapPoint.Y + targetGapPoint.Y) / 2);
        }

        var gappedSource = (X: sourceGapped.X + sourceGapOffset.X, Y: sourceGapped.Y + sourceGapOffset.Y);
        var gappedTarget = (X: targetGapped.X + targetGapOffset.X, Y: targetGapped.Y + targetGapOffset.Y);
        // A gapped end on the straight run from its end to the first corner draws nothing, so it
        // is left out, as the path of two facing sides always was.
        var kept = new List<(double X, double Y)> { source };
        if (gappedSource != points[0] && !Straight3(source, gappedSource, points[0])) kept.Add(gappedSource);
        kept.AddRange(points);
        if (gappedTarget != points[^1] && !Straight3(points[^1], gappedTarget, target)) kept.Add(gappedTarget);
        kept.Add(target);
        bounds.Add(Hull(kept));

        var path = new StringBuilder($"M{N(kept[0].X)},{N(kept[0].Y)}");
        for (var i = 1; i < kept.Count - 1; i++)
        {
            var (a, b, c) = (kept[i - 1], kept[i], kept[i + 1]);
            var r = Math.Min(5, Math.Min(Distance(a, b), Distance(b, c)) / 2);
            if (r <= 0)
            {
                path.Append(CultureInfo.InvariantCulture, $" L{N(b.X)},{N(b.Y)}");
                continue;
            }
            var before = Toward(b, a, r);
            var after = Toward(b, c, r);
            path.Append(CultureInfo.InvariantCulture, $" L{N(before.X)},{N(before.Y)} Q{N(b.X)},{N(b.Y)} {N(after.X)},{N(after.Y)}");
        }
        path.Append(CultureInfo.InvariantCulture, $" L{N(kept[^1].X)},{N(kept[^1].Y)}");
        return (path.ToString(), cx, cy);

        static bool Straight3((double X, double Y) a, (double X, double Y) b, (double X, double Y) c) =>
            (a.X == b.X && b.X == c.X) || (a.Y == b.Y && b.Y == c.Y);

        static (double X, double Y) Direction(CardSide side) => side switch
        {
            CardSide.Left => (-1, 0),
            CardSide.Right => (1, 0),
            CardSide.Top => (0, -1),
            _ => (0, 1),
        };

        static double Distance((double X, double Y) a, (double X, double Y) b) => Math.Abs(a.X - b.X) + Math.Abs(a.Y - b.Y);

        static (double X, double Y) Toward((double X, double Y) from, (double X, double Y) to, double length)
        {
            var d = Distance(from, to);
            return d == 0 ? from : (from.X + (to.X - from.X) / d * length, from.Y + (to.Y - from.Y) / d * length);
        }
    }

    private static Rect Hull(IReadOnlyList<(double X, double Y)> points)
    {
        var (minX, minY) = (points.Min(p => p.X), points.Min(p => p.Y));
        return new Rect(minX, minY, points.Max(p => p.X) - minX, points.Max(p => p.Y) - minY);
    }

    private sealed class Bounds
    {
        private double _minX = double.PositiveInfinity, _minY = double.PositiveInfinity;
        private double _maxX = double.NegativeInfinity, _maxY = double.NegativeInfinity;

        public void Add(Rect r)
        {
            _minX = Math.Min(_minX, r.X);
            _minY = Math.Min(_minY, r.Y);
            _maxX = Math.Max(_maxX, r.X + r.Width);
            _maxY = Math.Max(_maxY, r.Y + r.Height);
        }

        public (double MinX, double MinY, double MaxX, double MaxY) Value =>
            double.IsFinite(_minX) ? (_minX, _minY, _maxX, _maxY) : (0, 0, 0, 0);
    }

    // ---- Text ----

    internal static string N(double value) => DiagramText.Number(value);

    internal static int Length(string text) => new StringInfo(text).LengthInTextElements;

    /// <summary>The text cut to the width with an ellipsis, by whole text elements.</summary>
    internal static string Truncate(string text, double width, double charWidth)
    {
        var info = new StringInfo(text);
        var fits = (int)Math.Floor(width / charWidth);
        if (info.LengthInTextElements <= fits) return text;
        if (fits <= 1) return "…";
        return info.SubstringByTextElements(0, fits - 1).TrimEnd() + "…";
    }

    /// <summary>The text in lines of the width, at most <paramref name="maxLines"/>; the last line ends with an ellipsis when text is left.</summary>
    internal static List<string> Wrap(string text, double width, double charWidth, int maxLines)
    {
        var lines = new List<string>();
        if (text.Length == 0) return lines;
        var perLine = Math.Max(1, (int)Math.Floor(width / charWidth));
        var words = new Queue<string>(text.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        var line = new StringBuilder();
        while (words.Count > 0 && lines.Count < maxLines)
        {
            var word = words.Peek();
            var candidate = line.Length == 0 ? word : line + " " + word;
            if (Length(candidate) <= perLine)
            {
                line.Clear().Append(candidate);
                words.Dequeue();
                continue;
            }
            if (line.Length == 0)
            {
                // A word longer than a line is broken.
                var info = new StringInfo(word);
                line.Append(info.SubstringByTextElements(0, perLine));
                words.Dequeue();
                if (info.LengthInTextElements > perLine)
                    words = new Queue<string>(words.Prepend(info.SubstringByTextElements(perLine)));
            }
            lines.Add(line.ToString());
            line.Clear();
        }
        if (line.Length > 0 && lines.Count < maxLines) lines.Add(line.ToString());
        if (words.Count > 0 && lines.Count > 0)
            lines[^1] = Truncate(lines[^1] + " " + string.Join(' ', words), width, charWidth);
        return lines;
    }

    /// <summary>
    /// Text for XML content and attribute values: the five markup characters as entities, and
    /// every character XML 1.0 does not allow (control characters, U+FFFE, U+FFFF and a
    /// surrogate without its pair) as U+FFFD.
    /// </summary>
    public static string Xml(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var builder = new StringBuilder(text.Length + 16);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            switch (c)
            {
                case '&': builder.Append("&amp;"); break;
                case '<': builder.Append("&lt;"); break;
                case '>': builder.Append("&gt;"); break;
                case '"': builder.Append("&quot;"); break;
                case '\'': builder.Append("&apos;"); break;
                default:
                    if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                    {
                        builder.Append(c).Append(text[++i]);
                    }
                    else if (char.IsSurrogate(c) || c is '\uFFFE' or '\uFFFF' || (c < 0x20 && c is not '\t' and not '\n' and not '\r'))
                    {
                        builder.Append('\uFFFD');
                    }
                    else
                    {
                        builder.Append(c);
                    }
                    break;
            }
        }
        return builder.ToString();
    }

    // ---- Colours of the dark theme ----

    /// <summary>The colour of an element: the colour of its kind, muted for an external system.</summary>
    private static string ElementColor(ElementState e) => e.IsExternal ? MutedForeground : KindColor(e.Kind);

    internal static string KindColor(ElementKind kind) => kind switch
    {
        ElementKind.Actor => "#7d8ca1",
        ElementKind.System => "#639cf8",
        ElementKind.App => "#10b77f",
        ElementKind.Store => "#f59f0a",
        ElementKind.Component => "#9e77f8",
        _ => MutedForeground,
    };

    private static string FactColor(FactStatus status) => status switch
    {
        FactStatus.ObservedInSource => "#2dd4bf",
        FactStatus.Inferred => "#f59e0b",
        FactStatus.Measured => "#22c55e",
        FactStatus.Declared => MutedForeground,
        _ => "#f87171",
    };

    private static string FactLabel(FactStatus status) => status.ToApiString();
}
