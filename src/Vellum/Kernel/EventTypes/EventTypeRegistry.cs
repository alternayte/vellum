using System.Text.Json;
using System.Text.Json.Nodes;

namespace Vellum.Kernel.EventTypes;

public sealed class EventTypeRegistry : IEventTypeRegistry
{
    private readonly Dictionary<Type, string> _clrToName = new();
    private readonly Dictionary<string, Type> _nameToClr = new();
    private readonly Dictionary<string, (string TargetTypeName, Func<JsonNode, JsonNode> Transform)> _upcasts = new();

    private static readonly JsonSerializerOptions DeserializeOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public void Register<T>(string typeName) where T : class
    {
        _clrToName[typeof(T)] = typeName;
        _nameToClr[typeName] = typeof(T);
    }

    public string GetTypeName(Type clrType) =>
        _clrToName.TryGetValue(clrType, out var name)
            ? name
            : throw new InvalidOperationException($"No type name registered for {clrType.FullName}");

    public Type GetClrType(string typeName) =>
        _nameToClr.TryGetValue(typeName, out var type)
            ? type
            : throw new InvalidOperationException($"No CLR type registered for '{typeName}'");

    public void RegisterUpcast(
        string fromTypeName,
        string toTypeName,
        Func<JsonNode, JsonNode> transform)
    {
        _upcasts[fromTypeName] = (toTypeName, transform);
    }

    public object DeserializeEvent(string storedTypeName, JsonDocument payload)
    {
        var currentTypeName = storedTypeName;
        var node = JsonNode.Parse(payload.RootElement.GetRawText())!;

        while (_upcasts.TryGetValue(currentTypeName, out var upcast))
        {
            node = upcast.Transform(node);
            currentTypeName = upcast.TargetTypeName;
        }

        var clrType = GetClrType(currentTypeName);
        return node.Deserialize(clrType, DeserializeOptions)
               ?? throw new InvalidOperationException($"Failed to deserialize event '{currentTypeName}'");
    }
}
