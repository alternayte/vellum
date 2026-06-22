using System.Text.Json;
using System.Text.Json.Nodes;
using Vellum.Kernel.EventTypes;

namespace Vellum.Tests.Kernel.EventTypes;

public class UpcastingTests
{
    private sealed record ItemAddedV2(string Name, int Quantity);

    [Fact]
    public void Deserialize_current_version_without_upcasting()
    {
        var registry = new EventTypeRegistry();
        registry.Register<ItemAddedV2>("item.added.v2");

        var payload = JsonSerializer.SerializeToDocument(new { name = "Widget", quantity = 5 });

        var result = registry.DeserializeEvent("item.added.v2", payload);

        var typed = Assert.IsType<ItemAddedV2>(result);
        Assert.Equal("Widget", typed.Name);
        Assert.Equal(5, typed.Quantity);
    }

    [Fact]
    public void Upcast_v1_to_v2_adds_missing_field()
    {
        var registry = new EventTypeRegistry();
        registry.Register<ItemAddedV2>("item.added.v2");

        registry.RegisterUpcast("item.added.v1", "item.added.v2", node =>
        {
            node["quantity"] = 1;
            return node;
        });

        var v1Payload = JsonSerializer.SerializeToDocument(new { name = "Widget" });

        var result = registry.DeserializeEvent("item.added.v1", v1Payload);

        var typed = Assert.IsType<ItemAddedV2>(result);
        Assert.Equal("Widget", typed.Name);
        Assert.Equal(1, typed.Quantity);
    }

    [Fact]
    public void Upcast_chain_v1_to_v2_to_v3()
    {
        var registry = new EventTypeRegistry();

        // v3 is the current type
        registry.Register<ItemAddedV3>("item.added.v3");

        registry.RegisterUpcast("item.added.v1", "item.added.v2", node =>
        {
            node["quantity"] = 1;
            return node;
        });
        registry.RegisterUpcast("item.added.v2", "item.added.v3", node =>
        {
            node["category"] = "default";
            return node;
        });

        var v1Payload = JsonSerializer.SerializeToDocument(new { name = "Widget" });

        var result = registry.DeserializeEvent("item.added.v1", v1Payload);

        var typed = Assert.IsType<ItemAddedV3>(result);
        Assert.Equal("Widget", typed.Name);
        Assert.Equal(1, typed.Quantity);
        Assert.Equal("default", typed.Category);
    }

    [Fact]
    public void Deserialize_unknown_type_throws()
    {
        var registry = new EventTypeRegistry();

        var payload = JsonSerializer.SerializeToDocument(new { });

        Assert.Throws<InvalidOperationException>(() =>
            registry.DeserializeEvent("totally.unknown.v1", payload));
    }

    private sealed record ItemAddedV3(string Name, int Quantity, string Category);
}
