using Vellum.Kernel.Results;
using Vellum.Modules.Schemas;

namespace Vellum.Tests.Modules.Schemas;

public class SchemaDeciderTests
{
    private static SchemaState StateWith(params SchemaEvent[] events) =>
        events.Aggregate(SchemaState.Initial, (s, e) => s.Evolve(e));

    [Fact]
    public void CreateSchema_valid()
    {
        var result = SchemaDecider.Create(SchemaState.Initial,
            new CreateSchemaCommand(Guid.NewGuid(), "OrderEvent", "Order event schema",
                """{"type": "object", "properties": {"orderId": {"type": "string"}}}""",
                Guid.NewGuid()));
        var success = Assert.IsType<CommandResult<IReadOnlyList<SchemaEvent>>.Success>(result);
        var created = Assert.IsType<SchemaEvent.SchemaCreated>(Assert.Single(success.Value));
        Assert.Equal("OrderEvent", created.Name);
        Assert.Equal(1, created.Version);
    }

    [Fact]
    public void CreateSchema_invalid_json_returns_invalid()
    {
        var result = SchemaDecider.Create(SchemaState.Initial,
            new CreateSchemaCommand(Guid.NewGuid(), "Bad", null, "not valid json", Guid.NewGuid()));
        Assert.IsType<CommandResult<IReadOnlyList<SchemaEvent>>.Invalid>(result);
    }

    [Fact]
    public void CreateSchema_empty_name_returns_invalid()
    {
        var result = SchemaDecider.Create(SchemaState.Initial,
            new CreateSchemaCommand(Guid.NewGuid(), "", null, """{"type":"object"}""", Guid.NewGuid()));
        Assert.IsType<CommandResult<IReadOnlyList<SchemaEvent>>.Invalid>(result);
    }

    [Fact]
    public void UpdateSchema_content_increments_version()
    {
        var id = Guid.NewGuid();
        var state = StateWith(new SchemaEvent.SchemaCreated(
            id, "OrderEvent", null, """{"type":"object"}""", 1, Guid.NewGuid()));
        var result = SchemaDecider.Update(state,
            new UpdateSchemaCommand(id, Content: """{"type":"array"}""", SetContent: true));
        var success = Assert.IsType<CommandResult<IReadOnlyList<SchemaEvent>>.Success>(result);
        var updated = Assert.IsType<SchemaEvent.SchemaUpdated>(Assert.Single(success.Value));
        Assert.Equal(2, updated.Version);
    }

    [Fact]
    public void DeleteSchema_valid()
    {
        var id = Guid.NewGuid();
        var state = StateWith(new SchemaEvent.SchemaCreated(
            id, "OrderEvent", null, """{"type":"object"}""", 1, Guid.NewGuid()));
        var result = SchemaDecider.Delete(state, id);
        Assert.IsType<CommandResult<IReadOnlyList<SchemaEvent>>.Success>(result);
    }
}
