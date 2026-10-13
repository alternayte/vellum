using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlScalar = YamlDotNet.Core.Events.Scalar;

namespace Vellum.Modules.Import.Extraction;

internal enum DocKind { Map, Seq, Scalar }

internal enum ScalarType { String, Number, Bool, Null }

internal sealed record DocEntry(string Key, int KeyLine, DocNode Value);

/// <summary>A node of a JSON or YAML document, with the lines it occupies in the file.</summary>
internal sealed class DocNode
{
    public DocKind Kind { get; init; }
    public int StartLine { get; init; }
    public int EndLine { get; set; }
    public string Text { get; init; } = "";
    public ScalarType Type { get; init; }
    public List<DocEntry> Entries { get; init; } = [];
    public List<DocNode> Items { get; init; } = [];
    /// <summary>Nodes below and including this one, counting an aliased node once for each use.</summary>
    public int Size { get; set; } = 1;
    public int Height { get; set; } = 1;

    private const int IndexFrom = 8;
    private Dictionary<string, DocEntry>? _index;

    /// <summary>
    /// The first entry with the key. A map with more than <see cref="IndexFrom"/> entries is
    /// looked up through an index that is built once, so a loop that asks a large map for many
    /// keys is linear and not quadratic.
    /// </summary>
    public DocEntry? Entry(string key)
    {
        if (Kind != DocKind.Map)
            return null;
        if (Entries.Count <= IndexFrom)
        {
            foreach (var entry in Entries)
                if (string.Equals(entry.Key, key, StringComparison.Ordinal))
                    return entry;
            return null;
        }
        if (_index is null)
        {
            var index = new Dictionary<string, DocEntry>(Entries.Count, StringComparer.Ordinal);
            foreach (var entry in Entries)
                index.TryAdd(entry.Key, entry);
            _index = index;
        }
        return _index.GetValueOrDefault(key);
    }

    public DocNode? Get(string key) => Entry(key)?.Value;
    public DocNode? Map(string key) => Get(key) is { Kind: DocKind.Map } node ? node : null;
    public DocNode? Seq(string key) => Get(key) is { Kind: DocKind.Seq } node ? node : null;

    /// <summary>The text of a scalar that is not null, or null.</summary>
    public string? Str(string key) => Get(key)?.AsText();
    public string? AsText() => Kind == DocKind.Scalar && Type != ScalarType.Null ? Text : null;
}

/// <param name="Failure">Null when the content was parsed. Otherwise a fixed reason code.</param>
internal sealed record ParsedDocuments(IReadOnlyList<DocNode> Roots, string? Failure, int FailureLine);

/// <summary>
/// Reads JSON with <see cref="Utf8JsonReader"/> and YAML with the YamlDotNet event parser into
/// one node shape. Nothing is deserialized into types, no tag is acted on, and an alias is
/// followed only inside a small budget, so a document cannot make the reader do work that
/// is out of proportion to its size. Each reader is one pass over the content and charges the
/// length of the content to the meter of the file.
/// <para>
/// Every document of a multi-document YAML file is a root, also an empty one. A node ends at
/// its last line of content: a block scalar ends at its last line that is not blank, also when
/// that line starts with '#', and never at a YAML comment after it.
/// </para>
/// <para>
/// A line of only spaces and tabs is read as an empty line. YAML refuses a tab in indentation,
/// and such a line indents nothing.
/// </para>
/// </summary>
internal static partial class StructuredDocument
{
    private const int MaxAliases = 100;
    private const int MaxAliasedNodes = 10_000;

    [GeneratedRegex(@"^-?(0|[1-9][0-9]*)(\.[0-9]+)?([eE][+\-]?[0-9]+)?$", RegexOptions.NonBacktracking | RegexOptions.CultureInvariant)]
    private static partial Regex JsonNumber();

    private sealed class Frame(DocNode node, string? anchor, bool flow)
    {
        public DocNode Node { get; } = node;
        public string? Anchor { get; } = anchor;
        public bool Flow { get; } = flow;
        public bool HasKey { get; set; }
        public string Key { get; set; } = "";
        public int KeyLine { get; set; }
    }

