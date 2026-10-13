using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;
using YamlScalar = YamlDotNet.Core.Events.Scalar;

namespace Vellum.Modules.Modelling.Export;

/// <summary>
/// YAML is a second spelling of the JSON export document, not a second format. The export
/// writes the JSON document as YAML, and the import turns YAML back into that JSON and reads
/// it with the JSON reader. So both formats have one set of member names, one way to write
/// an ID or a time stamp, and one reader that decides what is valid.
/// </summary>
public static class ModelYaml
{
    // The export document nests five levels (document, list, entity, provenance, value).
    private const int MaxDepth = 16;

    private static readonly ISerializer Serializer = new SerializerBuilder()
        // A text value that another YAML reader would take for a number, a boolean or null
        // (a name such as "123" or "true") is written in quotes.
        .WithQuotingNecessaryStrings()
        .Build();

    // The reader below gives every scalar as text, because YAML does not say whether `123` is
    // a number or a name. The member's type decides: a number is read from its text. The
    // export document has no boolean member.
    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    public static string Write(ModelExportDocument document, JsonSerializerOptions jsonOptions)
    {
        using var json = JsonSerializer.SerializeToDocument(document, jsonOptions);
        return Serializer.Serialize(Plain(json.RootElement)!);
    }

    /// <summary>The document, or null when the text holds none.</summary>
    /// <exception cref="YamlException">The text is not YAML this reader accepts.</exception>
    /// <exception cref="JsonException">The YAML is not an export document.</exception>
    public static ModelExportDocument? Read(string yaml)
    {
        var parser = new Parser(new StringReader(yaml));
        parser.Consume<StreamStart>();
        if (!parser.TryConsume<DocumentStart>(out _))
            return null;

        return Node(parser, 1).Deserialize<ModelExportDocument>(ReadOptions);
    }

    private static object? Plain(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object => value.EnumerateObject().ToDictionary(p => p.Name, p => Plain(p.Value)),
        JsonValueKind.Array => value.EnumerateArray().Select(Plain).ToList(),
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number => value.TryGetInt64(out var whole) ? whole : value.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null,
    };

    // Reads parser events, not a node graph. An alias is refused: a few lines of nested
    // aliases expand to gigabytes. The depth limit bounds this recursion.
    private static JsonNode? Node(IParser parser, int depth)
    {
        if (depth > MaxDepth)
            throw new YamlException(parser.Current!.Start, parser.Current.End, $"The document nests more than {MaxDepth} levels.");

        if (parser.TryConsume<YamlScalar>(out var scalar))
        {
            var isNull = scalar.Style == ScalarStyle.Plain && scalar.Value is "" or "~" or "null" or "Null" or "NULL";
            return isNull ? null : JsonValue.Create(scalar.Value);
        }

        if (parser.TryConsume<SequenceStart>(out _))
        {
            var list = new JsonArray();
            while (!parser.TryConsume<SequenceEnd>(out _))
                list.Add(Node(parser, depth + 1));
            return list;
        }

        if (parser.TryConsume<MappingStart>(out _))
        {
            var map = new JsonObject();
            while (!parser.TryConsume<MappingEnd>(out _))
            {
                var key = parser.Consume<YamlScalar>();
                map[key.Value] = Node(parser, depth + 1);
            }
            return map;
        }

        throw new YamlException(parser.Current!.Start, parser.Current.End, "Anchors and aliases are not supported.");
    }
}
