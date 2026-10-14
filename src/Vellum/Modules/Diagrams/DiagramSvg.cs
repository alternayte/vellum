using System.Globalization;
using System.Text;
using Vellum.Modules.Modelling;

namespace Vellum.Modules.Diagrams;

/// <summary>
/// A view as one SVG document that looks like the canvas in its dark theme: the cards, the frame
/// of the level's root, the relationships with their labels. The same diagram always gives the
/// same bytes: the lists are ordered, numbers are written in the invariant culture with two
/// decimals at most, every ID in the file is a fixed word, and nothing holds a time. Fonts are
/// named by family, not embedded, so a viewer without them shows a fallback font.
/// </summary>
public static class DiagramSvg
{
    // The dark theme of the web client (tailwind.css :root), as hex.
    private const string Background = "#15191e";
    private const string Card = "#1e232a";
    private const string Foreground = "#f4f5f5";
    private const string MutedForeground = "#9aa8b7";
    private const string Border = "#303a45";
    private const string Muted = "#2b3440";
    private const string Behavioural = "#2dd4bf";
    private const string Stale = "#f6a823";

    private const string DisplayFont = "'Space Grotesk', sans-serif";
    private const string BodyFont = "Inter, sans-serif";
    private const string MonoFont = "'JetBrains Mono', monospace";

    // Text is not measured on the server. These are average advance widths per character, a
    // little wide, so truncated text fits its box in the fonts above and in common fallbacks.
    private const double NameChar = 9;
    private const double BodyChar = 6.7;
    private const double MonoChar = 6.6;
    private const double LabelChar = 6.8;

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

        if (diagram.Root is { } root && boxes.Count > 0)
        {
            var frame = FrameRect(boxes.Values);
            bounds.Add(frame);
            WriteFrame(body, root, frame);
        }

        var labels = new StringBuilder();
        WriteRelationships(body, labels, diagram, boxes, bounds);

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
        var color = KindColor(root.Kind);
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
        var color = KindColor(e.Kind);
        var radius = e.Kind == ElementKind.Actor ? 24 : 12;

        svg.Append(CultureInfo.InvariantCulture,
            $"<g class=\"element element-{ViewDiagram.KindWord(e.Kind)}\"><rect x=\"{N(x)}\" y=\"{N(y)}\" width=\"{N(w)}\" height=\"{N(h)}\" rx=\"{radius}\" fill=\"{Card}\" stroke=\"{Border}\"/>");
        svg.Append(CultureInfo.InvariantCulture,
            $"<rect x=\"{N(x + 14)}\" y=\"{N(y + 14)}\" width=\"32\" height=\"32\" rx=\"8\" fill=\"{color}\" fill-opacity=\"0.14\" stroke=\"{color}\" stroke-opacity=\"0.4\"/>");
        Icon(svg, e, x + 22, y + 22, 16, color);

        // The badges at the top right, from the right: the count of elements inside, the truth
        // label marker, the stale mark.
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

