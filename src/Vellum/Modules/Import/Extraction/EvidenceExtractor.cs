using System.Text;

namespace Vellum.Modules.Import.Extraction;

/// <summary>One family of rules. An extractor reads the accepted files and adds facts and notes.</summary>
internal interface IExtractor
{
    string Id { get; }
    IReadOnlyList<ExtractionRule> Rules { get; }
    void Run(ExtractionContext context);
}

/// <summary>
/// Deterministic evidence extraction: a pure function from file content to facts with exact
/// source lines. It reads text and nothing else. It does not run a build tool, load an
/// assembly, evaluate a template, follow a URL or act on anything the text says.
/// </summary>
public static class EvidenceExtractor
{
    private static readonly IExtractor[] Extractors =
    [
        new OpenApiExtractor(), new AsyncApiExtractor(), new ComposeExtractor(),
        new DotNetExtractor(), new GoExtractor(), new DeploymentExtractor(),
    ];

    /// <summary>Every rule with its version, in a stable order.</summary>
    public static IReadOnlyList<ExtractionRule> Rules { get; } =
        Extractors.SelectMany(e => e.Rules).OrderBy(r => r.Id, StringComparer.Ordinal).ToList();

    /// <summary>
    /// Never throws for any input. The same files give the same result in any input order,
    /// under any culture.
    /// </summary>
    public static ExtractionResult Extract(ExtractionInput input, ExtractionLimits? limits = null)
    {
        limits ??= ExtractionLimits.Default;
        var context = new ExtractionContext(limits);
        try
        {
            context.Accept(input.Files ?? []);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            context.Failed("extractor-failure:intake", "");
        }

        foreach (var extractor in Extractors)
        {
            if (context.Stopped)
                break;
            try
            {
                extractor.Run(context);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // The extractors are written not to throw. This keeps the promise if one does:
                // the facts of the other extractors stay, and the result is marked incomplete.
                context.Failed("extractor-failure:" + extractor.Id, "");
            }
        }

        return context.Result(input.Repository ?? "", input.CommitSha ?? "");
    }
}

/// <summary>A file that passed the path filter and the bounds, with normalised line ends.</summary>
internal sealed class SourceText
{
    private int[]? _lastContentLine;

    public SourceText(string path, string content)
    {
        Path = path;
        Content = content;
        var slash = path.LastIndexOf('/');
        Directory = slash < 0 ? "" : path[..slash];
        Name = slash < 0 ? path : path[(slash + 1)..];
        Extension = PathFilter.Extension(Name).ToLowerInvariant();
    }

    public string Path { get; }
    public string Content { get; }
    /// <summary>The directory of the file, or "" at the repository root.</summary>
    public string Directory { get; }
    public string Name { get; }
    /// <summary>Lower case, with the dot.</summary>
    public string Extension { get; }
    public bool IsYaml => Extension is ".yaml" or ".yml";
    public bool IsJson => Extension == ".json";

    /// <summary>True when the file is in <paramref name="directory"/> or below it.</summary>
    public bool IsUnder(string directory) =>
        directory.Length == 0 || Directory == directory
        || (Directory.Length > directory.Length && Directory[directory.Length] == '/' && Directory.StartsWith(directory, StringComparison.Ordinal));

    /// <summary>
    /// The last line at or before <paramref name="end"/> that is not blank and not a '#'
    /// comment. A YAML node ends where the next one starts, so its range is trimmed to the
    /// lines a reader would call its own.
    /// </summary>
    public int TrimEnd(int start, int end)
    {
        _lastContentLine ??= LastContentLines(Content);
        return Math.Max(start, _lastContentLine[Math.Clamp(end, 0, _lastContentLine.Length - 1)]);
    }

    /// <summary>For each line number, the last line at or before it that has content. Index 0 is 0.</summary>
    private static int[] LastContentLines(string content)
    {
        var result = new List<int> { 0 };
        var from = 0;
        while (from <= content.Length)
        {
            var to = content.IndexOf('\n', from);
            if (to < 0)
                to = content.Length;
            var text = content.AsSpan(from, to - from).Trim();
            var line = result.Count;
            result.Add(text.Length > 0 && text[0] != '#' ? line : result[line - 1]);
            from = to + 1;
        }
        return [.. result];
    }
}

