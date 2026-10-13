using System.Text;

namespace Vellum.Modules.Import.Extraction;

/// <summary>
/// Reads a Dockerfile in one pass over its lines and gives one fact,
/// <c>deployment:dockerfile:&lt;path&gt;</c>, about the image that the LAST stage builds.
/// <list type="bullet">
/// <item><b>Instructions.</b> A line that ends with the escape character (a backslash, or the
/// one a leading <c># escape=</c> directive names) continues on the next line; comment lines
/// and empty lines inside a continued instruction are skipped. Only <c>FROM</c>,
/// <c>EXPOSE</c>, <c>ENTRYPOINT</c> and <c>CMD</c> are read, and the first line of <c>RUN</c>,
/// <c>COPY</c>, <c>ADD</c> and <c>ONBUILD</c> for heredocs. The text of every other instruction
/// is not even collected: <c>ARG</c>, <c>ENV</c> and <c>LABEL</c> are never read.</item>
/// <item><b>Heredocs.</b> A word of a <c>RUN</c>, <c>COPY</c> or <c>ADD</c> instruction (also
/// after <c>ONBUILD</c>) that starts outside quotes with <c>&lt;&lt;</c> or <c>&lt;&lt;-</c> opens a
/// heredoc; the delimiter can be quoted; one instruction can open several. The lines up to the
/// delimiter line (for <c>&lt;&lt;-</c>, after its leading tabs) are the body and are never
/// read as instructions. A heredoc that does not end gives no fact and an
/// <c>unterminated-heredoc</c> note.</item>
/// <item><b>Stages.</b> <c>FROM image [AS name]</c> starts a stage. An image that is the name of
/// an earlier stage (compared without case; the latest stage with the name) makes that stage the
/// parent. A stage LINKS to its parent and copies nothing, and names are looked up in a
/// dictionary, so the reader is linear in the size of the file. At most
/// <see cref="MaxStages"/> stages are followed: a file with more gives no fact and a
/// <c>limit:dockerfile-stages</c> note, because its last stage is then not known.</item>
/// <item><b>The last stage.</b> Walking from the last stage up its parents:
/// <c>baseImage</c> is the image of the first stage of the chain; <c>ports</c> are the
/// <c>EXPOSE</c> items of the chain from the first stage down, each once (digits, an optional
/// range and <c>/protocol</c>; another item gives an <c>invalid-port</c> note);
/// <c>entrypoint</c> is the nearest <c>ENTRYPOINT</c>; <c>command</c> is the nearest
/// <c>CMD</c>, unless a stage nearer than it, or the same stage, sets an <c>ENTRYPOINT</c>
/// without a <c>CMD</c> of its own: as in Docker, an <c>ENTRYPOINT</c> resets the command a
/// stage inherits, and keeps a <c>CMD</c> that its own stage sets. <c>stageImages</c> are the
/// images of all stages that are not stage names.</item>
/// <item><b>Values.</b> <c>entrypoint</c> and <c>command</c> hold the executable and the flag
/// names only (<see cref="CommandLine"/>), for the JSON-array form and the shell form. When
/// the chain has an entrypoint, the command is its argument list. An image is kept as written
/// when it is a safe reference (<see cref="ImageReference.Checked"/>): one with user
/// information is left out with a note, one with a variable is kept and noted.</item>
/// </list>
/// The evidence is every <c>FROM</c> line and the <c>EXPOSE</c>, <c>ENTRYPOINT</c> and
/// <c>CMD</c> lines that give the attributes.
/// Not recognised: the value of any variable; <c>FROM</c> by stage number; a base image's own
/// entrypoint, command and ports; <c>ONBUILD</c> triggers; <c>--platform</c> and other flags of
/// <c>FROM</c>; <c>HEALTHCHECK</c>, <c>USER</c>, <c>VOLUME</c>, <c>WORKDIR</c>, <c>SHELL</c>.
/// </summary>
internal static class DockerfileReader
{
    /// <summary>Stages of one file that are followed. A file with more gives no fact.</summary>
    internal const int MaxStages = 256;