    private static void WriteRelationships(
        StringBuilder svg, StringBuilder labels, ViewDiagram diagram, Dictionary<ElementId, Rect> boxes, Bounds bounds)
    {
        // Parallel relationships between one pair of elements are pushed apart, in ID order.
        static (Guid, Guid) Pair(RelationshipState r) =>
            r.FromId.Value.CompareTo(r.ToId.Value) <= 0 ? (r.FromId.Value, r.ToId.Value) : (r.ToId.Value, r.FromId.Value);
        var totals = diagram.Relationships.GroupBy(Pair).ToDictionary(g => g.Key, g => g.Count());
        var seen = new Dictionary<(Guid, Guid), int>();

        foreach (var r in diagram.Relationships)
        {
            var pair = Pair(r);
            var total = totals[pair];
            var index = seen.GetValueOrDefault(pair);
            seen[pair] = index + 1;
            var offset = total <= 1 ? 0 : (index - (total - 1) / 2.0) * ParallelOffset;

            var source = boxes[r.FromId];
            var target = boxes[r.ToId];
            if (r.FromId == r.ToId)
                continue; // a loop has no facing sides; the canvas does not draw one either

            var ends = Facing(source, target, offset);
            var (path, centerX, centerY) = r.LineShape switch
            {
                "straight" => Straight(ends),
                "step" => Step(ends),
                _ => Bezier(ends),
            };

            var behavioural = r.Kind == RelationshipKind.Behavioural;
            svg.Append(CultureInfo.InvariantCulture,
                $"<path class=\"relationship\" d=\"{path}\" fill=\"none\" stroke=\"{(behavioural ? Behavioural : MutedForeground)}\" stroke-width=\"{(behavioural ? "2" : "1.5")}\" marker-end=\"url(#vellum-arrow-{(behavioural ? "behavioural" : "structural")})\"/>\n");

            // The labels of parallel relationships move along the path, apart from each other.
            // The canvas moves them less; a picture has no hover to tell two labels apart.
            var labelT = total <= 1 ? 0.5 : Math.Clamp(0.5 + (index - (total - 1) / 2.0) * 0.3, 0.2, 0.8);
            // Measured in the direction of the pair, so two opposite relationships move apart too.
            var shift = (labelT - 0.5) * (r.FromId.Value == pair.Item1 ? 1 : -1);
            var lx = centerX + (ends.TargetX - ends.SourceX) * shift;
            var ly = centerY + (ends.TargetY - ends.SourceY) * shift;
            WriteLabel(labels, r, lx, ly, bounds);
        }
    }

