using System.Globalization;
using System.Text;
using static Vellum.Modules.Diagrams.DiagramSvg;

namespace Vellum.Modules.Diagrams;

/// <summary>
/// A scenario as one SVG sequence diagram, drawn from <see cref="ScenarioSequence"/>, so it
/// shows what the Mermaid export shows: a box and a lifeline per participant, an arrow per
/// request, response and message, a frame per <c>alt</c> and <c>par</c> block, and notes. It
/// uses the colours and fonts of the view SVG (the dark theme of the canvas). The same sequence
/// always gives the same bytes: numbers have at most two decimals, and nothing holds a time.
/// </summary>
public static class SequenceSvg
{
    private const double BoxHeight = 48;
    private const double MinBox = 120;
    private const double MaxBox = 220;
    private const double BoxGap = 40;
    private const double Padding = 40;
    private const double MaxLabel = 320;
    private const double MaxNote = 260;
    private const double SelfLoop = 30;
    private const double FrameInset = 12;
    private const double TabWidth = 40;

    private const string Call = "#d7dde4";

    public static string Write(ScenarioSequence sequence)
    {
        var participants = sequence.Participants;
        var widths = participants.Select(p => Math.Clamp(Length(p.Name) * 8 + 32, MinBox, MaxBox)).ToArray();
        var centers = Columns(sequence, widths, out var rightEdge);

        var frames = new StringBuilder();
        var content = new StringBuilder();
        var extent = new Extent();
        for (var i = 0; i < participants.Count; i++)
            extent.Add(centers[i] - widths[i] / 2, centers[i] + widths[i] / 2);
        if (participants.Count > 0)
            extent.Add(rightEdge, rightEdge);

        var y = BoxHeight + 24;
        Items(sequence.Items, centers, frames, content, extent, ref y);
        var bottom = y + 8;

        var body = new StringBuilder();
        for (var i = 0; i < participants.Count; i++)
        {
            body.Append(CultureInfo.InvariantCulture,
                $"<line class=\"lifeline\" x1=\"{N(centers[i])}\" y1=\"{N(BoxHeight)}\" x2=\"{N(centers[i])}\" y2=\"{N(bottom)}\" stroke=\"{Border}\" stroke-dasharray=\"4 4\"/>\n");
        }
        body.Append(frames);
        for (var i = 0; i < participants.Count; i++)
            Participant(body, participants[i], centers[i], widths[i]);
        body.Append(content);

        double minX, maxX, maxY;
        if (participants.Count == 0)
        {
            (minX, maxX, maxY) = (0, 320, 80);
            body.Append(CultureInfo.InvariantCulture,
                $"<text x=\"160\" y=\"45\" text-anchor=\"middle\" font-family=\"{BodyFont}\" font-size=\"13\" fill=\"{MutedForeground}\">No steps in this scenario</text>\n");
        }
        else
        {
            (minX, maxX, maxY) = (extent.Min, extent.Max, bottom);
        }

        minX -= Padding; maxX += Padding;
        var minY = -Padding;
        maxY += Padding;
        var width = maxX - minX;
        var height = maxY - minY;

        var svg = new StringBuilder();
        svg.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
        svg.Append(CultureInfo.InvariantCulture,
            $"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{N(width)}\" height=\"{N(height)}\" viewBox=\"{N(minX)} {N(minY)} {N(width)} {N(height)}\" font-family=\"{BodyFont}\">\n");
        svg.Append("<title>").Append(Xml(DiagramText.OneLine(sequence.ScenarioName))).Append("</title>\n");
        svg.Append("<desc>")
            .Append(Xml($"Vellum scenario \"{DiagramText.OneLine(sequence.ScenarioName)}\", from {sequence.SourceText()}. " +
                        $"One-way export of a scenario as a sequence diagram. It does not show: {ScenarioSequence.Dropped}."))
            .Append("</desc>\n");
        svg.Append("<defs>\n");
        Marker(svg, "vellum-seq-call", Call, closed: true);
        Marker(svg, "vellum-seq-reply", MutedForeground, closed: false);
        Marker(svg, "vellum-seq-async", Behavioural, closed: false);
        svg.Append("</defs>\n");
        svg.Append(CultureInfo.InvariantCulture,
            $"<rect x=\"{N(minX)}\" y=\"{N(minY)}\" width=\"{N(width)}\" height=\"{N(height)}\" fill=\"{Background}\"/>\n");
        svg.Append(body);
        svg.Append("</svg>\n");
        return svg.ToString();
    }

