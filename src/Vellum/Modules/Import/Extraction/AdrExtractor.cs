using System.Globalization;
using System.Text.RegularExpressions;

namespace Vellum.Modules.Import.Extraction;

/// <summary>
/// Architecture decision records in Markdown: the Nygard format (<c>adr-tools</c>) and MADR
/// (versions 2 and 3), in <c>docs/adr/*.md</c> or <c>doc/adr/*.md</c>, and MADR also in
/// <c>docs/decisions/*.md</c>, at any depth of the repository. Rules <c>adr.nygard</c> and
/// <c>adr.madr</c>, version 1. No AI reads them. Each ADR is one <see cref="FactKind.Decision"/>
/// fact, <c>declared</c>, with the key <c>adr:&lt;path&gt;</c>.
/// <para>
/// <b>What is kept.</b> <c>title</c> (the first <c>#</c> heading, without a leading number such
/// as <c>1.</c> or <c>ADR-0001:</c>), <c>status</c>, <c>date</c>, <c>context</c>, <c>decision</c>
/// and <c>consequences</c>, each section as written, and <c>lines.&lt;section&gt;</c> with the
/// lines it was read from. The evidence is the lines from the title to the last line of text.
/// <list type="bullet">
/// <item>Nygard: <c>Date:</c> before the first section; sections <c>## Status</c>, <c>## Context</c>,
/// <c>## Decision</c>, <c>## Consequences</c>.</item>
/// <item>MADR: <c>status</c> and <c>date</c> from the front matter, or from the bullets
/// <c>* Status:</c> and <c>* Date:</c>; context from <c>## Context and Problem Statement</c>;
/// decision from <c>## Decision Outcome</c> up to its first <c>###</c> heading; consequences from
/// <c>### Consequences</c>, or <c>### Positive Consequences</c> and <c>### Negative
/// Consequences</c> together.</item>
/// </list>
/// Considered options, pros and cons, drivers, deciders and links are not kept. A file named
/// <c>README</c>, <c>index</c> or with <c>template</c> in its name is not an ADR. A file with no
/// decision section gives the note <c>adr:format-not-recognised</c>. A section longer than
/// <see cref="MaxSection"/> characters is cut, with a note.
/// </para>
/// </summary>
internal sealed partial class AdrExtractor : IExtractor
{
    public static readonly ExtractionRule Nygard = new("adr.nygard", 1);
    public static readonly ExtractionRule Madr = new("adr.madr", 1);

    /// <summary>The most characters of one section: the bound of a decision's text.</summary>
    public const int MaxSection = 4000;

    public string Id => "adr";
    public IReadOnlyList<ExtractionRule> Rules { get; } = [Madr, Nygard];