    private static void WriteLabel(StringBuilder svg, RelationshipState r, double cx, double cy, Bounds bounds)
    {
        var label = Truncate(DiagramText.OneLine(r.Label), 240, LabelChar);
        var technology = Truncate(DiagramText.OneLine(r.Technology), 240, 6.2);
        if (label.Length == 0 && technology.Length == 0) return;

        var width = Math.Max(Length(label) * LabelChar, Length(technology) * 6.2) + 20;
        var height = 8 + (label.Length > 0 ? 16 : 0) + (technology.Length > 0 ? 14 : 0);
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

    private enum Side { Left, Right, Top, Bottom }

    private readonly record struct Rect(double X, double Y, double Width, double Height);

    private readonly record struct Ends(double SourceX, double SourceY, Side SourceSide, double TargetX, double TargetY, Side TargetSide);

    /// <summary>Attaches a relationship to the sides of two boxes that face each other.</summary>
    private static Ends Facing(Rect source, Rect target, double offset)
    {
        var dx = target.X + target.Width / 2 - (source.X + source.Width / 2);
        var dy = target.Y + target.Height / 2 - (source.Y + source.Height / 2);
        var gapX = Math.Abs(dx) - (source.Width + target.Width) / 2;
        var gapY = Math.Abs(dy) - (source.Height + target.Height) / 2;
        var horizontal = gapX >= gapY;

        var sourceSide = horizontal ? (dx >= 0 ? Side.Right : Side.Left) : (dy >= 0 ? Side.Bottom : Side.Top);
        var targetSide = horizontal ? (dx >= 0 ? Side.Left : Side.Right) : (dy >= 0 ? Side.Top : Side.Bottom);
        var (sx, sy) = SidePoint(source, sourceSide, offset);
        var (tx, ty) = SidePoint(target, targetSide, offset);
        return new Ends(sx, sy, sourceSide, tx, ty, targetSide);
    }

    private static (double, double) SidePoint(Rect r, Side side, double offset)
    {
        var cx = r.X + r.Width / 2;
        var cy = r.Y + r.Height / 2;
        return side switch
        {
            Side.Left => (r.X, cy + offset),
            Side.Right => (r.X + r.Width, cy + offset),
            Side.Top => (cx + offset, r.Y),
            _ => (cx + offset, r.Y + r.Height),
        };
    }

    private static (string, double, double) Straight(Ends e) =>
        ($"M{N(e.SourceX)},{N(e.SourceY)} L{N(e.TargetX)},{N(e.TargetY)}", (e.SourceX + e.TargetX) / 2, (e.SourceY + e.TargetY) / 2);

    /// <summary>React Flow's getBezierPath with its default curvature of 0.25.</summary>
    private static (string, double, double) Bezier(Ends e)
    {
        var (scx, scy) = Control(e.SourceSide, e.SourceX, e.SourceY, e.TargetX, e.TargetY);
        var (tcx, tcy) = Control(e.TargetSide, e.TargetX, e.TargetY, e.SourceX, e.SourceY);
        var cx = e.SourceX * 0.125 + scx * 0.375 + tcx * 0.375 + e.TargetX * 0.125;
        var cy = e.SourceY * 0.125 + scy * 0.375 + tcy * 0.375 + e.TargetY * 0.125;
        return ($"M{N(e.SourceX)},{N(e.SourceY)} C{N(scx)},{N(scy)} {N(tcx)},{N(tcy)} {N(e.TargetX)},{N(e.TargetY)}", cx, cy);

        static double Offset(double distance) => distance >= 0 ? 0.5 * distance : 0.25 * 25 * Math.Sqrt(-distance);

        static (double, double) Control(Side side, double x1, double y1, double x2, double y2) => side switch
        {
            Side.Left => (x1 - Offset(x1 - x2), y1),
            Side.Right => (x1 + Offset(x2 - x1), y1),
            Side.Top => (x1, y1 - Offset(y1 - y2)),
            _ => (x1, y1 + Offset(y2 - y1)),
        };
    }

    /// <summary>An orthogonal path through the middle between the two sides, with corners of radius 5.</summary>
    private static (string, double, double) Step(Ends e)
    {
        var horizontal = e.SourceSide is Side.Left or Side.Right;
        (double X, double Y)[] points;
        double cx, cy;
        if (horizontal)
        {
            var mid = (e.SourceX + e.TargetX) / 2;
            points = [(e.SourceX, e.SourceY), (mid, e.SourceY), (mid, e.TargetY), (e.TargetX, e.TargetY)];
            (cx, cy) = (mid, (e.SourceY + e.TargetY) / 2);
        }
        else
        {
            var mid = (e.SourceY + e.TargetY) / 2;
            points = [(e.SourceX, e.SourceY), (e.SourceX, mid), (e.TargetX, mid), (e.TargetX, e.TargetY)];
            (cx, cy) = ((e.SourceX + e.TargetX) / 2, mid);
        }

        var path = new StringBuilder($"M{N(points[0].X)},{N(points[0].Y)}");
        for (var i = 1; i < points.Length - 1; i++)
        {
            var (a, b, c) = (points[i - 1], points[i], points[i + 1]);
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
        path.Append(CultureInfo.InvariantCulture, $" L{N(points[^1].X)},{N(points[^1].Y)}");
        return (path.ToString(), cx, cy);

        static double Distance((double X, double Y) a, (double X, double Y) b) => Math.Abs(a.X - b.X) + Math.Abs(a.Y - b.Y);

        static (double X, double Y) Toward((double X, double Y) from, (double X, double Y) to, double length)
        {
            var d = Distance(from, to);
            return d == 0 ? from : (from.X + (to.X - from.X) / d * length, from.Y + (to.Y - from.Y) / d * length);
        }
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

    private static string N(double value) => DiagramText.Number(value);

    private static int Length(string text) => new StringInfo(text).LengthInTextElements;

    /// <summary>The text cut to the width with an ellipsis, by whole text elements.</summary>
    private static string Truncate(string text, double width, double charWidth)
    {
        var info = new StringInfo(text);
        var fits = (int)Math.Floor(width / charWidth);
        if (info.LengthInTextElements <= fits) return text;
        if (fits <= 1) return "…";
        return info.SubstringByTextElements(0, fits - 1).TrimEnd() + "…";
    }

    /// <summary>The text in lines of the width, at most <paramref name="maxLines"/>; the last line ends with an ellipsis when text is left.</summary>
    private static List<string> Wrap(string text, double width, double charWidth, int maxLines)
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

    private static string KindColor(ElementKind kind) => kind switch
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