    // A closed arrow for a request, as on the view SVG; an open arrow for a response and a message.
    private static void Marker(StringBuilder svg, string id, string color, bool closed)
    {
        var (points, fill) = closed ? ("-5,-4 0,0 -5,4 -5,-4", color) : ("-5,-4 0,0 -5,4", "none");
        svg.Append(
            $"<marker id=\"{id}\" markerWidth=\"12.5\" markerHeight=\"12.5\" viewBox=\"-10 -10 20 20\" markerUnits=\"strokeWidth\" orient=\"auto-start-reverse\" refX=\"0\" refY=\"0\">" +
            $"<polyline points=\"{points}\" fill=\"{fill}\" stroke=\"{color}\" stroke-width=\"1\" stroke-linecap=\"round\" stroke-linejoin=\"round\"/></marker>\n");
    }

    // ---- Columns ----

    /// <summary>
    /// The centre of each lifeline. Neighbours stand apart by their boxes and a gap. A message
    /// pushes its right end far enough for its label; a message to itself pushes the next
    /// lifeline, or the right edge, past its loop and label.
    /// </summary>
    private static double[] Columns(ScenarioSequence sequence, double[] widths, out double rightEdge)
    {
        var count = sequence.Participants.Count;
        var apart = new Dictionary<(int, int), double>();
        var self = new double[count];

        void Need(int a, int b, double distance) => apart[(a, b)] = Math.Max(apart.GetValueOrDefault((a, b)), distance);

        void Walk(IReadOnlyList<SequenceItem> items)
        {
            foreach (var item in items)
            {
                switch (item)
                {
                    case SequenceMessage m when m.From == m.To:
                        self[m.From] = Math.Max(self[m.From], SelfLoop + 8 + LabelWidth(m.Text) + 16);
                        break;
                    case SequenceMessage m:
                        Need(Math.Min(m.From, m.To), Math.Max(m.From, m.To), LabelWidth(m.Text) + 40);
                        break;
                    case SequenceBlock b:
                        foreach (var section in b.Sections) Walk(section.Items);
                        break;
                }
            }
        }
        Walk(sequence.Items);

        var centers = new double[count];
        for (var i = 0; i < count; i++)
        {
            if (i == 0)
            {
                centers[0] = widths[0] / 2;
                continue;
            }
            var c = centers[i - 1] + Math.Max(widths[i - 1] / 2 + widths[i] / 2 + BoxGap, self[i - 1]);
            for (var a = 0; a < i; a++)
            {
                if (apart.TryGetValue((a, i), out var distance))
                    c = Math.Max(c, centers[a] + distance);
            }
            centers[i] = c;
        }

        rightEdge = count == 0 ? 0 : centers[^1] + Math.Max(widths[^1] / 2, self[^1]);
        return centers;
    }

    private static double LabelWidth(string text) => Length(Truncate(DiagramText.OneLine(text), MaxLabel, LabelChar)) * LabelChar;

    // ---- Items ----

    private static void Items(IReadOnlyList<SequenceItem> items, double[] centers, StringBuilder frames, StringBuilder content, Extent extent, ref double y)
    {
        foreach (var item in items)
        {
            switch (item)
            {
                case SequenceMessage m:
                    Message(m, centers, content, extent, ref y);
                    break;
                case SequenceNote n:
                    Note(n, centers, content, extent, ref y);
                    break;
                case SequenceBlock b:
                    Block(b, centers, frames, content, extent, ref y);
                    break;
            }
        }
    }

