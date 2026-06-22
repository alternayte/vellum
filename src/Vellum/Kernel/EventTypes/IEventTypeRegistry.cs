namespace Vellum.Kernel.EventTypes;

public interface IEventTypeRegistry
{
    string GetTypeName(Type clrType);
    Type GetClrType(string typeName);
    void Register<T>(string typeName) where T : class;
}
