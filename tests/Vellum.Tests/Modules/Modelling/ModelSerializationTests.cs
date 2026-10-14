using System.Text.Json;
using Deedbox.Testing;
using Vellum.Modules.Modelling;

namespace Vellum.Tests.Modules.Modelling;

/// <summary>
/// The evergreen model format: typed IDs serialize as plain GUID strings, state snapshots
/// round-trip, and label sets are closed.
/// </summary>
public class ModelSerializationTests
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    [Fact]
    public void Closed_label_sets_reject_numeric_strings()
    {
        Assert.False(ModelEnum.TryParseDefined("42", out FactStatus _));
        Assert.False(ModelEnum.TryParseDefined("7", out RelationshipKind _));
        Assert.False(ModelEnum.TryParseDefined("99", out ElementKind _));
        Assert.False(ModelEnum.TryParseDefined("0", out FactStatus _));
        Assert.False(ModelEnum.TryParseDefined("1", out ElementKind _));
        Assert.False(ModelEnum.TryParseDefined("deprecated,removed", out ElementStatus _));

        Assert.True(ModelEnum.TryParseDefined("observed-in-source", out FactStatus observed));
        Assert.Equal(FactStatus.ObservedInSource, observed);
        Assert.True(ModelEnum.TryParseDefined("Behavioural", out RelationshipKind behavioural));
        Assert.Equal(RelationshipKind.Behavioural, behavioural);
    }

    [Fact]
    public void Typed_ids_serialize_as_plain_guid_strings()
    {
        var id = Guid.NewGuid();
        var json = JsonSerializer.Serialize(new ElementId(id), JsonOpts);
        Assert.Equal($"\"{id}\"", json);
        Assert.Equal(id, JsonSerializer.Deserialize<ElementId>(json, JsonOpts).Value);
    }

    [Fact]
    public void State_snapshot_round_trips_ids_kinds_and_provenance()
    {
        var elementId = Guid.NewGuid();
        var relationshipId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var schemaId = Guid.NewGuid();

        var state = Decider.Given<ModelState>(
            new ModelEvent.ElementAdded(elementId, ElementKind.System, "Orders",
                null, null, null, ElementStatus.Current, null, []),
            new ModelEvent.ElementProvenanceRecorded(elementId, FactStatus.Inferred,
                new Provenance("github.com/acme/orders@abc:src/Orders.cs:1-10", "abc", 0.75, Guid.NewGuid(), DateTimeOffset.UtcNow)),
            new ModelEvent.RelationshipAdded(relationshipId, elementId, elementId,
                "publishes", "AMQP", null, RelationshipKind.Behavioural),
            new ModelEvent.MessageAdded(messageId, "OrderPlaced", null,
                elementId, [elementId], schemaId, [])).State;

        var json = JsonSerializer.Serialize(state, JsonOpts);
        var roundTripped = JsonSerializer.Deserialize<ModelState>(json, JsonOpts)!;

        Assert.Equivalent(state, roundTripped);
        Assert.Equal(RelationshipKind.Behavioural, roundTripped.Relationships[relationshipId].Kind);
        Assert.Equal(FactStatus.Inferred, roundTripped.Elements[elementId].FactStatus);
        Assert.Equal(0.75, roundTripped.Elements[elementId].Provenance!.Confidence);
        Assert.Equal(schemaId, roundTripped.Messages[messageId].SchemaId!.Value.Value);
    }

    [Fact]
    public void Dictionary_keys_round_trip_through_typed_ids()
    {
        var id = Guid.NewGuid();
        var state = Decider.Given<ModelState>(
            new ModelEvent.ElementAdded(id, ElementKind.System, "Orders",
                null, null, null, ElementStatus.Current, null, [])).State;

        var json = JsonSerializer.Serialize(state, JsonOpts);
        Assert.Contains(id.ToString(), json);

        var roundTripped = JsonSerializer.Deserialize<ModelState>(json, JsonOpts)!;
        Assert.True(roundTripped.Elements.ContainsKey(id));
    }
}
