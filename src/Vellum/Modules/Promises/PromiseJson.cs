using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Vellum.Modules.Trace.Engine;

namespace Vellum.Modules.Promises;

/// <summary>
/// The JSON of the promise read tables and of every hash this module makes: one set of
/// options, so what is written reads back. <c>jsonb</c> sorts the keys of an object, so a
/// fault or an assertion is read with its <c>type</c> at any place.
/// </summary>
internal static class PromiseJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        AllowOutOfOrderMetadataProperties = true,
        Converters =
        {
            new JsonStringEnumConverter<StepOutcome>(),
            new JsonStringEnumConverter<FaultKind>(),
            new JsonStringEnumConverter<TraceEventKind>(),
        },
    };

    public static string Write<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T Read<T>(string json) => JsonSerializer.Deserialize<T>(json, Options)!;

    /// <summary>SHA-256 of the canonical JSON of a value, in lower-case hex.</summary>
    public static string Hash<T>(T value)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
            CanonicalJson.Write(writer, JsonSerializer.SerializeToElement(value, Options));
        return Convert.ToHexStringLower(SHA256.HashData(buffer.GetBuffer().AsSpan(0, (int)buffer.Length)));
    }

    /// <summary>
    /// SHA-256 of the canonical JSON of a value with every null member left out. A hash that
    /// is stored and compared later (the content a person approved, the obligation a run was
    /// made for) uses this form, so a member that is added later and is null for old content
    /// does not change the hash of that content.
    /// </summary>
    public static string StableHash<T>(T value)
    {
        var node = JsonSerializer.SerializeToNode(value, Options);
        DropNulls(node);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
            CanonicalJson.Write(writer, JsonSerializer.SerializeToElement(node, Options));
        return Convert.ToHexStringLower(SHA256.HashData(buffer.GetBuffer().AsSpan(0, (int)buffer.Length)));
    }

    private static void DropNulls(System.Text.Json.Nodes.JsonNode? node)
    {
        switch (node)
        {
            case System.Text.Json.Nodes.JsonObject obj:
                foreach (var name in obj.Where(p => p.Value is null).Select(p => p.Key).ToList())
                    obj.Remove(name);
                foreach (var (_, value) in obj)
                    DropNulls(value);
                break;
            case System.Text.Json.Nodes.JsonArray array:
                foreach (var item in array)
                    DropNulls(item);
                break;
        }
    }
}
