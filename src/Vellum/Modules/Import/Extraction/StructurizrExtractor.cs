using System.Text;

namespace Vellum.Modules.Import.Extraction;

/// <summary>
/// Structurizr workspaces: the DSL (<c>*.dsl</c>, rule <c>structurizr.dsl</c>) and the workspace
/// JSON (any <c>*.json</c> whose root has a <c>model</c> with <c>people</c> or
/// <c>softwareSystems</c>, rule <c>structurizr.json</c>). Both give <c>declared</c> facts with
/// the same keys, so the DSL and the JSON of one workspace are one set of facts.
/// <para>
/// <b>Mapping</b> (the reverse of the step 24 export): a <c>person</c> is an <c>actor</c>; a
/// <c>softwareSystem</c> is a <c>system</c>; a <c>container</c> is an <c>app</c>, or a
/// <c>store</c> when it has the tag <c>Database</c>; a <c>component</c> is a <c>component</c>.
/// A relationship keeps its description (the label), its technology, and is behavioural when
/// it has the tag <c>Behavioural</c>. A relationship with the tag <c>Derived</c> is a derived
/// line that the export wrote for a view, not a fact, and is not read. An element keeps its description, its technology (a
/// container or a component) and its own tags; the Structurizr default tags
/// (<c>Element</c>, <c>Person</c>, …) and <c>Database</c> of a store are left out. Groups and
/// enterprises are ignored with a note; their elements are read as if no group were there.
/// Views, styles, deployment environments, properties, perspectives, URLs and custom
/// elements are not read.
/// </para>
/// <para>
/// <b>Keys.</b> An element is <c>structurizr:element:&lt;names&gt;</c>, the names of its
/// ancestors and its own joined with <c>/</c> (a <c>/</c> or <c>%</c> in a name is written
/// <c>%2F</c>, <c>%25</c>): the names that Structurizr keeps unique in their scope. A DSL
/// identifier is local to the file and is not part of a key. A relationship is
/// <c>structurizr:relationship:&lt;from&gt;-&gt;&lt;to&gt;|&lt;label&gt;</c>.
/// </para>
/// <para>
/// <b>The DSL reader does not evaluate.</b> It reads the statements of the file and nothing
/// else. <c>!include</c>, <c>!script</c>, <c>!plugin</c>, <c>!docs</c>, <c>!adrs</c>,
/// <c>!constant</c>, <c>!const</c>, <c>!var</c>, <c>!ref</c>, <c>!extend</c> and every other
/// directive are not followed, run or applied: each gives a note, and a block after one is
/// skipped. <c>workspace extends</c> is not followed. <c>${NAME}</c> is never substituted: the
/// text stays as written, with a note. A URL is never fetched: it gives a note. An identifier
/// resolves within the file only; a relationship to one that the file does not define gives a
/// note and no fact.
/// </para>
/// </summary>
internal sealed class StructurizrExtractor : IExtractor
{
    public static readonly ExtractionRule Dsl = new("structurizr.dsl", 1);
    public static readonly ExtractionRule Json = new("structurizr.json", 1);

    public string Id => "structurizr";
    public IReadOnlyList<ExtractionRule> Rules { get; } = [Dsl, Json];

    public void Run(ExtractionContext context)
    {
        foreach (var file in context.Files)
        {
            if (context.Stopped)
                return;
            if (file.Extension == ".dsl")
                context.Guarded(Id, file.Path, () => new DslReader(context, file).Read());
            else if (file.IsJson && LooksLikeWorkspace(file.Content))
                context.Guarded(Id, file.Path, () => ReadJson(context, file));
        }
    }

    // ---- shared ---------------------------------------------------------------------------

