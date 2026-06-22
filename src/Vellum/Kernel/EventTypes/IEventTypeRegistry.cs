using System.Text.Json;
using System.Text.Json.Nodes;

namespace Vellum.Kernel.EventTypes;

public interface IEventTypeRegistry
{
    string GetTypeName(Type clrType);
    Type GetClrType(string typeName);
    void Register<T>(string typeName) where T : class;
    void RegisterUpcast(string fromTypeName, string toTypeName, Func<JsonNode, JsonNode> transform);
    object DeserializeEvent(string storedTypeName, JsonDocument payload);
}
