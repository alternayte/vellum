using System.Globalization;
using System.Text;

namespace Vellum.Modules.Import.Extraction;

/// <summary>
/// Counts the operations an extractor spends on one file. Every loop over the characters,
/// tokens or nodes of a file charges it. When the count passes the limit the extractor stops
/// that file; the context adds one <c>work-limit</c> note and marks the result incomplete.
/// It is a counter and not a clock, so the stop is at the same place on every machine.
/// </summary>
internal sealed class WorkMeter(ExtractionContext context, string path, long limit)
{
    public long Spent { get; private set; }
    public bool Exhausted { get; private set; }

    /// <summary>False when the budget of the file is spent. The caller stops its work on the file.</summary>
    public bool Charge(long operations = 1)
    {
        if (Exhausted)
            return false;
        Spent += operations;
        if (Spent <= limit)
            return true;
        Exhausted = true;
        context.WorkLimitReached(path);
        return false;
    }
}

internal sealed class ExtractionContext(ExtractionLimits limits)
{
    private const int MaxKeyLength = 512;
    private const int MaxNameLength = 256;
    private const int MaxValueLength = 2048;
    private const int MaxConflictValues = 8;

    /// <summary>
    /// Attributes that hold a list of declared NAMES (identifiers of the source), built with
    /// <see cref="Names"/>. A name such as <c>Token:string</c> is not a secret, so these are not
    /// checked for the "key = value" shapes of a credential. Every other attribute is text.
    /// </summary>
    private static readonly HashSet<string> NameListAttributes = new(StringComparer.Ordinal)
    {
        "fields", "entities", "environment",
    };

    private sealed class Draft(FactKind kind, string key, string name)
    {
        public FactKind Kind { get; } = kind;
        public string Key { get; } = key;
        public string Name { get; } = name;
        /// <summary>Attribute name to each distinct value with the first evidence (in evidence order) that gave it.</summary>
        public SortedDictionary<string, SortedDictionary<string, SourceEvidence>> Attributes { get; } = new(StringComparer.Ordinal);
        public List<SourceEvidence> Evidence { get; } = [];
    }

    private readonly Dictionary<(FactKind, string), Draft> _facts = [];
    private readonly List<ExtractionNote> _notes = [];
    private readonly HashSet<ExtractionNote> _seenNotes = [];
    private readonly Dictionary<string, ParsedDocuments> _documents = new(StringComparer.Ordinal);
    private readonly Dictionary<string, WorkMeter> _meters = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _factsOfFile = new(StringComparer.Ordinal);
    private readonly List<SourceText> _files = [];
    private Dictionary<string, string>? _projectIds;
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

    // ---- bounded work -----------------------------------------------------------------

    /// <summary>The operation counter of one file. All extractors share it, so the bound holds for the file as a whole.</summary>
    public WorkMeter Meter(string path)
    {
        if (!_meters.TryGetValue(path, out var meter))
            _meters[path] = meter = new WorkMeter(this, path, Limits.MaxWorkPerFile);
        return meter;
    }

    public WorkMeter Meter(SourceText file) => Meter(file.Path);

    internal void WorkLimitReached(string path)
    {
        _incomplete = true;
        Note(NoteKind.LimitReached, "work-limit", path);
    }

    public IReadOnlyDictionary<string, long> WorkSpent() =>
        _meters.ToDictionary(m => m.Key, m => m.Value.Spent, StringComparer.Ordinal);

    /// <summary>
    /// Runs the work of one extractor on one file. Nothing it throws leaves: the failure becomes
    /// a note with a fixed reason, the result is marked incomplete, and the other files are read.
    /// </summary>
    public void Guarded(string extractor, string path, Action work)
    {
        try
        {
            work();
        }
        catch (Exception exception)
        {
            Failed(EvidenceExtractor.FailureReason(extractor, exception), path);
        }
    }