    /// <summary>An element as a model file declares it, before its end line is known.</summary>
    internal sealed class ElementDraft(string key, string name, string type, ElementDraft? parent, int startLine)
    {
        public string Key { get; } = key;
        public string Name { get; } = name;
        /// <summary><c>person</c>, <c>softwareSystem</c>, <c>container</c> or <c>component</c>.</summary>
        public string Type { get; } = type;
        public ElementDraft? Parent { get; } = parent;
        public int StartLine { get; } = startLine;
        public int EndLine { get; set; } = startLine;
        public string? Description { get; set; }
        public string? Technology { get; set; }
        public List<string> Tags { get; } = [];
    }

    internal sealed class RelationshipDraft(string? from, int startLine)
    {
        public string? From { get; set; } = from;
        public string? To { get; set; }
        public int StartLine { get; } = startLine;
        public int EndLine { get; set; } = startLine;
        public string? Label { get; set; }
        public string? Technology { get; set; }
        public List<string> Tags { get; } = [];
    }

    private static readonly HashSet<string> DefaultTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "Element", "Person", "Software System", "Container", "Component", "Relationship",
    };

    public static string ElementKey(ElementDraft? parent, string name) =>
        (parent is null ? "structurizr:element:" : parent.Key + "/") + name.Replace("%", "%25", StringComparison.Ordinal).Replace("/", "%2F", StringComparison.Ordinal);

    public static string RelationshipKey(string from, string to, string? label) => $"structurizr:relationship:{from}->{to}|{label ?? ""}";

    /// <summary>The model kind of a Structurizr element, or null when the model cannot hold it where it stands.</summary>
    private static string? KindOf(ElementDraft element)
    {
        var database = element.Tags.Any(t => t.Equals("Database", StringComparison.OrdinalIgnoreCase));
        return element.Type switch
        {
            "person" => "actor",
            "softwareSystem" => "system",
            "container" => database ? "store" : "app",
            // A component of a database container has no place in the model: a component sits in an app.
            "component" when KindOf(element.Parent!) == "store" => null,
            "component" => "component",
            _ => null,
        };
    }

    public static IEnumerable<string> SplitTags(string? tags) =>
        (Text(tags) ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>Adds the facts of one file: every element whose place the model can hold, then every relationship between two of them.</summary>
    public static void AddFacts(
        ExtractionContext context, SourceText file, ExtractionRule rule,
        IReadOnlyList<ElementDraft> elements, IReadOnlyList<RelationshipDraft> relationships)
    {
        var added = new HashSet<string>(StringComparer.Ordinal);
        foreach (var element in elements)
        {
            if (KindOf(element) is not { } kind)
            {
                context.Note(NoteKind.Undecided, "structurizr:component-in-database", file.Path, element.StartLine);
                continue;
            }
            if (element.Parent is not null && !added.Contains(element.Parent.Key))
                continue;
            if (ModelFileText.Line(context, file, element.StartLine, "name", element.Name) is null)
                continue;
            // Structurizr's External is the tag Vellum gives a system outside the repository.
            var tags = element.Tags
                .Where(t => !DefaultTags.Contains(t) && !(kind == "store" && t.Equals("Database", StringComparison.OrdinalIgnoreCase)))
                .Select(t => t.Equals(ElementTags.External, StringComparison.OrdinalIgnoreCase) ? ElementTags.External : t)
                .Distinct(StringComparer.Ordinal);
            context.Add(FactKind.Element, element.Key, element.Name, rule.At(file.Path, element.StartLine, element.EndLine),
                ("elementKind", kind), ("parent", element.Parent?.Key), ("structurizr.type", element.Type),
                ("description", ModelFileText.Lines(context, file, element.StartLine, "description", element.Description)),
                ("technology", element.Type is "container" or "component" ? ModelFileText.Line(context, file, element.StartLine, "technology", element.Technology) : null),
                ("tags", ModelFileText.Tags(context, file, element.StartLine, tags)));
            added.Add(element.Key);
        }

        foreach (var relationship in relationships)
        {
            if (relationship.From is not { } from || relationship.To is not { } to || !added.Contains(from) || !added.Contains(to))
                continue;
            // A derived line of a Vellum view stands for relationships the file holds already.
            if (relationship.Tags.Any(t => t.Equals("Derived", StringComparison.OrdinalIgnoreCase)))
                continue;
            var behavioural = relationship.Tags.Any(t => t.Equals("Behavioural", StringComparison.OrdinalIgnoreCase));
            var label = ModelFileText.Line(context, file, relationship.StartLine, "label", relationship.Label);
            if (relationship.Label is not null && label is null)
                continue;
            context.Add(FactKind.Relationship, RelationshipKey(from, to, label), label ?? "",
                rule.At(file.Path, relationship.StartLine, relationship.EndLine),
                ("from", from), ("to", to), ("label", label),
                ("technology", ModelFileText.Line(context, file, relationship.StartLine, "technology", relationship.Technology)),
                ("relationshipKind", behavioural ? "behavioural" : "structural"));
        }
    }

    /// <summary>The value, without the one mark the reader may have put in front of it; null when it is blank.</summary>
    private static string? Text(string? value)
    {
        if (value is { Length: > 0 } && value[0] == QuotedMark)
            value = value[1..];
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    /// <summary>Marks a quoted word that reads like syntax, so the reader never takes it for syntax. <see cref="Text"/> takes it off.</summary>
    private const char QuotedMark = (char)1;

    // ---- workspace JSON ---------------------------------------------------------------------

    /// <summary>A cheap test before the parse: a workspace names its model parts.</summary>
    private static bool LooksLikeWorkspace(string content) =>
        content.Contains("\"model\"", StringComparison.Ordinal)
        && (content.Contains("\"softwareSystems\"", StringComparison.Ordinal) || content.Contains("\"people\"", StringComparison.Ordinal));

    private static void ReadJson(ExtractionContext context, SourceText file)
    {
        var roots = context.Documents(file);
        if (roots.Count != 1 || roots[0].Map("model") is not { } model
            || (model.Seq("people") is null && model.Seq("softwareSystems") is null))
            return;
        var meter = context.Meter(file);
        var elements = new List<ElementDraft>();
        var byId = new Dictionary<string, ElementDraft>(StringComparer.Ordinal);
        var pending = new List<(string SourceId, DocNode Node)>();

        void Read(DocNode? list, string type, ElementDraft? parent)
        {
            foreach (var node in list?.Items ?? [])
            {
                if (!meter.Charge() || node.Kind != DocKind.Map)
                    continue;
                if (Text(node.Str("name")) is not { } name)
                {
                    context.Note(NoteKind.Undecided, "structurizr:element-without-name", file.Path, node.StartLine);
                    continue;
                }
                if (node.Get("group") is not null)
                    context.Note(NoteKind.Undecided, "structurizr:group-ignored", file.Path, node.StartLine);
                if (node.Get("url") is not null)
                    context.Note(NoteKind.Undecided, "structurizr:url-not-followed", file.Path, node.StartLine);
                var element = new ElementDraft(ElementKey(parent, name), name, type, parent, node.StartLine)
                {
                    EndLine = node.EndLine,
                    Description = Text(node.Str("description")),
                    Technology = Text(node.Str("technology")),
                };
                element.Tags.AddRange(SplitTags(node.Str("tags")));
                elements.Add(element);
                if (node.Str("id") is { } id && !byId.TryAdd(id, element))
                    context.Note(NoteKind.Undecided, "structurizr:duplicate-identifier", file.Path, node.StartLine);
                foreach (var relationship in node.Seq("relationships")?.Items ?? [])
                {
                    if (relationship.Kind == DocKind.Map)
                        pending.Add((node.Str("id") ?? "", relationship));
                }
                if (type == "softwareSystem")
                    Read(node.Seq("containers"), "container", element);
                else if (type == "container")
                    Read(node.Seq("components"), "component", element);
            }
        }

        Read(model.Seq("people"), "person", null);
        Read(model.Seq("softwareSystems"), "softwareSystem", null);
        if (model.Seq("deploymentNodes") is not null)
            context.Note(NoteKind.Undecided, "structurizr:deployment-not-read", file.Path, model.StartLine);

        var relationships = new List<RelationshipDraft>();
        foreach (var (owner, node) in pending)
        {
            if (!meter.Charge())
                break;
            // A relationship that Structurizr implied from another one is not a statement of the file.
            if (node.Str("linkedRelationshipId") is not null)
                continue;
            var from = byId.GetValueOrDefault(node.Str("sourceId") ?? owner);
            var to = byId.GetValueOrDefault(node.Str("destinationId") ?? "");
            if (from is null || to is null)
            {
                context.Note(NoteKind.Undecided, "structurizr:unresolved-identifier", file.Path, node.StartLine);
                continue;
            }
            var draft = new RelationshipDraft(from.Key, node.StartLine)
            {
                To = to.Key, EndLine = node.EndLine, Label = Text(node.Str("description")), Technology = Text(node.Str("technology")),
            };
            draft.Tags.AddRange(SplitTags(node.Str("tags")));
            relationships.Add(draft);
        }
        AddFacts(context, file, Json, elements, relationships);
    }

    // ---- DSL ----------------------------------------------------------------------------------

    /// <summary>
    /// A bounded reader of the DSL. It reads statements line by line (a line that ends with
    /// <c>\</c> goes on on the next one), keeps a stack of blocks, and stops a file when its
    /// work budget is spent. It never opens another file and never runs anything.
    /// </summary>
    private sealed class DslReader(ExtractionContext context, SourceText file)
    {
        private enum Block { Root, Workspace, Model, Element, Group, Relationship, Skip }

        private sealed record Frame(Block Block, ElementDraft? Element, RelationshipDraft? Relationship = null);

        private readonly record struct Token(string Text, bool Quoted)
        {
            public bool Is(string syntax) => !Quoted && Text == syntax;
        }

        private readonly WorkMeter _meter = context.Meter(file);
        private readonly List<ElementDraft> _elements = [];
        private readonly List<(RelationshipDraft Draft, string? FromId, string? ToId, int Line)> _relationships = [];
        private readonly Dictionary<string, ElementDraft> _ids = new(StringComparer.Ordinal);
        private readonly Dictionary<ElementDraft, string> _paths = [];
        private readonly Stack<Frame> _stack = new();
        private bool _hierarchical;

        public void Read()
        {
            _stack.Push(new Frame(Block.Root, null));
            foreach (var (tokens, line) in Statements())
            {
                if (!_meter.Charge(tokens.Count + 1))
                    return;
                Statement(tokens, line);
            }

            var relationships = new List<RelationshipDraft>();
            foreach (var (draft, fromId, toId, line) in _relationships)
            {
                var from = draft.From ?? Resolve(fromId);
                var to = Resolve(toId);
                if (from is null || to is null)
                {
                    context.Note(NoteKind.Undecided, "structurizr:unresolved-identifier", file.Path, line);
                    continue;
                }
                draft.From = from;
                draft.To = to;
                relationships.Add(draft);
            }
            AddFacts(context, file, Dsl, _elements, relationships);
        }

        private string? Resolve(string? id) => id is not null && _ids.TryGetValue(id, out var element) ? element.Key : null;

        private ElementDraft? CurrentElement => _stack.Count == 0 ? null : _stack.Peek().Element;

        private void Statement(List<Token> tokens, int line)
        {
            var frame = _stack.Peek();
            var opens = tokens[^1].Is("{");
            var bare = opens ? tokens[..^1] : tokens;
            // A quoted word never acts as syntax: it gets a mark that no comparison below matches.
            var words = bare.Select(t => t.Quoted && (t.Text is "{" or "}" or "->" or "=" || t.Text.StartsWith('!')) ? QuotedMark + t.Text : t.Text).ToList();
            if (words.Count == 1 && bare[0].Is("}"))
            {
                Close(line);
                return;
            }

            if (words.Count == 1 && words[0] == "}")
            {
                Close(line);
                return;
            }
            if (words.Count > 0 && words[^1] == "}")
            {
                // "{ … }" on one line: read nothing of it.
                return;
            }
            if (words.Count == 0)
            {
                if (opens)
                    _stack.Push(new Frame(Block.Skip, frame.Element));
                return;
            }

            foreach (var word in words)
            {
                if (word.Contains("${", StringComparison.Ordinal))
                    context.Note(NoteKind.Undecided, "structurizr:variable-not-substituted", file.Path, line);
                if (word.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || word.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    context.Note(NoteKind.Undecided, "structurizr:url-not-followed", file.Path, line);
            }

            // A directive gives its note wherever it stands, also inside a block that is not read.
            if (words[0].StartsWith('!'))
            {
                Directive(words, line);
                if (opens)
                    _stack.Push(new Frame(Block.Skip, frame.Element));
                return;
            }

            if (frame.Block == Block.Skip)
            {
                if (opens)
                    _stack.Push(new Frame(Block.Skip, frame.Element));
                return;
            }

            var keyword = words[0].ToLowerInvariant();
            switch (frame.Block)
            {
                case Block.Root:
                    if (keyword == "workspace")
                    {
                        if (words.Count > 1 && words[1].Equals("extends", StringComparison.OrdinalIgnoreCase))
                            context.Note(NoteKind.Undecided, "structurizr:extends-not-followed", file.Path, line);
                        Push(opens, Block.Workspace, null);
                    }
                    else
                        Push(opens, Block.Skip, null);
                    return;

                case Block.Workspace:
                    Push(opens, keyword == "model" ? Block.Model : Block.Skip, null);
                    return;

                case Block.Relationship:
                    RelationshipProperty(frame.Relationship!, keyword, words, line);
                    if (opens)
                        _stack.Push(new Frame(Block.Skip, frame.Element));
                    return;

                default:
                    ModelStatement(frame, keyword, words, opens, line);
                    return;
            }
        }

        private void ModelStatement(Frame frame, string keyword, List<string> words, bool opens, int line)
        {
            // An identifier assignment: "id = …".
            string? id = null;
            var body = words;
            if (words.Count >= 3 && words[1] == "=")
            {
                id = words[0];
                body = words[2..];
                keyword = body[0].ToLowerInvariant();
            }

            var arrow = body.IndexOf("->");
            if (arrow >= 0)
            {
                Relationship(frame, body, arrow, opens, line);
                return;
            }

            switch (keyword)
            {
                case "person" or "softwaresystem" or "container" or "component":
                    Element(frame, id, keyword, body, opens, line);
                    return;
                case "group" or "enterprise":
                    context.Note(NoteKind.Undecided, "structurizr:group-ignored", file.Path, line);
                    Push(opens, Block.Group, frame.Element);
                    return;
                case "description" when frame.Block == Block.Element && body.Count > 1:
                    frame.Element!.Description = Text(body[1]);
                    return;
                case "technology" when frame.Block == Block.Element && body.Count > 1:
                    frame.Element!.Technology = Text(body[1]);
                    return;
                case "tags" or "tag" when frame.Block == Block.Element:
                    foreach (var tag in body.Skip(1))
                        frame.Element!.Tags.AddRange(SplitTags(tag));
                    return;
                case "url":
                    context.Note(NoteKind.Undecided, "structurizr:url-not-followed", file.Path, line);
                    return;
                case "deploymentenvironment" or "deploymentnode" or "infrastructurenode":
                    context.Note(NoteKind.Undecided, "structurizr:deployment-not-read", file.Path, line);
                    Push(opens, Block.Skip, frame.Element);
                    return;
                case "element" or "archetypes":
                    context.Note(NoteKind.Undecided, "structurizr:custom-element-not-read", file.Path, line);
                    Push(opens, Block.Skip, frame.Element);
                    return;
                default:
                    // properties, perspectives, healthcheck, instances and anything unknown.
                    Push(opens, Block.Skip, frame.Element);
                    return;
            }
        }

        private void Element(Frame frame, string? id, string keyword, List<string> body, bool opens, int line)
        {
            var type = keyword switch { "person" => "person", "softwaresystem" => "softwareSystem", "container" => "container", _ => "component" };
            var parent = frame.Element;
            var fits = type switch
            {
                "person" or "softwareSystem" => parent is null,
                "container" => parent?.Type == "softwareSystem",
                _ => parent?.Type == "container",
            };
            var args = body.Skip(1).ToList();
            if (!fits || args.Count == 0 || Text(args[0]) is not { } name)
            {
                context.Note(NoteKind.Undecided, fits ? "structurizr:element-without-name" : "structurizr:element-out-of-place", file.Path, line);
                Push(opens, Block.Skip, parent);
                return;
            }

            var element = new ElementDraft(ElementKey(parent, name), name, type, parent, line);
            element.Description = args.Count > 1 ? Text(args[1]) : null;
            var tagIndex = 2;
            if (type is "container" or "component")
            {
                element.Technology = args.Count > 2 ? Text(args[2]) : null;
                tagIndex = 3;
            }
            if (args.Count > tagIndex)
                element.Tags.AddRange(SplitTags(args[tagIndex]));
            _elements.Add(element);

            if (id is not null)
            {
                var path = parent is not null && _paths.TryGetValue(parent, out var parentPath) ? $"{parentPath}.{id}" : id;
                _paths[element] = path;
                if (!_ids.TryAdd(_hierarchical ? path : id, element))
                    context.Note(NoteKind.Undecided, "structurizr:duplicate-identifier", file.Path, line);
                else if (!_hierarchical)
                    _ids.TryAdd(path, element);
            }
            Push(opens, Block.Element, element);
        }

        private void Relationship(Frame frame, List<string> body, int arrow, bool opens, int line)
        {
            if (frame.Block == Block.Root || frame.Block == Block.Workspace)
                return;
            var source = arrow == 0 ? "this" : body[arrow - 1];
            if (arrow > 1 || arrow + 1 >= body.Count)
            {
                context.Note(NoteKind.Undecided, "structurizr:relationship-not-read", file.Path, line);
                Push(opens, Block.Skip, frame.Element);
                return;
            }
            var args = body.Skip(arrow + 2).ToList();
            var draft = new RelationshipDraft(source == "this" ? frame.Element?.Key : null, line)
            {
                Label = args.Count > 0 ? Text(args[0]) : null,
                Technology = args.Count > 1 ? Text(args[1]) : null,
            };
            if (args.Count > 2)
                draft.Tags.AddRange(SplitTags(args[2]));
            if (source == "this" && frame.Element is null)
            {
                context.Note(NoteKind.Undecided, "structurizr:unresolved-identifier", file.Path, line);
                Push(opens, Block.Skip, frame.Element);
                return;
            }
            _relationships.Add((draft, source == "this" ? null : source, body[arrow + 1], line));
            if (opens)
                _stack.Push(new Frame(Block.Relationship, frame.Element, draft));
        }

        private void RelationshipProperty(RelationshipDraft draft, string keyword, List<string> words, int line)
        {
            if (keyword == "url")
                context.Note(NoteKind.Undecided, "structurizr:url-not-followed", file.Path, line);
            if (words.Count < 2)
                return;
            switch (keyword)
            {
                case "description":
                    draft.Label = Text(words[1]);
                    break;
                case "technology":
                    draft.Technology = Text(words[1]);
                    break;
                case "tags" or "tag":
                    draft.Tags.AddRange(words.Skip(1).SelectMany(SplitTags));
                    break;
            }
        }

        private void Directive(List<string> words, int line)
        {
            var name = words[0].ToLowerInvariant();
            switch (name)
            {
                case "!identifiers":
                    _hierarchical = words.Count > 1 && words[1].Equals("hierarchical", StringComparison.OrdinalIgnoreCase);
                    return;
                case "!impliedrelationships":
                    return;
                case "!include":
                    context.Note(NoteKind.Undecided, "structurizr:include-not-followed", file.Path, line);
                    return;
                case "!script":
                    context.Note(NoteKind.Undecided, "structurizr:script-not-run", file.Path, line);
                    return;
                case "!plugin":
                    context.Note(NoteKind.Undecided, "structurizr:plugin-not-run", file.Path, line);
                    return;
                case "!docs" or "!adrs" or "!decisions":
                    context.Note(NoteKind.Undecided, "structurizr:docs-not-read", file.Path, line);
                    return;
                case "!constant" or "!const" or "!var":
                    context.Note(NoteKind.Undecided, "structurizr:constant-not-substituted", file.Path, line);
                    return;
                default:
                    context.Note(NoteKind.Undecided, "structurizr:directive-not-followed", file.Path, line);
                    return;
            }
        }

        private void Push(bool opens, Block block, ElementDraft? element)
        {
            if (opens)
                _stack.Push(new Frame(block, element));
        }

        private void Close(int line)
        {
            if (_stack.Count <= 1)
                return;
            var frame = _stack.Pop();
            if (frame.Block == Block.Element && frame.Element is { } element)
                element.EndLine = line;
            else if (frame.Block == Block.Relationship && frame.Relationship is { } relationship)
                relationship.EndLine = line;
        }

        /// <summary>
        /// The statements of the file: the tokens of each line and the line it starts on. A
        /// token is a quoted string (with <c>\"</c> as the one escape), <c>{</c>, <c>}</c> or a run
        /// of other characters. Comments (<c>//</c>, <c>#</c> at the start, <c>/* … */</c>) are
        /// left out.
        /// </summary>
        private IEnumerable<(List<Token> Tokens, int Line)> Statements()
        {
            var tokens = new List<Token>();
            var start = 0;
            var inBlockComment = false;
            var lineNumber = 0;
            foreach (var raw in file.Content.Split('\n'))
            {
                lineNumber++;
                if (!_meter.Charge(raw.Length + 1))
                    yield break;
                if (tokens.Count == 0)
                    start = lineNumber;

                var continued = false;
                var i = 0;
                var text = raw;
                while (i < text.Length)
                {
                    if (inBlockComment)
                    {
                        var end = text.IndexOf("*/", i, StringComparison.Ordinal);
                        if (end < 0)
                        {
                            i = text.Length;
                            break;
                        }
                        inBlockComment = false;
                        i = end + 2;
                        continue;
                    }
                    var c = text[i];
                    if (char.IsWhiteSpace(c))
                    {
                        i++;
                        continue;
                    }
                    if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
                    {
                        inBlockComment = true;
                        i += 2;
                        continue;
                    }
                    if ((c == '/' && i + 1 < text.Length && text[i + 1] == '/') || (c == '#' && tokens.Count == 0))
                        break;
                    if (c == '\\' && text.AsSpan(i + 1).IsWhiteSpace())
                    {
                        continued = true;
                        break;
                    }
                    if (c == '"')
                    {
                        var value = new StringBuilder();
                        i++;
                        while (i < text.Length && text[i] != '"')
                        {
                            if (text[i] == '\\' && i + 1 < text.Length && text[i + 1] == '"')
                                i++;
                            if (text[i] != QuotedMark)
                                value.Append(text[i]);
                            i++;
                        }
                        i++;
                        tokens.Add(new Token(value.ToString(), true));
                        continue;
                    }
                    if (c is '{' or '}')
                    {
                        tokens.Add(new Token(c.ToString(), false));
                        i++;
                        continue;
                    }
                    var startToken = i;
                    while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] is not ('"' or '{' or '}'))
                        i++;
                    tokens.Add(new Token(text[startToken..i].Replace(QuotedMark.ToString(), "", StringComparison.Ordinal), false));
                }

                if (continued || tokens.Count == 0)
                    continue;
                // "a {" and "}" are statements; a line "} else {" does not occur in the DSL.
                yield return (tokens, start);
                tokens = [];
            }
            if (tokens.Count > 0)
                yield return (tokens, start);
        }
    }
}