    public static ParsedDocuments ParseYaml(string content, int maxNesting, WorkMeter? meter = null)
    {
        if (meter is not null && !meter.Charge(content.Length))
            return new ParsedDocuments([], "limit:work", 0);
        content = BlankTabLines(content);
        var lastTextLine = LastTextLines(content);
        var roots = new List<DocNode>();
        var stack = new Stack<Frame>();
        var anchors = new Dictionary<string, DocNode>(StringComparer.Ordinal);
        var aliases = 0;
        long aliasedNodes = 0;
        var line = 1;

        // A failure is reported at a line that the file has.
        ParsedDocuments Fail(string reason) => new([], reason, Math.Clamp(line, 1, Math.Max(1, lastTextLine[^1])));

        void Attach(DocNode node)
        {
            if (stack.Count == 0)
            {
                roots.Add(node);
                return;
            }
            var frame = stack.Peek();
            if (frame.Node.Kind == DocKind.Seq)
            {
                frame.Node.Items.Add(node);
            }
            else if (!frame.HasKey)
            {
                frame.HasKey = true;
                frame.Key = node.Kind == DocKind.Scalar ? node.Text : "";
                frame.KeyLine = node.StartLine;
            }
            else
            {
                frame.HasKey = false;
                frame.Node.Entries.Add(new DocEntry(frame.Key, frame.KeyLine, node));
            }
        }

        try
        {
            // YAML also breaks lines at NEL, LS and PS. Editors and GitHub do not, so those
            // characters become spaces and every line number matches what a reader sees.
            var parser = new Parser(new StringReader(content.Replace((char)0x85, ' ').Replace((char)0x2028, ' ').Replace((char)0x2029, ' ')));
            while (parser.MoveNext())
            {
                var current = parser.Current;
                if (current is null)
                    continue;
                line = (int)current.Start.Line;
                switch (current)
                {
                    case DocumentStart:
                        anchors.Clear();
                        stack.Clear();
                        break;
                    case MappingStart mapping:
                        if (stack.Count >= maxNesting)
                            return Fail("limit:nesting");
                        stack.Push(new Frame(
                            new DocNode { Kind = DocKind.Map, StartLine = line, EndLine = line },
                            AnchorOf(mapping), mapping.Style == MappingStyle.Flow));
                        break;
                    case SequenceStart sequence:
                        if (stack.Count >= maxNesting)
                            return Fail("limit:nesting");
                        stack.Push(new Frame(
                            new DocNode { Kind = DocKind.Seq, StartLine = line, EndLine = line },
                            AnchorOf(sequence), sequence.Style == SequenceStyle.Flow));
                        break;
                    case MappingEnd or SequenceEnd:
                    {
                        if (stack.Count == 0)
                            return Fail("yaml-syntax");
                        var frame = stack.Pop();
                        var node = frame.Node;
                        var end = frame.Flow ? (int)current.End.Line : node.StartLine;
                        var size = 1;
                        var height = 0;
                        foreach (var child in node.Kind == DocKind.Map ? node.Entries.Select(e => e.Value) : node.Items)
                        {
                            end = Math.Max(end, child.EndLine);
                            size = (int)Math.Min(int.MaxValue, (long)size + child.Size);
                            height = Math.Max(height, child.Height);
                        }
                        node.EndLine = end;
                        node.Size = size;
                        node.Height = height + 1;
                        if (node.Height > maxNesting)
                            return Fail("limit:nesting");
                        if (frame.Anchor is not null)
                            anchors[frame.Anchor] = node;
                        Attach(node);
                        break;
                    }
                    case YamlScalar scalar:
                    {
                        var end = (int)scalar.End.Line;
                        if (scalar.End.Column <= 1 && end > line)
                            end--;
                        // A block scalar takes the blank lines after its text. They are not evidence.
                        if (scalar.Style is ScalarStyle.Literal or ScalarStyle.Folded)
                            end = lastTextLine[Math.Clamp(end, 0, lastTextLine.Length - 1)];
                        var plain = scalar.Style == ScalarStyle.Plain;
                        var node = new DocNode
                        {
                            Kind = DocKind.Scalar, StartLine = line, EndLine = Math.Max(line, end),
                            Text = scalar.Value, Type = plain ? PlainType(scalar.Value) : ScalarType.String,
                        };
                        if (AnchorOf(scalar) is { } anchor)
                            anchors[anchor] = node;
                        Attach(node);
                        break;
                    }
                    case AnchorAlias alias:
                    {
                        anchors.TryGetValue(alias.Value.Value, out var target);
                        aliases++;
                        aliasedNodes += target?.Size ?? 1;
                        if (aliases > MaxAliases || aliasedNodes > MaxAliasedNodes)
                            return Fail("limit:yaml-aliases");
                        // The copy carries the lines of the place of use; the children are shared.
                        Attach(target is null
                            ? new DocNode { Kind = DocKind.Scalar, StartLine = line, EndLine = line, Type = ScalarType.Null }
                            : new DocNode
                            {
                                Kind = target.Kind, StartLine = line, EndLine = line, Text = target.Text, Type = target.Type,
                                Entries = target.Entries, Items = target.Items, Size = target.Size, Height = target.Height,
                            });
                        break;
                    }
                }
            }
        }
        catch (YamlException exception)
        {
            line = (int)exception.Start.Line;
            return Fail("yaml-syntax");
        }
        catch (InvalidOperationException)
        {
            // The scanner of YamlDotNet throws this for a flow collection that is not closed.
            // The line is the one of the last node that was read.
            return Fail("yaml-syntax");
        }

        return stack.Count == 0 ? new ParsedDocuments(roots, null, 0) : Fail("yaml-syntax");
    }