    // ---- intake -----------------------------------------------------------------------

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
            if (!PathFilter.IsRelevantPath(file.Path))
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
    /// A manifest that declares a Secret in any of its documents, or in the <c>items</c> of a
    /// <c>kind: List</c>, is not read at all. The line check covers a manifest that does not parse.
    /// </summary>
    private bool IsKubernetesSecret(SourceText file)
    {
        foreach (var root in Parse(file).Roots)
        {
            if (IsSecret(root))
                return true;
            if (root.Kind == DocKind.Map && root.Seq("items") is { } items)
                foreach (var item in items.Items)
                    if (IsSecret(item))
                        return true;
        }
        foreach (var line in file.Content.AsSpan().EnumerateLines())
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("- ", StringComparison.Ordinal))
                trimmed = trimmed[2..].TrimStart();
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

        static bool IsSecret(DocNode node) =>
            node.Kind == DocKind.Map && node.Str("kind") is { } kind && kind.Equals("Secret", StringComparison.Ordinal);
    }

    private ParsedDocuments Parse(SourceText file)
    {
        if (!_documents.TryGetValue(file.Path, out var parsed))
        {
            try
            {
                parsed = file.IsJson
                    ? StructuredDocument.ParseJson(file.Content, Limits.MaxNesting, Meter(file))
                    : StructuredDocument.ParseYaml(file.Content, Limits.MaxNesting, Meter(file));
            }
            catch (Exception exception)
            {
                parsed = new ParsedDocuments([], EvidenceExtractor.FailureReason("document", exception), 0);
            }
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
        else if (parsed.Failure.StartsWith("extractor-failure:", StringComparison.Ordinal) || parsed.Failure.StartsWith("out-of-memory:", StringComparison.Ordinal))
            Failed(parsed.Failure, file.Path);
        else
            Note(NoteKind.ParseFailure, parsed.Failure, file.Path, parsed.FailureLine);
        return [];
    }

    // ---- project identity -------------------------------------------------------------

    /// <summary>
    /// The identity of the project that a project file (<c>*.csproj</c> or <c>go.mod</c>)
    /// declares. It is the part of every key of that project: <c>service:&lt;id&gt;</c>,
    /// <c>operation:&lt;id&gt;:…</c>, <c>dependency:&lt;id&gt;:…</c>.
    /// <list type="bullet">
    /// <item>The repository-relative directory of the project file.</item>
    /// <item>For a project file at the repository root: the file name without <c>.csproj</c>,
    /// or the last element of the module path of <c>go.mod</c>.</item>
    /// <item>When two project files of the scan would get the same identity (two project files in
    /// one directory, or a root project named like a directory), each of them gets
    /// <c>&lt;identity&gt;#&lt;project file path&gt;</c>, so no two projects share a key.</item>
    /// </list>
    /// The identity depends only on the set of project files, not on their order.
    /// </summary>
    public string ProjectId(SourceText projectFile)
    {
        if (_projectIds is null)
        {
            var plain = new List<(string Path, string Id)>();
            foreach (var file in _files)
            {
                if (file.Extension == ".csproj")
                    plain.Add((file.Path, file.Directory.Length > 0 ? file.Directory : file.Name[..^".csproj".Length]));
                else if (file.Name == "go.mod")
                    plain.Add((file.Path, file.Directory.Length > 0 ? file.Directory : GoMod.LastElement(GoMod.ModulePath(file.Content) ?? "") is { Length: > 0 } last ? last : "go.mod"));
            }
            var shared = plain.GroupBy(p => p.Id, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.Ordinal);
            _projectIds = plain.ToDictionary(p => p.Path, p => shared.Contains(p.Id) ? $"{p.Id}#{p.Path}" : p.Id, StringComparer.Ordinal);
        }
        return _projectIds.TryGetValue(projectFile.Path, out var id) ? id : projectFile.Path;
    }

    // ---- facts ------------------------------------------------------------------------

    /// <summary>
    /// Joins the items of a list attribute. At most <see cref="ExtractionLimits.MaxListItems"/>
    /// items are kept; a longer list is cut, noted and makes the result incomplete, so a file
    /// cannot make one attribute (or the copies of it) grow without bound.
    /// </summary>
    public string List(IEnumerable<string> items, string path, int line, bool sorted = false, string separator = ",")
    {
        var kept = new List<string>();
        var cut = false;
        foreach (var item in sorted ? items.Distinct(StringComparer.Ordinal).OrderBy(i => i, StringComparer.Ordinal) : items)
        {
            if (kept.Count >= Limits.MaxListItems)
            {
                cut = true;
                break;
            }
            kept.Add(item);
        }
        if (cut)
            LimitReached("list-items", path, stop: false, line);
        return string.Join(separator, kept);
    }

    /// <summary>
    /// A list of declared names for one of the name-list attributes (<c>fields</c>,
    /// <c>entities</c>, <c>environment</c>). Each item is checked alone and only for the shape
    /// of a token: an item that has it is left out with a note, and the other names stay.
    /// </summary>
    public string Names(IEnumerable<string> items, string attribute, string path, int line, bool sorted = true, string separator = ",")
    {
        var safe = new List<string>();
        foreach (var item in items)
        {
            if (item.Length == 0)
                continue;
            if (item.Length > MaxNameLength || CredentialGuard.LooksSecretToken(item))
                Note(NoteKind.Redacted, "credential-like-value:" + attribute, path, line);
            else
                safe.Add(item);
        }
        return List(safe, path, line, sorted, separator);
    }

    public void Add(FactKind kind, string key, string name, SourceEvidence evidence, params (string Name, string? Value)[] attributes) =>
        Add(kind, key, name, [evidence], attributes);

    /// <summary>
    /// Adds a fact, or merges it into the fact with the same kind and key: the evidence is
    /// joined and every attribute value is recorded with the evidence that gave it. When the
    /// sources of one fact give different values for an attribute, no value wins: see
    /// <see cref="Result"/>. A key, name or value that looks like a credential never enters a fact.
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
            var ofFile = _factsOfFile.GetValueOrDefault(at.Path);
            if (ofFile >= Limits.MaxFactsPerFile)
            {
                LimitReached("facts-per-file", at.Path, stop: false);
                return;
            }
            _factsOfFile[at.Path] = ofFile + 1;
            draft = new Draft(kind, key, name);
            _facts[(kind, key)] = draft;
        }

        draft.Evidence.AddRange(evidence);
        var first = evidence.Min(EvidenceOrder)!;
        foreach (var (attribute, raw) in attributes)
        {
            if (string.IsNullOrEmpty(raw))
                continue;
            var value = TextUtil.FixSurrogates(raw);
            var isBody = attribute is "schema" or "payload" or "context" or "decision" or "consequences";
            if (value.Length > (isBody ? Limits.MaxSchemaBytes : MaxValueLength))
            {
                Note(NoteKind.Redacted, "value-too-long:" + attribute, at.Path, at.StartLine);
                continue;
            }
            if (NameListAttributes.Contains(attribute) ? CredentialGuard.LooksSecretToken(value) : CredentialGuard.LooksSecret(value))
            {
                Note(NoteKind.Redacted, "credential-like-value:" + attribute, at.Path, at.StartLine);
                continue;
            }
            if (!draft.Attributes.TryGetValue(attribute, out var values))
                draft.Attributes[attribute] = values = new SortedDictionary<string, SourceEvidence>(StringComparer.Ordinal);
            if (!values.TryGetValue(value, out var known))
            {
                if (values.Count < MaxConflictValues)
                    values[value] = first;
            }
            else if (EvidenceOrder.Compare(first, known) < 0)
            {
                values[value] = first;
            }
        }
    }

    private static readonly Comparer<SourceEvidence> EvidenceOrder = Comparer<SourceEvidence>.Create((a, b) =>
    {
        var order = string.CompareOrdinal(a.Path, b.Path);
        if (order == 0) order = a.StartLine.CompareTo(b.StartLine);
        if (order == 0) order = a.EndLine.CompareTo(b.EndLine);
        return order != 0 ? order : string.CompareOrdinal(a.Rule, b.Rule);
    });

    /// <summary>
    /// The facts in kind then key order, each with its evidence in path then line order.
    /// An attribute that all sources of a fact agree on is an attribute of the fact. An
    /// attribute that they disagree on is left out; <c>conflict.&lt;attribute&gt;</c> lists the
    /// distinct values in ordinal order, each with the first evidence that gave it
    /// (<c>value @ path:start-end</c>, joined with <c> | </c>), and a Conflict note is added.
    /// None of this depends on the order of the input or on the order of the extractors.
    /// </summary>
    public ExtractionResult Result(string repository, string commitSha)
    {
        var facts = Facts(note: true);
        var notes = _notes
            .OrderBy(n => n.Path, StringComparer.Ordinal).ThenBy(n => n.Line)
            .ThenBy(n => n.Kind).ThenBy(n => n.Reason, StringComparer.Ordinal).ToList();
        return new ExtractionResult(repository, commitSha, facts, notes, !_incomplete);
    }

    /// <summary>
    /// The facts added so far, as <see cref="Result"/> gives them, for a rule that reads the
    /// facts of the other extractors (<see cref="RelationshipExtractor"/>). It adds no note: an
    /// attribute that two sources disagree on is left out here and noted once by the result.
    /// </summary>
    public IReadOnlyList<ExtractedFact> CurrentFacts() => Facts(note: false);

    /// <summary>The services of the compose files, as <see cref="ComposeExtractor"/> read them. Never written to a fact.</summary>
    public List<ComposeService> ComposeServices { get; } = [];

    private List<ExtractedFact> Facts(bool note)
    {
        var facts = new List<ExtractedFact>(_facts.Count);
        foreach (var draft in _facts.Values.OrderBy(f => f.Kind.ToString(), StringComparer.Ordinal).ThenBy(f => f.Key, StringComparer.Ordinal))
        {
            var attributes = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (var (attribute, values) in draft.Attributes)
            {
                if (values.Count == 1)
                {
                    attributes[attribute] = values.Keys.First();
                    continue;
                }
                if (!note)
                    continue;
                var first = values.Values.Min(EvidenceOrder)!;
                Note(NoteKind.Conflict, "attribute:" + attribute, first.Path, first.StartLine);
                var listed = string.Join(" | ", values.Select(v => string.Create(
                    CultureInfo.InvariantCulture, $"{v.Key} @ {v.Value.Path}:{v.Value.StartLine}-{v.Value.EndLine}")));
                if (listed.Length <= MaxValueLength)
                    attributes["conflict." + attribute] = listed;
                else
                    Note(NoteKind.Redacted, "value-too-long:conflict." + attribute, first.Path, first.StartLine);
            }
            facts.Add(new ExtractedFact(draft.Kind, draft.Key, draft.Name, attributes,
                draft.Evidence.Distinct().Order(EvidenceOrder).ToList()));
        }
        return facts;
    }
}