    private static void Message(SequenceMessage m, double[] centers, StringBuilder svg, Extent extent, ref double y)
    {
        var (color, marker, dash) = m.Arrow switch
        {
            SequenceArrow.Call => (Call, "call", ""),
            SequenceArrow.Reply => (MutedForeground, "reply", " stroke-dasharray=\"5 4\""),
            _ => (Behavioural, "async", ""),
        };
        var label = Xml(Truncate(DiagramText.OneLine(m.Text), MaxLabel, LabelChar));
        var from = centers[m.From];
        var to = centers[m.To];

        if (m.From == m.To)
        {
            svg.Append(CultureInfo.InvariantCulture,
                $"<g class=\"message message-{marker}\"><path d=\"M{N(from)},{N(y + 14)} H{N(from + SelfLoop)} V{N(y + 32)} H{N(from + 2)}\" fill=\"none\" stroke=\"{color}\" stroke-width=\"1.5\"{dash} marker-end=\"url(#vellum-seq-{marker})\"/>" +
                $"<text x=\"{N(from + SelfLoop + 8)}\" y=\"{N(y + 27)}\" font-size=\"12\" fill=\"{Foreground}\">{label}</text></g>\n");
            extent.Add(from, from + SelfLoop + 8 + LabelWidth(m.Text));
            y += 46;
            return;
        }

        // The arrow ends just short of the lifeline, so its head stays visible on the line.
        var end = to > from ? to - 2 : to + 2;
        svg.Append(CultureInfo.InvariantCulture,
            $"<g class=\"message message-{marker}\"><text x=\"{N((from + to) / 2)}\" y=\"{N(y + 14)}\" text-anchor=\"middle\" font-size=\"12\" fill=\"{Foreground}\">{label}</text>" +
            $"<line x1=\"{N(from)}\" y1=\"{N(y + 22)}\" x2=\"{N(end)}\" y2=\"{N(y + 22)}\" stroke=\"{color}\" stroke-width=\"1.5\"{dash} marker-end=\"url(#vellum-seq-{marker})\"/></g>\n");
        extent.Add(Math.Min(from, to), Math.Max(from, to));
        y += 36;
    }

    private static void Note(SequenceNote n, double[] centers, StringBuilder svg, Extent extent, ref double y)
    {
        var text = DiagramText.OneLine(n.Text);
        var (left, right) = (centers[Math.Min(n.From, n.To)], centers[Math.Max(n.From, n.To)]);
        var width = left == right
            ? Math.Clamp(Length(text) * BodyChar + 20, 80, MaxNote)
            : right - left + 80;
        var lines = Wrap(text, width - 20, BodyChar, 4);
        if (lines.Count == 0) lines.Add("");
        var height = 10 + lines.Count * 16;
        var x = (left + right) / 2 - width / 2;

        svg.Append(CultureInfo.InvariantCulture,
            $"<g class=\"note\"><rect x=\"{N(x)}\" y=\"{N(y)}\" width=\"{N(width)}\" height=\"{N(height)}\" rx=\"4\" fill=\"{Muted}\" stroke=\"{Border}\"/>");
        for (var i = 0; i < lines.Count; i++)
        {
            svg.Append(CultureInfo.InvariantCulture,
                $"<text x=\"{N(x + 10)}\" y=\"{N(y + 18 + i * 16)}\" font-size=\"12\" fill=\"{Foreground}\">{Xml(lines[i])}</text>");
        }
        svg.Append("</g>\n");
        extent.Add(x, x + width);
        y += height + 10;
    }

