using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vellum.Modules.Modelling;

/// <summary>
/// An identity for an architectural fact. Typed IDs prevent cross-kind references
/// (an element ID cannot be passed where a message ID is expected). They serialize as
/// plain GUID strings so existing event payloads and stream snapshots stay compatible.
/// </summary>
public interface IArchitectureId
{
    Guid Value { get; }
}

[JsonConverter(typeof(ElementIdJsonConverter))]
public readonly record struct ElementId(Guid Value) : IArchitectureId
{
    public static implicit operator ElementId(Guid value) => new(value);
    public static implicit operator Guid(ElementId id) => id.Value;
    public override string ToString() => Value.ToString();
}

[JsonConverter(typeof(RelationshipIdJsonConverter))]
public readonly record struct RelationshipId(Guid Value) : IArchitectureId
{
    public static implicit operator RelationshipId(Guid value) => new(value);
    public static implicit operator Guid(RelationshipId id) => id.Value;
    public override string ToString() => Value.ToString();
}

[JsonConverter(typeof(MessageIdJsonConverter))]
public readonly record struct MessageId(Guid Value) : IArchitectureId
{
    public static implicit operator MessageId(Guid value) => new(value);
    public static implicit operator Guid(MessageId id) => id.Value;
    public override string ToString() => Value.ToString();
}

[JsonConverter(typeof(SchemaIdJsonConverter))]
public readonly record struct SchemaId(Guid Value) : IArchitectureId
{
    public static implicit operator SchemaId(Guid value) => new(value);
    public static implicit operator Guid(SchemaId id) => id.Value;
    public override string ToString() => Value.ToString();
}

/// <summary>
/// Base converter for GUID-backed IDs. Writes plain GUID strings and supports use as
/// dictionary keys, which keeps serialized state compatible with pre-typed-ID payloads.
/// </summary>
public abstract class GuidIdJsonConverter<T> : JsonConverter<T> where T : struct
{
    protected abstract T FromGuid(Guid value);
    protected abstract Guid ToGuid(T value);

    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String && Guid.TryParse(reader.GetString(), out var guid))
            return FromGuid(guid);

        throw new JsonException($"Expected a GUID string for {typeof(T).Name}");
    }

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) =>
        writer.WriteStringValue(ToGuid(value));

    public override T ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (Guid.TryParse(reader.GetString(), out var guid))
            return FromGuid(guid);

        throw new JsonException($"Expected a GUID property name for {typeof(T).Name}");
    }

    public override void WriteAsPropertyName(Utf8JsonWriter writer, T value, JsonSerializerOptions options) =>
        writer.WritePropertyName(ToGuid(value).ToString());
}

public static class ArchitectureIdArrays
{
    public static ElementId[] ToElementIds(this IEnumerable<Guid> values) =>
        values.Select(value => new ElementId(value)).ToArray();

    public static Guid[] ToGuids(this IEnumerable<ElementId> values) =>
        values.Select(value => value.Value).ToArray();
}

public sealed class ElementIdJsonConverter : GuidIdJsonConverter<ElementId>
{
    protected override ElementId FromGuid(Guid value) => new(value);
    protected override Guid ToGuid(ElementId value) => value.Value;
}

public sealed class RelationshipIdJsonConverter : GuidIdJsonConverter<RelationshipId>
{
    protected override RelationshipId FromGuid(Guid value) => new(value);
    protected override Guid ToGuid(RelationshipId value) => value.Value;
}

public sealed class MessageIdJsonConverter : GuidIdJsonConverter<MessageId>
{
    protected override MessageId FromGuid(Guid value) => new(value);
    protected override Guid ToGuid(MessageId value) => value.Value;
}

public sealed class SchemaIdJsonConverter : GuidIdJsonConverter<SchemaId>
{
    protected override SchemaId FromGuid(Guid value) => new(value);
    protected override Guid ToGuid(SchemaId value) => value.Value;
}
