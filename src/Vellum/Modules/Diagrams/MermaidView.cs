using System.Text;
using Vellum.Modules.Modelling;

namespace Vellum.Modules.Diagrams;

/// <summary>
/// A view as a Mermaid flowchart (top to bottom). The mapping is fixed:
/// <list type="bullet">
/// <item>The root element of the level is a <c>subgraph</c> around the shown elements.</item>
/// <item>An actor is a stadium <c>([...])</c>, a store a cylinder <c>[(...)]</c>, a component a
/// subroutine <c>[[...]]</c>, a system and an app a rectangle <c>[...]</c>. The label is the name,
/// then the kind and the technology on a second line.</item>
/// <item>A structural relationship is <c>--&gt;</c>, a behavioural one <c>==&gt;</c>; the label
/// and the technology in brackets are the link text.</item>
/// <item>Node IDs come from <see cref="DiagramText.Identifiers"/>.</item>
/// </list>
/// The export is one-way: the header lists what it drops.
/// </summary>
public static class MermaidView
{
    // Words Mermaid reads as syntax where a node ID stands.
    private static readonly HashSet<string> Reserved = new(StringComparer.Ordinal)
    {
        "end", "graph", "flowchart", "subgraph", "direction", "style", "classdef", "class",
        "click", "linkstyle", "call", "href", "callback", "default", "interpolate", "accTitle", "acctitle", "accdescr",
    };

    public const string Dropped =
        "layout and positions, icons, truth labels and their provenance, element status, descriptions, tags, " +
        "owners, line shapes, elements on other levels, messages, contracts (operations, channels, subscriptions, " +
        "bindings), schemas, scenarios, promises, costs and comments";

    public static string Write(ViewDiagram diagram)
    {
        var text = new StringBuilder();
        text.Append("%% Vellum view \"").Append(Comment(diagram.ViewName)).Append("\", Mermaid flowchart.\n");
        text.Append("%% From ").Append(Comment(ViewDiagram.PinText(diagram.Pin))).Append(".\n");
        text.Append("%% One-way export. It drops: ").Append(Dropped).Append(".\n");
        text.Append("flowchart TB\n");

        var taken = new HashSet<string>(StringComparer.Ordinal);
        var ids = DiagramText.Identifiers(diagram.Elements.Select(e => (e.Element.Id.Value, e.Element.Name)), "element", Reserved, taken);

        var indent = "    ";
        if (diagram.Root is { } root && diagram.Elements.Count > 0)
        {
            var frameId = DiagramText.Identifiers([(root.Id.Value, root.Name)], "boundary", Reserved, taken)[root.Id.Value];
            text.Append(indent).Append("subgraph ").Append(frameId).Append(" [\"")
                .Append(Label(root.Name)).Append("<br/>").Append(Label(Kind(root))).Append("\"]\n");
            indent = "        ";
        }

        foreach (var item in diagram.Elements)
        {
            var e = item.Element;
            var (open, close) = e.Kind switch
            {
                ElementKind.Actor => ("([", "])"),
                ElementKind.Store => ("[(", ")]"),
                ElementKind.Component => ("[[", "]]"),
                _ => ("[", "]"),
            };
            text.Append(indent).Append(ids[e.Id.Value]).Append(open).Append('"')
                .Append(Label(e.Name)).Append("<br/>").Append(Label(Kind(e)))
                .Append('"').Append(close).Append('\n');
        }

        if (indent.Length > 4) text.Append("    end\n");

        foreach (var r in diagram.Relationships)
        {
            var arrow = r.Kind == RelationshipKind.Behavioural ? "==>" : "-->";
            var label = DiagramText.OneLine(r.Label);
            var technology = DiagramText.OneLine(r.Technology);
            var linkText = technology.Length == 0 ? label : label.Length == 0 ? $"[{technology}]" : $"{label} [{technology}]";
            text.Append("    ").Append(ids[r.FromId.Value]).Append(' ').Append(arrow);
            if (linkText.Length > 0) text.Append("|\"").Append(Label(linkText)).Append("\"|");
            text.Append(' ').Append(ids[r.ToId.Value]).Append('\n');
        }

        return text.ToString();
    }

    /// <summary>
    /// Text for a comment line: on one line, and with a space after each percent sign, so a name
    /// cannot form <c>%%{</c>, which Mermaid reads as a directive even inside a comment.
    /// </summary>
    private static string Comment(string? text) => DiagramText.OneLine(text).Replace("%", "% ", StringComparison.Ordinal).TrimEnd();

    private static string Kind(ElementState e)
    {
        var technology = DiagramText.OneLine(e.Technology);
        var kind = ViewDiagram.KindWord(e.Kind);
        return technology.Length == 0 ? kind : $"{kind} · {technology}";
    }

    /// <summary>
    /// Text inside a quoted Mermaid label. Every character that Mermaid, its Markdown labels or
    /// the HTML of a label could read as syntax is an entity code (<c>#quot;</c>, <c>#35;</c> …),
    /// so a name can neither end the label nor add markup. The text is on one line.
    /// </summary>
    public static string Label(string? text)
    {
        var builder = new StringBuilder();
        foreach (var c in DiagramText.OneLine(text))
        {
            builder.Append(c switch
            {
                '#' => "#35;",
                '"' => "#quot;",
                '&' => "#amp;",
                '<' => "#lt;",
                '>' => "#gt;",
                '|' => "#124;",
                '`' => "#96;",
                '[' => "#91;",
                ']' => "#93;",
                '(' => "#40;",
                ')' => "#41;",
                '{' => "#123;",
                '}' => "#125;",
                ';' => "#59;",
                '%' => "#37;",
                '\\' => "#92;",
                _ => c.ToString(),
            });
        }
        return builder.ToString();
    }
}