    private sealed class Stage(string? image, int parent, SourceEvidence from)
    {
        public string? Image { get; } = image;
        /// <summary>The index of the stage this one starts from, or -1 when it starts from an image.</summary>
        public int Parent { get; } = parent;
        public SourceEvidence From { get; } = from;
        /// <summary>The ports of the EXPOSE instructions of this stage only.</summary>
        public List<(string Port, SourceEvidence At)>? Ports { get; set; }
        public (string Text, SourceEvidence At)? Entrypoint { get; set; }
        public (string Text, SourceEvidence At)? Command { get; set; }
    }

    public static void Read(ExtractionContext context, SourceText file, ExtractionRule rule)
    {
        var content = file.Content;
        var meter = context.Meter(file);
        if (!meter.Charge(content.Length))
            return;

        var stages = new List<Stage>();
        var names = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var maxPorts = context.Limits.MaxListItems + 1;
        var text = new StringBuilder();
        var position = 0;
        var line = 0;
        var escape = '\\';
        var directives = true;

        // The next physical line, or false at the end of the content.
        bool Next(out ReadOnlySpan<char> raw)
        {
            if (position > content.Length)
            {
                raw = default;
                return false;
            }
            var end = content.IndexOf('\n', position);
            if (end < 0)
                end = content.Length;
            raw = content.AsSpan(position, end - position);
            position = end + 1;
            line++;
            return true;
        }

        while (Next(out var raw))
        {
            if (!meter.Charge())
                return;
            var trimmed = raw.Trim();
            if (trimmed.IsEmpty)
            {
                directives = false;
                continue;
            }
            if (trimmed[0] == '#')
            {
                // "# escape=`" before the first instruction changes the continuation character.
                if (directives && Directive(trimmed, "escape") is { Length: 1 } value)
                    escape = value[0];
                else if (directives && trimmed.IndexOf('=') < 0)
                    directives = false;
                continue;
            }
            directives = false;

            var start = line;
            var space = trimmed.IndexOfAny(' ', '\t');
            var word = space < 0 ? trimmed : trimmed[..space];
            var instruction =
                word.Equals("FROM", StringComparison.OrdinalIgnoreCase) ? "FROM"
                : word.Equals("EXPOSE", StringComparison.OrdinalIgnoreCase) ? "EXPOSE"
                : word.Equals("ENTRYPOINT", StringComparison.OrdinalIgnoreCase) ? "ENTRYPOINT"
                : word.Equals("CMD", StringComparison.OrdinalIgnoreCase) ? "CMD"
                : word.Equals("RUN", StringComparison.OrdinalIgnoreCase) || word.Equals("COPY", StringComparison.OrdinalIgnoreCase)
                    || word.Equals("ADD", StringComparison.OrdinalIgnoreCase) || word.Equals("ONBUILD", StringComparison.OrdinalIgnoreCase) ? "HEREDOC"
                : null;

            // The whole instruction: this line and the lines it continues on. The text of an
            // instruction that is not read is not collected.
            text.Clear();
            var firstLine = true;
            while (true)
            {
                var continued = trimmed[^1] == escape;
                var body = continued ? trimmed[..^1] : trimmed;
                if (firstLine)
                    body = space < 0 || space >= body.Length ? default : body[(space + 1)..];
                firstLine = false;
                if (instruction is not null)
                    text.Append(body).Append(' ');
                if (!continued)
                    break;
                bool more;
                do
                {
                    more = Next(out raw);
                    trimmed = raw.Trim();
                    if (more && !meter.Charge())
                        return;
                }
                while (more && (trimmed.IsEmpty || trimmed[0] == '#'));
                if (!more)
                    break;
            }
            if (instruction is null)
                continue;
            if (!meter.Charge(text.Length))
                return;
            var rest = text.ToString().Trim();
            var at = rule.At(file.Path, start, line);

            switch (instruction)
            {
                case "HEREDOC":
                    foreach (var (delimiter, stripTabs) in Heredocs(rest))
                    {
                        var closed = false;
                        while (!closed && Next(out raw))
                        {
                            if (!meter.Charge())
                                return;
                            closed = (stripTabs ? raw.TrimStart('\t') : raw).SequenceEqual(delimiter);
                        }
                        if (!closed)
                        {
                            context.Note(NoteKind.Undecided, "unterminated-heredoc", file.Path, start);
                            return;
                        }
                    }
                    break;
                case "FROM":
                {
                    var words = rest.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                    var first = Array.FindIndex(words, w => !w.StartsWith("--", StringComparison.Ordinal));
                    if (first < 0)
                    {
                        context.Note(NoteKind.Undecided, "invalid-from", file.Path, start);
                        return;
                    }
                    if (stages.Count >= MaxStages)
                    {
                        context.LimitReached("dockerfile-stages", file.Path, stop: false, start);
                        return;
                    }
                    var parent = names.GetValueOrDefault(words[first], -1);
                    stages.Add(new Stage(parent < 0 ? ImageReference.Checked(context, file, words[first], start) : null, parent, at));
                    if (first + 2 < words.Length && words[first + 1].Equals("AS", StringComparison.OrdinalIgnoreCase))
                        names[words[first + 2]] = stages.Count - 1;
                    break;
                }
                case "EXPOSE" when stages.Count > 0:
                {
                    var stage = stages[^1];
                    foreach (var range in rest.AsSpan().SplitAny(' ', '\t'))
                    {
                        var port = rest.AsSpan(range).Trim();
                        if (port.IsEmpty)
                            continue;
                        if (!meter.Charge())
                            return;
                        if (!IsPort(port))
                            context.Note(NoteKind.Undecided, "invalid-port", file.Path, start);
                        else if ((stage.Ports ??= []).Count < maxPorts)
                            stage.Ports.Add((port.ToString(), at));
                    }
                    break;
                }
                case "ENTRYPOINT" when stages.Count > 0:
                    stages[^1].Entrypoint = (rest, at);
                    break;
                case "CMD" when stages.Count > 0:
                    stages[^1].Command = (rest, at);
                    break;
            }
        }
        if (stages.Count == 0)
            return;

        // The chain of the last stage, from the last stage up. A parent has a lower index, so the walk ends.
        var chain = new List<Stage>();
        for (var index = stages.Count - 1; index >= 0; index = stages[index].Parent)
            chain.Add(stages[index]);

        (string Text, SourceEvidence At)? entrypoint = null, command = null;
        var commandDecided = false;
        foreach (var stage in chain)
        {
            if (!commandDecided && stage.Command is not null)
            {
                command = stage.Command;
                commandDecided = true;
            }
            if (stage.Entrypoint is not null)
            {
                entrypoint ??= stage.Entrypoint;
                // The entrypoint resets a command that comes from further up.
                commandDecided = true;
            }
        }

        var evidence = new List<SourceEvidence>(stages.Count + 8);
        foreach (var stage in stages)
            evidence.Add(stage.From);
        var ports = new List<string>();
        var seenPorts = new HashSet<string>(StringComparer.Ordinal);
        for (var index = chain.Count - 1; index >= 0 && ports.Count < maxPorts; index--)
        {
            foreach (var (port, at) in chain[index].Ports ?? [])
            {
                if (ports.Count >= maxPorts)
                    break;
                if (!seenPorts.Add(port))
                    continue;
                ports.Add(port);
                if (ports.Count <= context.Limits.MaxListItems)
                    evidence.Add(at);
            }
        }

        var head = evidence[0];
        var attributes = new List<(string, string?)>
        {
            ("source", "dockerfile"), ("baseImage", chain[^1].Image),
            ("stageImages", context.List(stages.Select(s => s.Image).OfType<string>(), file.Path, head.StartLine, sorted: true)),
            ("ports", context.List(ports, file.Path, head.StartLine)),
        };
        if (entrypoint is { } entry)
        {
            evidence.Add(entry.At);
            attributes.Add(("entrypoint", Reduce(context, file, entry.Text, hasExecutable: true, entry.At.StartLine)));
        }
        if (command is { } run)
        {
            evidence.Add(run.At);
            attributes.Add(("command", Reduce(context, file, run.Text, hasExecutable: entrypoint is null, run.At.StartLine)));
        }
        context.Add(FactKind.Deployment, "deployment:dockerfile:" + file.Path, file.Path, evidence, attributes);
    }