    /// <summary>The content with every line of only spaces and tabs made empty. The line count stays.</summary>
    private static string BlankTabLines(string content)
    {
        if (!content.Contains('\t'))
            return content;
        var builder = new StringBuilder(content.Length);
        var from = 0;
        while (from <= content.Length)
        {
            var to = content.IndexOf('\n', from);
            if (to < 0)
                to = content.Length;
            var text = content.AsSpan(from, to - from);
            if (text.IndexOfAnyExcept(' ', '\t') >= 0)
                builder.Append(text);
            if (to < content.Length)
                builder.Append('\n');
            from = to + 1;
        }
        return builder.ToString();
    }

    /// <summary>For each line number, the last line at or before it that is not blank. Index 0 is 0.</summary>
    private static int[] LastTextLines(string content)
    {
        var result = new List<int> { 0 };
        var from = 0;
        while (from <= content.Length)
        {
            var to = content.IndexOf('\n', from);
            if (to < 0)
                to = content.Length;
            var line = result.Count;
            result.Add(content.AsSpan(from, to - from).IsWhiteSpace() ? result[line - 1] : line);
            from = to + 1;
        }
        return [.. result];
    }

    private static string? AnchorOf(NodeEvent node) => node.Anchor.IsEmpty ? null : node.Anchor.Value;

    private static ScalarType PlainType(string value) => value switch
    {
        "" or "~" or "null" or "Null" or "NULL" => ScalarType.Null,
        "true" or "True" or "TRUE" or "false" or "False" or "FALSE" => ScalarType.Bool,
        _ => value.Length <= 40 && JsonNumber().IsMatch(value) ? ScalarType.Number : ScalarType.String,
    };

    public static ParsedDocuments ParseJson(string content, int maxNesting, WorkMeter? meter = null)
    {
        if (meter is not null && !meter.Charge(content.Length))
            return new ParsedDocuments([], "limit:work", 0);
        var bytes = Encoding.UTF8.GetBytes(content);
        var lineStarts = new List<int> { 0 };
        for (var i = 0; i < bytes.Length; i++)
            if (bytes[i] == (byte)'\n')
                lineStarts.Add(i + 1);

        int LineOf(long offset)
        {
            var index = lineStarts.BinarySearch((int)Math.Min(offset, int.MaxValue));
            return index >= 0 ? index + 1 : ~index;
        }

        var roots = new List<DocNode>();
        var stack = new Stack<Frame>();
        var line = 1;
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions
        {
            MaxDepth = Math.Max(1, maxNesting), CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true,
        });

        void Attach(DocNode node)
        {
            if (stack.Count == 0)
            {
                roots.Add(node);
                return;
            }
            var frame = stack.Peek();
            if (frame.Node.Kind == DocKind.Seq)
                frame.Node.Items.Add(node);
            else
                frame.Node.Entries.Add(new DocEntry(frame.Key, frame.KeyLine, node));
        }

