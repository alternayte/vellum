using System.Text;

namespace Vellum.Modules.Diagrams;

/// <summary>
/// A scenario as a Mermaid <c>sequenceDiagram</c>, drawn from <see cref="ScenarioSequence"/>:
/// a participant (or an actor) per lifeline, <c>-&gt;&gt;</c> for a request, <c>--&gt;&gt;</c>
/// for a response, <c>-)</c> for a message, <c>alt</c> and <c>par</c> blocks, and notes.
/// Every text is escaped as in <see cref="MermaidView.Label"/>, so a name can neither end a line
/// nor add markup. The export is one-way: the header lists what it drops.
/// </summary>
public static class MermaidSequence
{
    // Words the sequence diagram grammar reads as syntax where a participant ID stands.
    private static readonly HashSet<string> Reserved = new(StringComparer.Ordinal)
    {
        "end", "alt", "else", "opt", "loop", "par", "and", "rect", "critical", "option", "break", "note", "over",
        "left", "right", "of", "participant", "actor", "as", "activate", "deactivate", "autonumber", "box",
        "create", "destroy", "link", "links", "properties", "details", "title", "sequencediagram", "acctitle", "accdescr",
    };

    public static string Write(ScenarioSequence sequence)
    {
        var text = new StringBuilder();
        text.Append("%% Vellum scenario \"").Append(MermaidView.Comment(sequence.ScenarioName)).Append("\", Mermaid sequence diagram.\n");
        text.Append("%% From ").Append(MermaidView.Comment(sequence.SourceText())).Append(".\n");
        text.Append("%% One-way export. It drops: ").Append(ScenarioSequence.Dropped).Append(".\n");
        text.Append("sequenceDiagram\n");

        var ids = DiagramText.Identifiers(sequence.Participants.Select(p => (p.Id, p.Name)), "participant", Reserved);
        var names = sequence.Participants.Select(p => ids[p.Id]).ToList();

        foreach (var p in sequence.Participants)
        {
            var label = p.Kind == SequenceParticipantKind.Channel ? $"{p.Name} ({p.Detail})" : p.Name;
            text.Append("    ").Append(p.Kind == SequenceParticipantKind.Actor ? "actor " : "participant ")
                .Append(ids[p.Id]).Append(" as ").Append(MermaidView.Label(label)).Append('\n');
        }

        Items(text, sequence.Items, names, "    ");
        return text.ToString();
    }

    private static void Items(StringBuilder text, IReadOnlyList<SequenceItem> items, List<string> names, string indent)
    {
        foreach (var item in items)
        {
            switch (item)
            {
                case SequenceMessage m:
                    var arrow = m.Arrow switch
                    {
                        SequenceArrow.Call => "->>",
                        SequenceArrow.Reply => "-->>",
                        _ => "-)",
                    };
                    text.Append(indent).Append(names[m.From]).Append(arrow).Append(names[m.To])
                        .Append(": ").Append(MermaidView.Label(m.Text)).Append('\n');
                    break;

                case SequenceNote n:
                    text.Append(indent).Append("Note over ").Append(names[n.From]);
                    if (n.To != n.From) text.Append(',').Append(names[n.To]);
                    text.Append(": ").Append(MermaidView.Label(n.Text)).Append('\n');
                    break;

                case SequenceBlock b:
                    var (open, next) = b.Kind == SequenceBlockKind.Par ? ("par", "and") : ("alt", "else");
                    for (var i = 0; i < b.Sections.Count; i++)
                    {
                        text.Append(indent).Append(i == 0 ? open : next).Append(' ').Append(MermaidView.Label(b.Sections[i].Label)).Append('\n');
                        Items(text, b.Sections[i].Items, names, indent + "    ");
                    }
                    text.Append(indent).Append("end\n");
                    break;
            }
        }
    }
}