internal sealed class ExtractionContext(ExtractionLimits limits)
{
    private const int MaxKeyLength = 512;
    private const int MaxNameLength = 256;
    private const int MaxValueLength = 2048;

    private sealed class Draft(FactKind kind, string key, string name)
    {
        public FactKind Kind { get; } = kind;
        public string Key { get; } = key;
        public string Name { get; } = name;
        public SortedDictionary<string, string> Attributes { get; } = new(StringComparer.Ordinal);
        public List<SourceEvidence> Evidence { get; } = [];
    }

    private readonly Dictionary<(FactKind, string), Draft> _facts = [];
    private readonly List<ExtractionNote> _notes = [];
    private readonly HashSet<ExtractionNote> _seenNotes = [];
    private readonly Dictionary<string, ParsedDocuments> _documents = new(StringComparer.Ordinal);
    private readonly List<SourceText> _files = [];
    private bool _incomplete;

    public ExtractionLimits Limits { get; } = limits;
    public IReadOnlyList<SourceText> Files => _files;
    /// <summary>True after a bound that ends the extraction. Extractors stop adding when they see it.</summary>
    public bool Stopped { get; private set; }

    public void Note(NoteKind kind, string reason, string path, int line = 0)
    {
        var note = new ExtractionNote(kind, reason, path, line);
        if (!_seenNotes.Add(note))
            return;
        if (_notes.Count < Limits.MaxNotes)
            _notes.Add(note);
        else if (_notes.Count == Limits.MaxNotes)
            _notes.Add(new ExtractionNote(NoteKind.LimitReached, "limit:notes", "", 0));
    }

    public void Failed(string reason, string path, int line = 0)
    {
        _incomplete = true;
        Note(NoteKind.ParseFailure, reason, path, line);
    }

    /// <summary>Records a bound. <paramref name="stop"/> ends the extraction; otherwise one item was left out.</summary>
    public void LimitReached(string limit, string path, bool stop, int line = 0)
    {
        _incomplete = true;
        Stopped |= stop;
        Note(NoteKind.LimitReached, "limit:" + limit, path, line);
    }

    /// <summary>
    /// Path filter, then bounds, then content checks, in path order, so that the files read
    /// do not depend on the order of the input.
    /// </summary>
    public void Accept(IReadOnlyList<SourceFile> input)
    {
        var ordered = input
            .Where(f => f is { Path: not null, Content: not null })
            .OrderBy(f => f.Path, StringComparer.Ordinal).ThenBy(f => f.Content, StringComparer.Ordinal);

        long total = 0;
        string? previous = null;
        foreach (var file in ordered)
        {
            if (file.Path == previous)
            {
                Note(NoteKind.Skipped, "duplicate-path", file.Path);
                continue;
            }
            previous = file.Path;

            if (PathFilter.SkipReason(file.Path, out var notePath) is { } reason)
            {
                Note(NoteKind.Skipped, reason, notePath);
                continue;
            }
            var name = file.Path[(file.Path.LastIndexOf('/') + 1)..];
            if (!PathFilter.IsRelevant(name))
                continue;

            // A char is at least one UTF-8 byte, so the length alone can refuse a huge file.
            long bytes = file.Content.Length > Limits.MaxFileBytes ? file.Content.Length : Encoding.UTF8.GetByteCount(file.Content);
            if (bytes > Limits.MaxFileBytes)
            {
                LimitReached("file-bytes", file.Path, stop: false);
                continue;
            }
            if (_files.Count >= Limits.MaxFiles)
            {
                LimitReached("files", file.Path, stop: false);
                return;
            }
            if (total + bytes > Limits.MaxTotalBytes)
            {
                LimitReached("total-bytes", file.Path, stop: false);
                return;
            }
            if (file.Content.Contains('\0'))
            {
                Note(NoteKind.Skipped, "binary-content", file.Path);
                continue;
            }

            var text = new SourceText(file.Path, TextUtil.Normalize(file.Content));
            if ((text.IsYaml || text.IsJson) && IsKubernetesSecret(text))
            {
                _documents.Remove(text.Path);
                Note(NoteKind.Skipped, "secret-file", text.Path);
                continue;
            }
            total += bytes;
            _files.Add(text);
        }
    }