    private static void Block(SequenceBlock b, double[] centers, StringBuilder frames, StringBuilder content, Extent extent, ref double y)
    {
        var kind = b.Kind == SequenceBlockKind.Par ? "PAR" : "ALT";
        var top = y;
        var inner = new Extent();
        var separators = new List<(double Y, string Label)>();

        var labels = b.Sections.Select(s => Truncate(DiagramText.OneLine(s.Label), MaxLabel, BodyChar)).ToList();
        y += 30;
        for (var i = 0; i < b.Sections.Count; i++)
        {
            if (i > 0)
            {
                separators.Add((y + 4, labels[i]));
                y += 28;
            }
            Items(b.Sections[i].Items, centers, frames, content, inner, ref y);
        }
        y += 8;
        var bottom = y;
        y += 10;

        var left = inner.Min - FrameInset;
        var right = inner.Max + FrameInset;
        var labelRoom = labels.Select((l, i) => (i == 0 ? TabWidth + 8 : 8) + (Length(l) + 2) * BodyChar + 8).Max();
        right = Math.Max(right, left + labelRoom);
        extent.Add(left, right);

        frames.Append(CultureInfo.InvariantCulture,
            $"<g class=\"block block-{kind.ToLowerInvariant()}\"><rect x=\"{N(left)}\" y=\"{N(top)}\" width=\"{N(right - left)}\" height=\"{N(bottom - top)}\" rx=\"4\" fill=\"none\" stroke=\"{MutedForeground}\" stroke-opacity=\"0.7\"/>" +
            $"<path d=\"M{N(left)},{N(top)} H{N(left + TabWidth)} V{N(top + 12)} L{N(left + TabWidth - 6)},{N(top + 20)} H{N(left)} Z\" fill=\"{Muted}\" stroke=\"{MutedForeground}\" stroke-opacity=\"0.7\"/>" +
            $"<text x=\"{N(left + 8)}\" y=\"{N(top + 14)}\" font-family=\"{MonoFont}\" font-size=\"10\" letter-spacing=\"0.8\" fill=\"{Foreground}\">{kind}</text>" +
            $"<text x=\"{N(left + TabWidth + 8)}\" y=\"{N(top + 14)}\" font-size=\"11\" fill=\"{MutedForeground}\">[{Xml(labels[0])}]</text>");
        foreach (var (at, label) in separators)
        {
            frames.Append(CultureInfo.InvariantCulture,
                $"<line x1=\"{N(left)}\" y1=\"{N(at)}\" x2=\"{N(right)}\" y2=\"{N(at)}\" stroke=\"{MutedForeground}\" stroke-opacity=\"0.7\" stroke-dasharray=\"4 3\"/>" +
                $"<text x=\"{N(left + 8)}\" y=\"{N(at + 15)}\" font-size=\"11\" fill=\"{MutedForeground}\">[{Xml(label)}]</text>");
        }
        frames.Append("</g>\n");
    }

    // ---- Participants ----

    private static void Participant(StringBuilder svg, SequenceParticipant p, double center, double width)
    {
        var (color, radius) = p.Kind switch
        {
            SequenceParticipantKind.Actor => (KindColor(Modelling.ElementKind.Actor), 24),
            SequenceParticipantKind.Channel => (Behavioural, 6),
            SequenceParticipantKind.Element when p.ElementKind is { } kind => (KindColor(kind), 10),
            _ => (MutedForeground, 10),
        };
        var x = center - width / 2;
        var name = Truncate(p.Name, width - 20, 8);
        var detail = DiagramText.OneLine(p.Detail).ToUpperInvariant();
        svg.Append(CultureInfo.InvariantCulture,
            $"<g class=\"participant participant-{p.Kind.ToString().ToLowerInvariant()}\"><rect x=\"{N(x)}\" y=\"0\" width=\"{N(width)}\" height=\"{N(BoxHeight)}\" rx=\"{radius}\" fill=\"{Card}\" stroke=\"{color}\" stroke-opacity=\"0.7\"/>" +
            $"<text x=\"{N(center)}\" y=\"21\" text-anchor=\"middle\" font-family=\"{DisplayFont}\" font-size=\"13\" font-weight=\"600\" fill=\"{Foreground}\">{Xml(name)}</text>" +
            $"<text x=\"{N(center)}\" y=\"37\" text-anchor=\"middle\" font-family=\"{MonoFont}\" font-size=\"9.5\" letter-spacing=\"0.6\" fill=\"{color}\">{Xml(Truncate(detail, width - 20, MonoChar))}</text></g>\n");
    }

    private sealed class Extent
    {
        public double Min { get; private set; } = double.PositiveInfinity;
        public double Max { get; private set; } = double.NegativeInfinity;

        public void Add(double from, double to)
        {
            Min = Math.Min(Min, from);
            Max = Math.Max(Max, to);
        }
    }
}