        try
        {
            while (reader.Read())
            {
                line = LineOf(reader.TokenStartIndex);
                switch (reader.TokenType)
                {
                    case JsonTokenType.StartObject:
                        stack.Push(new Frame(new DocNode { Kind = DocKind.Map, StartLine = line, EndLine = line }, null, true));
                        break;
                    case JsonTokenType.StartArray:
                        stack.Push(new Frame(new DocNode { Kind = DocKind.Seq, StartLine = line, EndLine = line }, null, true));
                        break;
                    case JsonTokenType.EndObject or JsonTokenType.EndArray:
                    {
                        var node = stack.Pop().Node;
                        node.EndLine = line;
                        var size = 1;
                        foreach (var child in node.Kind == DocKind.Map ? node.Entries.Select(e => e.Value) : node.Items)
                            size += child.Size;
                        node.Size = size;
                        Attach(node);
                        break;
                    }
                    case JsonTokenType.PropertyName:
                        stack.Peek().Key = reader.GetString() ?? "";
                        stack.Peek().KeyLine = line;
                        break;
                    case JsonTokenType.String:
                        Attach(new DocNode { Kind = DocKind.Scalar, StartLine = line, EndLine = line, Text = reader.GetString() ?? "", Type = ScalarType.String });
                        break;
                    case JsonTokenType.Number:
                        Attach(new DocNode { Kind = DocKind.Scalar, StartLine = line, EndLine = line, Text = Encoding.UTF8.GetString(reader.ValueSpan), Type = ScalarType.Number });
                        break;
                    case JsonTokenType.True or JsonTokenType.False:
                        Attach(new DocNode { Kind = DocKind.Scalar, StartLine = line, EndLine = line, Text = reader.TokenType == JsonTokenType.True ? "true" : "false", Type = ScalarType.Bool });
                        break;
                    case JsonTokenType.Null:
                        Attach(new DocNode { Kind = DocKind.Scalar, StartLine = line, EndLine = line, Type = ScalarType.Null });
                        break;
                }
            }
        }
        catch (JsonException exception)
        {
            var failedAt = exception.LineNumber is { } number ? (int)Math.Min(number + 1, int.MaxValue) : line;
            return new ParsedDocuments([], stack.Count >= maxNesting ? "limit:nesting" : "json-syntax", failedAt);
        }
        catch (InvalidOperationException)
        {
            return new ParsedDocuments([], "json-syntax", line);
        }

        return new ParsedDocuments(roots, null, 0);
    }
}

/// <summary>Which document of a file an extractor reads.</summary>
internal static class DocumentSet
{
    /// <summary>
    /// The one document of the file that is a mapping and that <paramref name="qualifies"/>.
    /// Every document is looked at, so empty documents before it do not hide it. When more
    /// than one document qualifies, the file gives nothing and a <c>multiple-documents</c>
    /// note: the keys of the facts hold the file and not the document, so two documents
    /// would be merged into one claim.
    /// </summary>
    public static DocNode? Single(ExtractionContext context, SourceText file, Func<DocNode, bool> qualifies)
    {
        var meter = context.Meter(file);
        DocNode? found = null;
        foreach (var root in context.Documents(file))
        {
            if (!meter.Charge())
                return null;
            if (root.Kind != DocKind.Map || !qualifies(root))
                continue;
            if (found is not null)
            {
                context.Note(NoteKind.Undecided, "multiple-documents", file.Path, root.StartLine);
                return null;
            }
            found = root;
        }
        return found;
    }
}

/// <summary>A <c>$ref</c> inside one document: <c>#/a/b</c>, with <c>~1</c> for '/' and <c>~0</c> for '~'.</summary>
internal static class JsonPointer
{
    /// <summary>
    /// The segments of a local reference, or null when the reference leaves the document, is
    /// not a pointer or has more than <paramref name="maxSegments"/> segments.
    /// </summary>
    public static List<string>? Segments(string reference, int maxSegments = 16)
    {
        if (!reference.StartsWith("#/", StringComparison.Ordinal))
            return null;
        var segments = new List<string>();
        var from = 2;
        while (from <= reference.Length)
        {
            if (segments.Count >= maxSegments)
                return null;
            var to = reference.IndexOf('/', from);
            if (to < 0)
                to = reference.Length;
            var raw = reference[from..to];
            segments.Add(raw.Contains('~') ? raw.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal) : raw);
            from = to + 1;
        }
        return segments;
    }

    /// <summary>The node the segments lead to through mappings, or null.</summary>
    public static DocNode? Resolve(DocNode root, List<string> segments)
    {
        var node = root;
        foreach (var segment in segments)
        {
            if (node.Get(segment) is not { } next)
                return null;
            node = next;
        }
        return node;
    }
}