/// <summary>The two things the scan reads from a <c>go.mod</c> before the Go extractor runs.</summary>
internal static class GoMod
{
    /// <summary>The path of the first <c>module</c> directive, or null.</summary>
    public static string? ModulePath(string content)
    {
        foreach (var raw in content.AsSpan().EnumerateLines())
        {
            var line = raw;
            var comment = line.IndexOf("//", StringComparison.Ordinal);
            if (comment >= 0)
                line = line[..comment];
            line = line.Trim();
            if (!line.StartsWith("module", StringComparison.Ordinal) || line.Length < 8 || !char.IsWhiteSpace(line[6]))
                continue;
            var path = line[6..].Trim().Trim('"');
            return path.Length == 0 ? null : path.ToString();
        }
        return null;
    }

    /// <summary>"github.com/acme/orders/inventory-worker" and "…/inventory-worker/v2" both give "inventory-worker".</summary>
    public static string LastElement(string modulePath)
    {
        var parts = modulePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var last = parts.Length - 1;
        if (last > 0 && parts[last].Length > 1 && parts[last][0] == 'v' && parts[last].AsSpan(1).IndexOfAnyExceptInRange('0', '9') < 0)
            last--;
        return last < 0 ? "" : TextUtil.Slug(parts[last]);
    }
}