    /// <summary>
    /// A manifest that declares a Secret in any of its documents is not read at all. The line
    /// check covers a manifest that does not parse.
    /// </summary>
    private bool IsKubernetesSecret(SourceText file)
    {
        if (Parse(file).Roots.Any(root => root.Str("kind") is { } kind && kind.Equals("Secret", StringComparison.Ordinal)))
            return true;
        foreach (var line in file.Content.AsSpan().EnumerateLines())
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("\"kind\"", StringComparison.Ordinal))
                trimmed = trimmed[6..].TrimStart();
            else if (trimmed.StartsWith("kind", StringComparison.Ordinal))
                trimmed = trimmed[4..].TrimStart();
            else
                continue;
            if (trimmed.Length == 0 || trimmed[0] != ':')
                continue;
            var value = trimmed[1..].Trim().TrimEnd(',').Trim().Trim('"').Trim('\'');
            if (value.Equals("Secret", StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    private ParsedDocuments Parse(SourceText file)
    {
        if (!_documents.TryGetValue(file.Path, out var parsed))
        {
            parsed = file.IsJson
                ? StructuredDocument.ParseJson(file.Content, Limits.MaxNesting)
                : StructuredDocument.ParseYaml(file.Content, Limits.MaxNesting);
            _documents[file.Path] = parsed;
        }
        return parsed;
    }

    /// <summary>
    /// The documents of a JSON or YAML file, or an empty list when it does not parse. The
    /// failure is noted once. A template is noted as skipped: it is not YAML until something
    /// evaluates it, and nothing here does.
    /// </summary>
    public IReadOnlyList<DocNode> Documents(SourceText file)
    {
        var parsed = Parse(file);
        if (parsed.Failure is null)
            return parsed.Roots;
        if (parsed.Failure.StartsWith("limit:", StringComparison.Ordinal))
            LimitReached(parsed.Failure["limit:".Length..], file.Path, stop: false, parsed.FailureLine);
        else if (file.IsYaml && file.Content.Contains("{{", StringComparison.Ordinal))
            Note(NoteKind.Skipped, "template-not-evaluated", file.Path);
        else
            Note(NoteKind.ParseFailure, parsed.Failure, file.Path, parsed.FailureLine);
        return [];
    }

    public void Add(FactKind kind, string key, string name, SourceEvidence evidence, params (string Name, string? Value)[] attributes) =>
        Add(kind, key, name, [evidence], attributes);

    /// <summary>
    /// Adds a fact, or merges it into the fact with the same kind and key: the evidence is
    /// joined, a new attribute is added, and a different value for an attribute that is
    /// already there is refused with a note. A key, name or value that looks like a
    /// credential never enters a fact.
    /// </summary>
    public void Add(FactKind kind, string key, string name, IReadOnlyList<SourceEvidence> evidence, IEnumerable<(string Name, string? Value)> attributes)
    {
        if (Stopped || evidence.Count == 0)
            return;
        var at = evidence[0];
        key = TextUtil.FixSurrogates(key);
        name = TextUtil.FixSurrogates(name);
        if (key.Length > MaxKeyLength || name.Length > MaxNameLength)
        {
            Note(NoteKind.Redacted, "value-too-long", at.Path, at.StartLine);
            return;
        }
        if (CredentialGuard.LooksSecret(key) || CredentialGuard.LooksSecret(name))
        {
            Note(NoteKind.Redacted, "credential-like-value", at.Path, at.StartLine);
            return;
        }

        if (!_facts.TryGetValue((kind, key), out var draft))
        {
            if (_facts.Count >= Limits.MaxFacts)
            {
                LimitReached("facts", at.Path, stop: true, at.StartLine);
                return;
            }
            draft = new Draft(kind, key, name);
            _facts[(kind, key)] = draft;
        }

        draft.Evidence.AddRange(evidence);
        foreach (var (attribute, raw) in attributes)
        {
            if (string.IsNullOrEmpty(raw))
                continue;
            var value = TextUtil.FixSurrogates(raw);
            var isBody = attribute is "schema" or "payload";
            if (value.Length > (isBody ? Limits.MaxSchemaBytes : MaxValueLength))
            {
                Note(NoteKind.Redacted, "value-too-long:" + attribute, at.Path, at.StartLine);
                continue;
            }
            if (CredentialGuard.LooksSecret(value))
            {
                Note(NoteKind.Redacted, "credential-like-value:" + attribute, at.Path, at.StartLine);
                continue;
            }
            if (!draft.Attributes.TryGetValue(attribute, out var existing))
                draft.Attributes[attribute] = value;
            else if (!string.Equals(existing, value, StringComparison.Ordinal))
                Note(NoteKind.Conflict, "attribute:" + attribute, at.Path, at.StartLine);
        }
    }

    public ExtractionResult Result(string repository, string commitSha)
    {
        var facts = _facts.Values
            .OrderBy(f => f.Kind.ToString(), StringComparer.Ordinal).ThenBy(f => f.Key, StringComparer.Ordinal)
            .Select(f => new ExtractedFact(f.Kind, f.Key, f.Name, f.Attributes,
                f.Evidence.Distinct()
                    .OrderBy(e => e.Path, StringComparer.Ordinal).ThenBy(e => e.StartLine).ThenBy(e => e.EndLine)
                    .ThenBy(e => e.Rule, StringComparer.Ordinal).ToList()))
            .ToList();
        var notes = _notes
            .OrderBy(n => n.Path, StringComparer.Ordinal).ThenBy(n => n.Line)
            .ThenBy(n => n.Kind).ThenBy(n => n.Reason, StringComparer.Ordinal).ToList();
        return new ExtractionResult(repository, commitSha, facts, notes, !_incomplete);
    }
}

internal static class TextUtil
{
    private const char ByteOrderMark = (char)0xFEFF;
    private const char Replacement = (char)0xFFFD;

    /// <summary>
    /// Removes a byte order mark, makes every line end a line feed and replaces a lone
    /// surrogate, so that line numbers are the ones an editor shows and every later string
    /// is valid UTF-16.
    /// </summary>
    public static string Normalize(string content)
    {
        var start = content.Length > 0 && content[0] == ByteOrderMark ? 1 : 0;
        var clean = start == 0;
        for (var i = start; clean && i < content.Length; i++)
            clean = content[i] != '\r' && !char.IsSurrogate(content[i]);
        if (clean)
            return content;

        var builder = new StringBuilder(content.Length);
        for (var i = start; i < content.Length; i++)
        {
            var c = content[i];
            if (c == '\r')
            {
                builder.Append('\n');
                if (i + 1 < content.Length && content[i + 1] == '\n')
                    i++;
            }
            else if (char.IsHighSurrogate(c) && i + 1 < content.Length && char.IsLowSurrogate(content[i + 1]))
            {
                builder.Append(c).Append(content[++i]);
            }
            else
            {
                builder.Append(char.IsSurrogate(c) ? Replacement : c);
            }
        }
        return builder.ToString();
    }

    public static string FixSurrogates(string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            if (!char.IsSurrogate(value[i]))
                continue;
            if (char.IsHighSurrogate(value[i]) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
            {
                i++;
                continue;
            }
            return string.Create(value.Length, value, static (span, source) =>
            {
                for (var j = 0; j < source.Length; j++)
                {
                    var c = source[j];
                    if (char.IsHighSurrogate(c) && j + 1 < source.Length && char.IsLowSurrogate(source[j + 1]))
                    {
                        span[j] = c;
                        span[j + 1] = source[j + 1];
                        j++;
                    }
                    else
                    {
                        span[j] = char.IsSurrogate(c) ? Replacement : c;
                    }
                }
            });
        }
        return value;
    }

    /// <summary>Lower-case letters and digits, with every other run of characters as one '-'.</summary>
    public static string Slug(string value)
    {
        var builder = new StringBuilder(value.Length);
        var pending = false;
        foreach (var c in value)
        {
            var lower = c is >= 'A' and <= 'Z' ? (char)(c + 32) : c;
            if (lower is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                if (pending && builder.Length > 0)
                    builder.Append('-');
                pending = false;
                builder.Append(lower);
            }
            else
            {
                pending = true;
            }
        }
        return builder.ToString();
    }

    public static string Bounded(string value, int max) => value.Length <= max ? value : value[..max];

    public static string Join(IEnumerable<string> values) => string.Join(",", values);

    public static string JoinSorted(IEnumerable<string> values) =>
        string.Join(",", values.Distinct(StringComparer.Ordinal).OrderBy(v => v, StringComparer.Ordinal));
}