/// <summary>
/// Writes the schema bodies of one file. A body in a fact carries the STRUCTURE of a schema
/// and never a value of the document:
/// <list type="bullet">
/// <item>Only the keywords in the list of this class are written. Every other keyword is left
/// out, and with it <c>default</c>, <c>const</c>, <c>example</c>, <c>examples</c>, <c>pattern</c>,
/// <c>patternProperties</c>, <c>$id</c>, <c>externalDocs</c> and every <c>x-*</c> extension, at
/// every depth and inside <c>allOf</c>, <c>oneOf</c>, <c>anyOf</c> and sequences.</item>
/// <item>The keys of <c>properties</c>, <c>$defs</c>, <c>definitions</c> and
/// <c>dependentSchemas</c> are NAMES and stay, also a property named <c>default</c>,
/// <c>example</c> or <c>enum</c>.</item>
/// <item><c>enum</c> stays only when every member is null or an identifier token
/// (<see cref="CredentialGuard.IsIdentifierToken"/>: this takes numbers and booleans too), and
/// no property above it has a credential-like name. Otherwise it is left out with an
/// <c>enum-not-emitted</c> note.</item>
/// <item><c>description</c> and <c>title</c> stay unless they look like a credential; then that
/// one text is left out with a <c>description-not-emitted</c> note and the rest of the schema stays.</item>
/// <item><c>type</c> and <c>format</c> stay when they are identifier tokens; numeric bounds stay
/// when they are numbers or booleans; <c>required</c> stays as a list of names.</item>
/// <item>A <c>$ref</c> inside the document stays as written. One that leaves it is written as
/// <c>external-ref</c>.</item>
/// </list>
/// Keys are sorted, so reordering a schema does not read as a change. One body is at most
/// <see cref="ExtractionLimits.MaxSchemaBytes"/> and the writer stops when it passes that. The
/// bodies of one file together are at most four times that, so a schema that is used through
/// many YAML aliases cannot multiply the output. The depth of the recursion is the depth of
/// the document, which the reader holds to <see cref="ExtractionLimits.MaxNesting"/>.
/// </summary>
internal sealed class SchemaBody(ExtractionContext context, SourceText file)
{
    private enum Keyword { Schema, SchemaList, SchemaMap, Flag, Number, Token, Type, Text, Names, Enum, Reference, Discriminator }

    private static readonly Dictionary<string, Keyword> Keywords = new(StringComparer.Ordinal)
    {
        ["items"] = Keyword.Schema, ["additionalProperties"] = Keyword.Schema, ["not"] = Keyword.Schema,
        ["if"] = Keyword.Schema, ["then"] = Keyword.Schema, ["else"] = Keyword.Schema, ["contains"] = Keyword.Schema,
        ["propertyNames"] = Keyword.Schema, ["unevaluatedProperties"] = Keyword.Schema,
        ["unevaluatedItems"] = Keyword.Schema, ["additionalItems"] = Keyword.Schema,
        ["allOf"] = Keyword.SchemaList, ["oneOf"] = Keyword.SchemaList, ["anyOf"] = Keyword.SchemaList, ["prefixItems"] = Keyword.SchemaList,
        ["properties"] = Keyword.SchemaMap, ["$defs"] = Keyword.SchemaMap, ["definitions"] = Keyword.SchemaMap, ["dependentSchemas"] = Keyword.SchemaMap,
        ["nullable"] = Keyword.Flag, ["readOnly"] = Keyword.Flag, ["writeOnly"] = Keyword.Flag, ["deprecated"] = Keyword.Flag, ["uniqueItems"] = Keyword.Flag,
        ["minimum"] = Keyword.Number, ["maximum"] = Keyword.Number, ["exclusiveMinimum"] = Keyword.Number, ["exclusiveMaximum"] = Keyword.Number,
        ["multipleOf"] = Keyword.Number, ["minLength"] = Keyword.Number, ["maxLength"] = Keyword.Number,
        ["minItems"] = Keyword.Number, ["maxItems"] = Keyword.Number, ["minProperties"] = Keyword.Number, ["maxProperties"] = Keyword.Number,
        ["minContains"] = Keyword.Number, ["maxContains"] = Keyword.Number,
        ["format"] = Keyword.Token, ["type"] = Keyword.Type,
        ["description"] = Keyword.Text, ["title"] = Keyword.Text,
        ["required"] = Keyword.Names, ["enum"] = Keyword.Enum, ["$ref"] = Keyword.Reference, ["discriminator"] = Keyword.Discriminator,
    };

