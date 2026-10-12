using System.Text.Json;
using Vellum.Kernel.EventTypes;
using Vellum.Modules.Modelling;
using Vellum.Modules.Modelling.Model;

namespace Vellum.Tests.Modules.Modelling;

/// <summary>
/// SDD guarantee 2 and the migration promise: streams written before typed IDs, fact
/// statuses and relationship kinds existed must keep loading.
/// </summary>
public class ModelCompatibilityTests
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static EventTypeRegistry Registry()
    {
        var registry = new EventTypeRegistry();
        ModellingModule.RegisterEvents(registry);
        return registry;
    }

    [Fact]
    public void Old_element_added_payload_deserializes_with_typed_id()
    {
        var id = Guid.NewGuid();
        var payload = JsonSerializer.SerializeToDocument(new
        {
            id,
            kind = 1,
            name = "Orders",
            description = "Handles orders",
            technology = "dotnet",
            ownerId = (Guid?)null,
            status = 0,
            parentId = (Guid?)null,
            tags = new[] { "core" },
        }, JsonOpts);

        var evt = (ModelEvent)Registry().DeserializeEvent("modelling.element.added.v1", payload);

        var added = Assert.IsType<ModelEvent.ElementAdded>(evt);
        Assert.Equal(id, added.Id.Value);
        Assert.Equal(ElementKind.System, added.Kind);
        Assert.Equal(["core"], added.Tags);
    }

    [Fact]
    public void Old_relationship_added_payload_defaults_to_structural()
    {
        var id = Guid.NewGuid();
        var fromId = Guid.NewGuid();
        var toId = Guid.NewGuid();
        var payload = JsonSerializer.SerializeToDocument(new
        {
            id,
            fromId,
            toId,
            label = "uses",
            technology = "HTTP",
            messageId = (Guid?)null,
        }, JsonOpts);

        var evt = (ModelEvent)Registry().DeserializeEvent("modelling.relationship.added.v1", payload);

        var added = Assert.IsType<ModelEvent.RelationshipAdded>(evt);
        Assert.Equal(RelationshipKind.Structural, added.Kind);
        Assert.Equal(fromId, added.FromId.Value);
    }

    [Fact]
    public void Old_state_snapshot_deserializes_with_declared_facts()
    {
        var id = Guid.NewGuid();
        var snapshot = new
        {
            elements = new Dictionary<string, object>
            {
                [id.ToString()] = new
                {
                    id,
                    kind = 1,
                    name = "Orders",
                    description = (string?)null,
                    technology = (string?)null,
                    ownerId = (Guid?)null,
                    status = 0,
                    parentId = (Guid?)null,
                    tags = Array.Empty<string>(),
                    icon = (string?)null,
                },
            },
            relationships = new Dictionary<string, object>(),
            messages = new Dictionary<string, object>(),
        };

        var state = JsonSerializer.Deserialize<ModelState>(JsonSerializer.Serialize(snapshot, JsonOpts), JsonOpts)!;

        var element = state.Elements[id];
        Assert.Equal("Orders", element.Name);
        Assert.Equal(FactStatus.Declared, element.FactStatus);
        Assert.Null(element.Provenance);
    }

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
    public void State_snapshot_round_trips_ids_kinds_and_provenance()
    {
        var elementId = Guid.NewGuid();
        var relationshipId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var schemaId = Guid.NewGuid();

        var state = ModelState.Initial
            .Evolve(new ModelEvent.ElementAdded(elementId, ElementKind.System, "Orders",
                null, null, null, ElementStatus.Current, null, []))
            .Evolve(new ModelEvent.ElementProvenanceRecorded(elementId, FactStatus.Inferred,
                new Provenance("github.com/acme/orders@abc:src/Orders.cs:1-10", "abc", 0.75, Guid.NewGuid(), DateTimeOffset.UtcNow)))
            .Evolve(new ModelEvent.RelationshipAdded(relationshipId, elementId, elementId,
                "publishes", "AMQP", null, RelationshipKind.Behavioural))
            .Evolve(new ModelEvent.MessageAdded(messageId, "OrderPlaced", null,
                elementId, [elementId], schemaId, []));

        var json = JsonSerializer.Serialize(state, JsonOpts);
        var roundTripped = JsonSerializer.Deserialize<ModelState>(json, JsonOpts)!;

        Assert.Equivalent(state, roundTripped);
        Assert.Equal(RelationshipKind.Behavioural, roundTripped.Relationships[relationshipId].Kind);
        Assert.Equal(FactStatus.Inferred, roundTripped.Elements[elementId].FactStatus);
        Assert.Equal(0.75, roundTripped.Elements[elementId].Provenance!.Confidence);
        Assert.Equal(schemaId, roundTripped.Messages[messageId].SchemaId!.Value.Value);
    }
}