    [GeneratedRegex(@"^(?:ADR[- ]?)?\d+\s*[.:)-]\s*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LeadingNumber();

    /// <summary>The directory kind of an ADR path: <c>adr</c> (both formats), <c>decisions</c> (MADR only) or null.</summary>
    public static string? Directory(string directory)
    {
        static bool EndsWith(string path, string tail) =>
            path == tail || path.EndsWith("/" + tail, StringComparison.Ordinal);
        if (EndsWith(directory, "docs/adr") || EndsWith(directory, "doc/adr"))
            return "adr";
        return EndsWith(directory, "docs/decisions") ? "decisions" : null;
    }

    public void Run(ExtractionContext context)
    {
        foreach (var file in context.Files)
        {
            if (context.Stopped)
                return;
            if (file.Extension != ".md" || Directory(file.Directory) is not { } place)
                continue;
            var lower = file.Name.ToLowerInvariant();
            if (lower is "readme.md" or "index.md" || lower.Contains("template", StringComparison.Ordinal))
                continue;
            context.Guarded(Id, file.Path, () => Read(context, file, place));
        }
    }

    private sealed record Section(string Heading, int Level, int StartLine, int EndLine, List<string> Lines);

    private static void Read(ExtractionContext context, SourceText file, string place)
    {
        var meter = context.Meter(file);
        var lines = file.Content.Split('\n');
        if (!meter.Charge(file.Content.Length + lines.Length))
            return;

        // Front matter: "---" on the first line, "key: value" lines, "---".
        var front = new Dictionary<string, (string Value, int Line)>(StringComparer.OrdinalIgnoreCase);
        var first = 0;
        if (lines.Length > 0 && lines[0].Trim() == "---")
        {
            for (var i = 1; i < lines.Length; i++)
            {
                if (lines[i].Trim() == "---")
                {
                    first = i + 1;
                    break;
                }
                var colon = lines[i].IndexOf(':');
                if (colon > 0)
                    front.TryAdd(lines[i][..colon].Trim(), (Unquote(lines[i][(colon + 1)..].Trim()), i + 1));
            }
        }

        string? title = null;
        var titleLine = 0;
        var preamble = new List<(string Text, int Line)>();
        var sections = new List<Section>();
        var lastText = 0;
        var fenced = false;
        for (var i = first; i < lines.Length; i++)
        {
            var line = lines[i];
            var number = i + 1;
            if (line.Trim().Length > 0)
                lastText = number;
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
                fenced = !fenced;
            var level = fenced ? 0 : HeadingLevel(line);
            if (level == 1 && title is null)
            {
                title = line.TrimStart()[1..].Trim();
                titleLine = number;
                continue;
            }
            if (level is 2 or 3 && title is not null)
            {
                if (sections.Count > 0)
                    sections[^1] = sections[^1] with { EndLine = number - 1 };
                sections.Add(new Section(line.TrimStart()[level..].Trim(), level, number, number, []));
                continue;
            }
            if (sections.Count > 0)
                sections[^1].Lines.Add(line);
            else if (title is not null)
                preamble.Add((line, number));
        }
        if (sections.Count > 0)
            sections[^1] = sections[^1] with { EndLine = lines.Length };
        if (title is null || titleLine == 0)
        {
            context.Note(NoteKind.Undecided, "adr:format-not-recognised", file.Path);
            return;
        }

        Section? Find(int level, params string[] headings) =>
            sections.FirstOrDefault(s => s.Level == level && headings.Any(h => s.Heading.Equals(h, StringComparison.OrdinalIgnoreCase)));

        var madrOutcome = Find(2, "Decision Outcome");
        var nygardDecision = Find(2, "Decision");
        var isMadr = madrOutcome is not null || front.ContainsKey("status") || Find(2, "Context and Problem Statement") is not null;
        if (place == "decisions" && !isMadr)
        {
            context.Note(NoteKind.Undecided, "adr:format-not-recognised", file.Path);
            return;
        }

        var attributes = new List<(string Name, string? Value)>();
        string? status, date;
        Section? contextSection, decisionSection;
        List<Section> consequences;
        ExtractionRule rule;

        if (isMadr)
        {
            rule = Madr;
            status = front.TryGetValue("status", out var s) ? s.Value : Bullet(preamble, "Status");
            date = front.TryGetValue("date", out var d) ? d.Value : Bullet(preamble, "Date");
            contextSection = Find(2, "Context and Problem Statement", "Context");
            decisionSection = madrOutcome;
            consequences = [.. new[] { Find(3, "Consequences"), Find(3, "Positive Consequences"), Find(3, "Negative Consequences"), Find(2, "Consequences") }
                .Where(c => c is not null).Select(c => c!)];
        }
        else
        {
            rule = Nygard;
            var statusSection = Find(2, "Status");
            status = statusSection?.Lines.Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
            date = preamble.Select(p => p.Text.Trim()).Where(t => t.StartsWith("Date:", StringComparison.OrdinalIgnoreCase))
                .Select(t => t[5..].Trim()).FirstOrDefault();
            contextSection = Find(2, "Context");
            decisionSection = nygardDecision;
            consequences = Find(2, "Consequences") is { } c ? [c] : [];
            if (statusSection is not null)
                attributes.Add(("lines.status", Lines(statusSection)));
        }

        if (decisionSection is null)
        {
            context.Note(NoteKind.Undecided, "adr:format-not-recognised", file.Path);
            return;
        }

        title = LeadingNumber().Replace(title, "").Trim();
        if (title.Length == 0)
        {
            context.Note(NoteKind.Undecided, "adr:format-not-recognised", file.Path);
            return;
        }

        attributes.Add(("format", isMadr ? "madr" : "nygard"));
        attributes.Add(("title", title));
        attributes.Add(("status", status));
        attributes.Add(("date", date));
        attributes.Add(("context", Body(context, file, contextSection)));
        attributes.Add(("decision", Body(context, file, decisionSection)));
        attributes.Add(("consequences", consequences.Count == 0 ? null
            : string.Join("\n\n", consequences.Select(c => (c.Level == 3 && consequences.Count > 1 ? c.Heading + ":\n" : "") + Text(c)))
                is { Length: > 0 } joined ? Cut(context, file, joined, consequences[0].StartLine) : null));
        attributes.Add(("lines.title", Range(titleLine, titleLine)));
        if (contextSection is not null)
            attributes.Add(("lines.context", Lines(contextSection)));
        attributes.Add(("lines.decision", Lines(decisionSection)));
        if (consequences.Count > 0)
            attributes.Add(("lines.consequences", Range(consequences.Min(c => c.StartLine), consequences.Max(c => LastLine(c)))));

        context.Add(FactKind.Decision, "adr:" + file.Path, title, [rule.At(file.Path, titleLine, Math.Max(titleLine, lastText))], attributes);
    }

    private static int HeadingLevel(string line)
    {
        var trimmed = line.TrimStart();
        var level = 0;
        while (level < trimmed.Length && trimmed[level] == '#')
            level++;
        return level is > 0 and <= 6 && level < trimmed.Length && trimmed[level] == ' ' ? level : 0;
    }

    /// <summary>A bullet <c>* Status: accepted</c> or <c>- Date: 2020-01-01</c> before the first section (MADR 2).</summary>
    private static string? Bullet(List<(string Text, int Line)> preamble, string name)
    {
        foreach (var (text, _) in preamble)
        {
            var trimmed = text.Trim().TrimStart('*', '-').Trim();
            if (trimmed.StartsWith(name + ":", StringComparison.OrdinalIgnoreCase))
                return trimmed[(name.Length + 1)..].Trim() is { Length: > 0 } value ? value : null;
        }
        return null;
    }

    /// <summary>The text of a section without the lines of its subsections (for MADR's outcome).</summary>
    private static string Text(Section section) => string.Join('\n', section.Lines).Trim();

    private static string? Body(ExtractionContext context, SourceText file, Section? section) =>
        section is null || Text(section) is not { Length: > 0 } text ? null : Cut(context, file, text, section.StartLine);

    private static string Cut(ExtractionContext context, SourceText file, string text, int line)
    {
        if (text.Length <= MaxSection)
            return text;
        context.Note(NoteKind.Redacted, "adr:section-cut", file.Path, line);
        return text[..(MaxSection - 1)] + "…";
    }

    /// <summary>The last line of a section that holds text, or its heading line.</summary>
    private static int LastLine(Section section)
    {
        for (var i = section.Lines.Count - 1; i >= 0; i--)
        {
            if (section.Lines[i].Trim().Length > 0)
                return section.StartLine + 1 + i;
        }
        return section.StartLine;
    }

    private static string Lines(Section section) => Range(section.StartLine, LastLine(section));

    private static string Range(int start, int end) => string.Create(CultureInfo.InvariantCulture, $"{start}-{end}");

    private static string Unquote(string value) =>
        value.Length >= 2 && (value[0] == '"' && value[^1] == '"' || value[0] == '\'' && value[^1] == '\'') ? value[1..^1] : value;
}