    private static readonly JsonWriterOptions Options = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping, Indented = false, SkipValidation = true,
    };

    private readonly WorkMeter _meter = context.Meter(file);
    private readonly int _maxBytes = context.Limits.MaxSchemaBytes;
    private long _remaining = 4L * context.Limits.MaxSchemaBytes;
    private bool _tooLarge;

    /// <summary>
    /// The body of a schema, or null with a <c>limit:schema-bytes</c> or
    /// <c>limit:schema-bytes-per-file</c> note at <paramref name="line"/> when a bound is passed.
    /// </summary>
    public string? Write(DocNode schema, int line)
    {
        if (_meter.Exhausted)
            return null;
        if (_remaining <= 0)
        {
            context.LimitReached("schema-bytes-per-file", file.Path, stop: false, line);
            return null;
        }
        _tooLarge = false;
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream, Options);
        var written = Schema(writer, schema, sensitive: false, depth: 0);
        writer.Flush();
        if (!written || stream.Length > _maxBytes)
        {
            if (_tooLarge || stream.Length > _maxBytes)
                context.LimitReached("schema-bytes", file.Path, stop: false, line);
            return null;
        }
        if (stream.Length > _remaining)
        {
            _remaining = 0;
            context.LimitReached("schema-bytes-per-file", file.Path, stop: false, line);
            return null;
        }
        _remaining -= stream.Length;
        return Encoding.UTF8.GetString(stream.GetBuffer(), 0, (int)stream.Length);
    }

    /// <summary>False when the writer stops: the body is too large or the work of the file is spent.</summary>
    private bool Schema(Utf8JsonWriter writer, DocNode node, bool sensitive, int depth)
    {
        if (writer.BytesCommitted + writer.BytesPending > _maxBytes || depth > context.Limits.MaxNesting)
        {
            _tooLarge = true;
            return false;
        }
        if (!_meter.Charge())
            return false;
        if (node.Kind != DocKind.Map)
        {
            // "true" and "false" are schemas. Anything else in the place of a schema is not one.
            if (node is { Kind: DocKind.Scalar, Type: ScalarType.Bool })
                writer.WriteBooleanValue(IsTrue(node));
            else
                writer.WriteNullValue();
            return true;
        }

        if (!_meter.Charge(node.Entries.Count))
            return false;
        var kept = new List<(string Key, Keyword Kind, DocEntry Entry)>();
        foreach (var entry in node.Entries)
            if (Keywords.TryGetValue(entry.Key, out var kind))
                kept.Add((entry.Key, kind, entry));

        writer.WriteStartObject();
        string? previous = null;
        // The sort is stable, so the first of two entries with one key is the one written.
        foreach (var (key, kind, entry) in kept.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            if (key == previous)
                continue;
            previous = key;
            if (!Member(writer, key, kind, entry, sensitive, depth))
                return false;
        }
        writer.WriteEndObject();
        return true;
    }

    private bool Member(Utf8JsonWriter writer, string key, Keyword kind, DocEntry entry, bool sensitive, int depth)
    {
        var value = entry.Value;
        switch (kind)
        {
            case Keyword.Schema when value.Kind != DocKind.Seq:
                writer.WritePropertyName(key);
                return Schema(writer, value, sensitive, depth + 1);
            case Keyword.Schema or Keyword.SchemaList when value.Kind == DocKind.Seq:
                writer.WritePropertyName(key);
                writer.WriteStartArray();
                foreach (var item in value.Items)
                    if (!Schema(writer, item, sensitive, depth + 1))
                        return false;
                writer.WriteEndArray();
                return true;
            case Keyword.SchemaMap when value.Kind == DocKind.Map:
            {
                if (!_meter.Charge(value.Entries.Count))
                    return false;
                writer.WritePropertyName(key);
                writer.WriteStartObject();
                string? previous = null;
                foreach (var property in value.Entries.OrderBy(e => e.Key, StringComparer.Ordinal))
                {
                    if (property.Key == previous)
                        continue;
                    previous = property.Key;
                    writer.WritePropertyName(property.Key);
                    if (!Schema(writer, property.Value, sensitive || CredentialGuard.IsCredentialName(property.Key), depth + 1))
                        return false;
                }
                writer.WriteEndObject();
                return true;
            }
            case Keyword.Flag or Keyword.Number when value is { Kind: DocKind.Scalar, Type: ScalarType.Bool }:
                writer.WriteBoolean(key, IsTrue(value));
                return true;
            case Keyword.Number when value is { Kind: DocKind.Scalar, Type: ScalarType.Number }:
                writer.WritePropertyName(key);
                writer.WriteRawValue(value.Text, skipInputValidation: true);
                return true;
            case Keyword.Token or Keyword.Type when value.AsText() is { } token && CredentialGuard.IsIdentifierToken(token):
                writer.WriteString(key, token);
                return true;
            case Keyword.Type when value.Kind == DocKind.Seq:
                if (!_meter.Charge(value.Items.Count))
                    return false;
                writer.WritePropertyName(key);
                writer.WriteStartArray();
                foreach (var item in value.Items)
                    if (item.AsText() is { } type && CredentialGuard.IsIdentifierToken(type))
                        writer.WriteStringValue(type);
                writer.WriteEndArray();
                return true;
            case Keyword.Text when value.AsText() is { } text:
                if (CredentialGuard.LooksSecret(text))
                    context.Note(NoteKind.Redacted, "description-not-emitted", file.Path, entry.KeyLine);
                else
                    writer.WriteString(key, text);
                return true;
            case Keyword.Names when value.Kind == DocKind.Seq:
                if (!_meter.Charge(value.Items.Count))
                    return false;
                writer.WritePropertyName(key);
                writer.WriteStartArray();
                foreach (var item in value.Items)
                    if (item.AsText() is { } name)
                        writer.WriteStringValue(name);
                writer.WriteEndArray();
                return true;
            case Keyword.Enum when value.Kind == DocKind.Seq:
                if (!_meter.Charge(value.Items.Count))
                    return false;
                if (sensitive || !value.Items.TrueForAll(IsEnumMember))
                {
                    context.Note(NoteKind.Redacted, "enum-not-emitted", file.Path, entry.KeyLine);
                    return true;
                }
                writer.WritePropertyName(key);
                writer.WriteStartArray();
                foreach (var item in value.Items)
                {
                    switch (item.Type)
                    {
                        case ScalarType.Null: writer.WriteNullValue(); break;
                        case ScalarType.Bool: writer.WriteBooleanValue(IsTrue(item)); break;
                        case ScalarType.Number: writer.WriteRawValue(item.Text, skipInputValidation: true); break;
                        default: writer.WriteStringValue(item.Text); break;
                    }
                }
                writer.WriteEndArray();
                return true;
            case Keyword.Reference when value.AsText() is { } reference:
                writer.WriteString(key, reference.StartsWith('#') ? reference : "external-ref");
                return true;
            case Keyword.Discriminator when value.Kind == DocKind.Map && value.Str("propertyName") is { } propertyName:
                writer.WritePropertyName(key);
                writer.WriteStartObject();
                writer.WriteString("propertyName", propertyName);
                writer.WriteEndObject();
                return true;
            default:
                // The value has a shape that the keyword does not take. The keyword is left out.
                return true;
        }
    }

    private static bool IsEnumMember(DocNode item) =>
        item.Kind == DocKind.Scalar && (item.Type == ScalarType.Null || CredentialGuard.IsIdentifierToken(item.Text));

    private static bool IsTrue(DocNode node) => node.Text is "true" or "True" or "TRUE";
}