    private static string? Reduce(ExtractionContext context, SourceText file, string text, bool hasExecutable, int line) =>
        CommandLine.ExecForm(text) is { } words
            ? CommandLine.Reduce(words, hasExecutable, context, file.Path, line)
            : CommandLine.ReduceShell(text, hasExecutable, context, file.Path, line);

    /// <summary>The value of "# name=value", or null when the comment is not that directive.</summary>
    private static string? Directive(ReadOnlySpan<char> comment, string name)
    {
        var body = comment[1..].TrimStart();
        if (!body.StartsWith(name, StringComparison.OrdinalIgnoreCase))
            return null;
        body = body[name.Length..].TrimStart();
        return body.Length > 0 && body[0] == '=' ? body[1..].Trim().ToString() : null;
    }

    /// <summary>
    /// The heredocs that an instruction opens, in order: a word that starts outside quotes with
    /// optional digits and <c>&lt;&lt;</c> (and not <c>&lt;&lt;&lt;</c>), then an optional '-',
    /// then the delimiter, which can be quoted.
    /// </summary>
    private static List<(string Delimiter, bool StripTabs)> Heredocs(string text)
    {
        var found = new List<(string, bool)>();
        var quote = '\0';
        var wordStart = true;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quote != '\0')
            {
                if (c == '\\' && quote == '"')
                    i++;
                else if (c == quote)
                    quote = '\0';
                continue;
            }
            if (c is ' ' or '\t')
            {
                wordStart = true;
                continue;
            }
            if (wordStart)
            {
                var at = i;
                while (at < text.Length && char.IsAsciiDigit(text[at]))
                    at++;
                if (at + 1 < text.Length && text[at] == '<' && text[at + 1] == '<' && (at + 2 >= text.Length || text[at + 2] != '<'))
                {
                    at += 2;
                    var strip = at < text.Length && text[at] == '-';
                    if (strip)
                        at++;
                    var delimiter = new StringBuilder();
                    for (; at < text.Length && text[at] is not (' ' or '\t'); at++)
                        if (text[at] is not ('"' or '\''))
                            delimiter.Append(text[at]);
                    if (delimiter.Length > 0)
                        found.Add((delimiter.ToString(), strip));
                    i = at - 1;
                    continue;
                }
            }
            wordStart = false;
            if (c == '\\')
                i++;
            else if (c is '"' or '\'')
                quote = c;
        }
        return found;
    }

    /// <summary><c>80</c>, <c>80/tcp</c>, <c>8000-8010</c>, <c>8000-8010/udp</c>.</summary>
    private static bool IsPort(ReadOnlySpan<char> port)
    {
        var slash = port.IndexOf('/');
        if (slash >= 0)
        {
            var protocol = port[(slash + 1)..];
            if (!(protocol.SequenceEqual("tcp") || protocol.SequenceEqual("udp") || protocol.SequenceEqual("sctp")))
                return false;
            port = port[..slash];
        }
        var dash = port.IndexOf('-');
        return dash < 0 ? IsNumber(port) : IsNumber(port[..dash]) && IsNumber(port[(dash + 1)..]);

        static bool IsNumber(ReadOnlySpan<char> digits) => digits.Length is > 0 and <= 5 && digits.IndexOfAnyExceptInRange('0', '9') < 0;
    }
}
