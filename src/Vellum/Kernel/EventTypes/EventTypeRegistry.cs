namespace Vellum.Kernel.EventTypes;

public sealed class EventTypeRegistry : IEventTypeRegistry
{
    private readonly Dictionary<Type, string> _clrToName = new();
    private readonly Dictionary<string, Type> _nameToClr = new();

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
}
