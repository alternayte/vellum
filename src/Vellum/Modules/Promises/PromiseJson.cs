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
}
