using System.Text;
using System.Text.Encodings.Web;
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

    public DocEntry? Entry(string key)
    {
        if (Kind == DocKind.Map)
            foreach (var entry in Entries)
                if (string.Equals(entry.Key, key, StringComparison.Ordinal))
                    return entry;
        return null;
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
/// is out of proportion to its size.
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

    public static ParsedDocuments ParseYaml(string content, int maxNesting)
    {
        var roots = new List<DocNode>();
        var stack = new Stack<Frame>();
        var anchors = new Dictionary<string, DocNode>(StringComparer.Ordinal);
        var aliases = 0;
        long aliasedNodes = 0;
        var line = 1;

        ParsedDocuments Fail(string reason) => new([], reason, line);

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
            line = Math.Max(1, (int)exception.Start.Line);
            return Fail("yaml-syntax");
        }

        return stack.Count == 0 ? new ParsedDocuments(roots, null, 0) : Fail("yaml-syntax");
    }

    private static string? AnchorOf(NodeEvent node) => node.Anchor.IsEmpty ? null : node.Anchor.Value;

    private static ScalarType PlainType(string value) => value switch
    {
        "" or "~" or "null" or "Null" or "NULL" => ScalarType.Null,
        "true" or "True" or "TRUE" or "false" or "False" or "FALSE" => ScalarType.Bool,
        _ => value.Length <= 40 && JsonNumber().IsMatch(value) ? ScalarType.Number : ScalarType.String,
    };

    public static ParsedDocuments ParseJson(string content, int maxNesting)
    {
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

    private static readonly JsonWriterOptions CanonicalOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, Indented = false, SkipValidation = true,
    };

    /// <summary>
    /// The node as JSON with sorted keys, so that reordering a schema does not read as a
    /// change. Examples and extension fields are left out, a string that looks like a
    /// credential is replaced, and a fixed value of a property with a sensitive name is
    /// left out. Null when the JSON is larger than <paramref name="maxBytes"/>.
    /// </summary>
    public static string? ToCanonicalJson(DocNode node, int maxBytes)
    {
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream, CanonicalOptions);
        if (!Write(writer, node, maxBytes, false))
            return null;
        writer.Flush();
        return stream.Length > maxBytes ? null : Encoding.UTF8.GetString(stream.GetBuffer(), 0, (int)stream.Length);
    }

    private static bool Write(Utf8JsonWriter writer, DocNode node, int maxBytes, bool sensitive)
    {
        if (writer.BytesCommitted + writer.BytesPending > maxBytes)
            return false;
        switch (node.Kind)
        {
            case DocKind.Map:
                writer.WriteStartObject();
                string? previous = null;
                foreach (var entry in node.Entries.OrderBy(e => e.Key, StringComparer.Ordinal))
                {
                    if (entry.Key == previous)
                        continue;
                    previous = entry.Key;
                    if (entry.Key is "example" or "examples" || entry.Key.StartsWith("x-", StringComparison.Ordinal))
                        continue;
                    if (sensitive && entry.Key is "default" or "const" or "enum")
                        continue;
                    writer.WritePropertyName(entry.Key);
                    if (!Write(writer, entry.Value, maxBytes, IsSensitiveName(entry.Key)))
                        return false;
                }
                writer.WriteEndObject();
                return true;
            case DocKind.Seq:
                writer.WriteStartArray();
                foreach (var item in node.Items)
                    if (!Write(writer, item, maxBytes, false))
                        return false;
                writer.WriteEndArray();
                return true;
            default:
                switch (node.Type)
                {
                    case ScalarType.Null: writer.WriteNullValue(); break;
                    case ScalarType.Bool: writer.WriteBooleanValue(node.Text is "true" or "True" or "TRUE"); break;
                    case ScalarType.Number when node.Text.Length <= 40 && JsonNumber().IsMatch(node.Text): writer.WriteRawValue(node.Text, skipInputValidation: true); break;
                    default: writer.WriteStringValue(CredentialGuard.LooksSecret(node.Text) ? "[redacted]" : node.Text); break;
                }
                return true;
        }
    }

    private static readonly string[] SensitiveWords = ["password", "passwd", "secret", "token", "apikey", "api_key", "credential", "private"];

    private static bool IsSensitiveName(string name)
    {
        foreach (var word in SensitiveWords)
            if (name.Contains(word, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }
}
