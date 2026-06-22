using Vellum.Kernel.EventTypes;

namespace Vellum.Tests.Kernel.EventTypes;

public class EventTypeRegistryTests
{
    private sealed record SomeEvent(string Value);

    [Fact]
    public void Register_and_resolve_by_clr_type()
    {
        var registry = new EventTypeRegistry();
        registry.Register<SomeEvent>("test.some.v1");

        Assert.Equal("test.some.v1", registry.GetTypeName(typeof(SomeEvent)));
    }

    [Fact]
    public void Register_and_resolve_by_type_name()
    {
        var registry = new EventTypeRegistry();
        registry.Register<SomeEvent>("test.some.v1");

        Assert.Equal(typeof(SomeEvent), registry.GetClrType("test.some.v1"));
    }

    [Fact]
    public void GetTypeName_throws_for_unregistered_type()
    {
        var registry = new EventTypeRegistry();

        Assert.Throws<InvalidOperationException>(() => registry.GetTypeName(typeof(SomeEvent)));
    }

    [Fact]
    public void GetClrType_throws_for_unregistered_name()
    {
        var registry = new EventTypeRegistry();

        Assert.Throws<InvalidOperationException>(() => registry.GetClrType("unknown.v1"));
    }
}
