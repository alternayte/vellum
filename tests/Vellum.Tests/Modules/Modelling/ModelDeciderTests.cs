using Vellum.Shared;
using Deedbox.Testing;
using Vellum.Modules.Modelling;

namespace Vellum.Tests.Modules.Modelling;

public class ModelDeciderTests
{
    private static ModelState StateWith(params object[] events) =>
        Decider.Given<ModelState>(events).State;

    private static readonly Guid SystemId = Guid.NewGuid();
    private static readonly Guid AppId = Guid.NewGuid();

    private static ModelState StateWithSystemAndApp()
    {
        return StateWith(
            new ModelEvent.ElementAdded(SystemId, ElementKind.System, "Orders", null, null, null, ElementStatus.Current, null, []),
            new ModelEvent.ElementAdded(AppId, ElementKind.App, "API", null, "dotnet", null, ElementStatus.Current, SystemId, []));
    }

    // --- AddElement ---

    [Fact]
    public void AddElement_valid_top_level_system()
    {
        var result = ModelDecider.AddElement(ModelState.Initial,
            new AddElementCommand(Guid.NewGuid(), ElementKind.System, "Orders", null, null, null, ElementStatus.Current, null, []));
        var success = Assert.IsType<CommandResult<IReadOnlyList<object>>.Success>(result);
        var added = Assert.IsType<ModelEvent.ElementAdded>(Assert.Single(success.Value));
        Assert.Equal("Orders", added.Name);
    }

    [Fact]
    public void AddElement_app_with_system_parent_succeeds()
    {
        var state = StateWith(
            new ModelEvent.ElementAdded(SystemId, ElementKind.System, "Orders", null, null, null, ElementStatus.Current, null, []));
        var result = ModelDecider.AddElement(state,
            new AddElementCommand(Guid.NewGuid(), ElementKind.App, "API", null, null, null, ElementStatus.Current, SystemId, []));
        Assert.IsType<CommandResult<IReadOnlyList<object>>.Success>(result);
    }

    [Fact]
    public void AddElement_app_without_parent_fails()
    {
        var result = ModelDecider.AddElement(ModelState.Initial,
            new AddElementCommand(Guid.NewGuid(), ElementKind.App, "API", null, null, null, ElementStatus.Current, null, []));
        Assert.IsType<CommandResult<IReadOnlyList<object>>.Invalid>(result);
    }

    [Fact]
    public void AddElement_system_with_parent_fails()
    {
        var state = StateWith(
            new ModelEvent.ElementAdded(SystemId, ElementKind.System, "Orders", null, null, null, ElementStatus.Current, null, []));
        var result = ModelDecider.AddElement(state,
            new AddElementCommand(Guid.NewGuid(), ElementKind.System, "Payments", null, null, null, ElementStatus.Current, SystemId, []));
        Assert.IsType<CommandResult<IReadOnlyList<object>>.Invalid>(result);
    }

    [Fact]
    public void AddElement_component_with_app_parent_succeeds()
    {
        var state = StateWithSystemAndApp();
        var result = ModelDecider.AddElement(state,
            new AddElementCommand(Guid.NewGuid(), ElementKind.Component, "Handler", null, null, null, ElementStatus.Current, AppId, []));
        Assert.IsType<CommandResult<IReadOnlyList<object>>.Success>(result);
    }

    [Fact]
    public void AddElement_component_with_system_parent_fails()
    {
        var state = StateWithSystemAndApp();
        var result = ModelDecider.AddElement(state,
            new AddElementCommand(Guid.NewGuid(), ElementKind.Component, "Handler", null, null, null, ElementStatus.Current, SystemId, []));
        Assert.IsType<CommandResult<IReadOnlyList<object>>.Invalid>(result);
    }

    [Fact]
    public void AddElement_duplicate_id_returns_conflict()
    {
        var id = Guid.NewGuid();
        var state = StateWith(
            new ModelEvent.ElementAdded(id, ElementKind.System, "Orders", null, null, null, ElementStatus.Current, null, []));
        var result = ModelDecider.AddElement(state,
            new AddElementCommand(id, ElementKind.System, "Payments", null, null, null, ElementStatus.Current, null, []));
        Assert.IsType<CommandResult<IReadOnlyList<object>>.Conflict>(result);
    }

    [Fact]
    public void AddElement_empty_name_returns_invalid()
    {
        var result = ModelDecider.AddElement(ModelState.Initial,
            new AddElementCommand(Guid.NewGuid(), ElementKind.System, "", null, null, null, ElementStatus.Current, null, []));
        Assert.IsType<CommandResult<IReadOnlyList<object>>.Invalid>(result);
    }

    [Fact]
    public void AddElement_missing_parent_returns_invalid()
    {
        var result = ModelDecider.AddElement(ModelState.Initial,
            new AddElementCommand(Guid.NewGuid(), ElementKind.App, "API", null, null, null, ElementStatus.Current, Guid.NewGuid(), []));
        Assert.IsType<CommandResult<IReadOnlyList<object>>.Invalid>(result);
    }

    // --- UpdateElement ---

    [Fact]
    public void UpdateElement_rename_emits_only_renamed()
    {
        var id = Guid.NewGuid();
        var state = StateWith(
            new ModelEvent.ElementAdded(id, ElementKind.System, "Orders", null, null, null, ElementStatus.Current, null, []));
        var result = ModelDecider.UpdateElement(state,
            new UpdateElementCommand(id, Name: "Payments", SetName: true));
        var success = Assert.IsType<CommandResult<IReadOnlyList<object>>.Success>(result);
        var renamed = Assert.IsType<ModelEvent.ElementRenamed>(Assert.Single(success.Value));
        Assert.Equal("Payments", renamed.Name);
    }

    [Fact]
    public void UpdateElement_no_changes_emits_no_events()
    {
        var id = Guid.NewGuid();
        var state = StateWith(
            new ModelEvent.ElementAdded(id, ElementKind.System, "Orders", null, null, null, ElementStatus.Current, null, []));
        var result = ModelDecider.UpdateElement(state,
            new UpdateElementCommand(id, Name: "Orders", SetName: true));
        var success = Assert.IsType<CommandResult<IReadOnlyList<object>>.Success>(result);
        Assert.Empty(success.Value);
    }

    [Fact]
    public void UpdateElement_multiple_fields_emits_multiple_events()
    {
        var id = Guid.NewGuid();
        var state = StateWith(
            new ModelEvent.ElementAdded(id, ElementKind.System, "Orders", null, null, null, ElementStatus.Current, null, []));
        var result = ModelDecider.UpdateElement(state,
            new UpdateElementCommand(id, Name: "Payments", SetName: true, Status: ElementStatus.Planned));
        var success = Assert.IsType<CommandResult<IReadOnlyList<object>>.Success>(result);
        Assert.Equal(2, success.Value.Count);
        Assert.IsType<ModelEvent.ElementRenamed>(success.Value[0]);
        Assert.IsType<ModelEvent.ElementStatusChanged>(success.Value[1]);
    }

    [Fact]
    public void UpdateElement_reparent_app_to_null_fails()
    {
        var state = StateWithSystemAndApp();
        var result = ModelDecider.UpdateElement(state,
            new UpdateElementCommand(AppId, ParentId: null, SetParentId: true));
        Assert.IsType<CommandResult<IReadOnlyList<object>>.Invalid>(result);
    }

    [Fact]
    public void UpdateElement_nonexistent_returns_not_found()
    {
        var result = ModelDecider.UpdateElement(ModelState.Initial,
            new UpdateElementCommand(Guid.NewGuid(), Name: "X", SetName: true));
        Assert.IsType<CommandResult<IReadOnlyList<object>>.NotFound>(result);
    }

    // --- RemoveElement ---

    [Fact]
    public void RemoveElement_cascades_relationships()
    {
        var sysId = Guid.NewGuid();
        var relId = Guid.NewGuid();
        var otherId = Guid.NewGuid();
        var state = StateWith(
            new ModelEvent.ElementAdded(sysId, ElementKind.System, "A", null, null, null, ElementStatus.Current, null, []),
            new ModelEvent.ElementAdded(otherId, ElementKind.System, "B", null, null, null, ElementStatus.Current, null, []),
            new ModelEvent.RelationshipAdded(relId, sysId, otherId, "uses", null, null));

        var result = ModelDecider.RemoveElement(state, sysId);
        var success = Assert.IsType<CommandResult<IReadOnlyList<object>>.Success>(result);
        Assert.Equal(2, success.Value.Count);
        Assert.IsType<ModelEvent.RelationshipRemoved>(success.Value[0]);
        Assert.IsType<ModelEvent.ElementRemoved>(success.Value[1]);
    }

    [Fact]
    public void RemoveElement_cascades_children_recursively()
    {
        var sysId = Guid.NewGuid();
        var appId = Guid.NewGuid();
        var compId = Guid.NewGuid();
        var state = StateWith(
            new ModelEvent.ElementAdded(sysId, ElementKind.System, "Orders", null, null, null, ElementStatus.Current, null, []),
            new ModelEvent.ElementAdded(appId, ElementKind.App, "API", null, null, null, ElementStatus.Current, sysId, []),
            new ModelEvent.ElementAdded(compId, ElementKind.Component, "Handler", null, null, null, ElementStatus.Current, appId, []));

        var result = ModelDecider.RemoveElement(state, sysId);
        var success = Assert.IsType<CommandResult<IReadOnlyList<object>>.Success>(result);
        // Component removed, then App removed, then System removed
        Assert.Equal(3, success.Value.Count);
        var removals = success.Value.Cast<ModelEvent.ElementRemoved>().Select(r => r.ElementId).ToList();
        Assert.Contains(compId, removals);
        Assert.Contains(appId, removals);
        Assert.Contains(sysId, removals);
        // Children removed before parents
        Assert.True(removals.IndexOf(compId) < removals.IndexOf(appId));
        Assert.True(removals.IndexOf(appId) < removals.IndexOf(sysId));
    }

    [Fact]
    public void RemoveElement_nonexistent_returns_not_found()
    {
        var result = ModelDecider.RemoveElement(ModelState.Initial, Guid.NewGuid());
        Assert.IsType<CommandResult<IReadOnlyList<object>>.NotFound>(result);
    }

    [Fact]
    public void RemoveElement_cascade_emits_relationship_removed_exactly_once_when_both_endpoints_are_children()
    {
        // System with two App children connected by a relationship.
        // When the System is removed, the relationship's endpoints (AppA and AppB) are
        // both visited during the cascade. Without deduplication this would emit
        // RelationshipRemoved twice — once per child.
        var sysId = Guid.NewGuid();
        var appAId = Guid.NewGuid();
        var appBId = Guid.NewGuid();
        var relId = Guid.NewGuid();

        var state = StateWith(
            new ModelEvent.ElementAdded(sysId, ElementKind.System, "System", null, null, null, ElementStatus.Current, null, []),
            new ModelEvent.ElementAdded(appAId, ElementKind.App, "AppA", null, null, null, ElementStatus.Current, sysId, []),
            new ModelEvent.ElementAdded(appBId, ElementKind.App, "AppB", null, null, null, ElementStatus.Current, sysId, []),
            new ModelEvent.RelationshipAdded(relId, appAId, appBId, "calls", null, null));

        var result = ModelDecider.RemoveElement(state, sysId);
        var success = Assert.IsType<CommandResult<IReadOnlyList<object>>.Success>(result);

        var relationshipRemovals = success.Value.OfType<ModelEvent.RelationshipRemoved>().ToList();
        Assert.Single(relationshipRemovals);
        Assert.Equal(relId, relationshipRemovals[0].RelationshipId.Value);
    }

    // --- AddMessage ---

    [Fact]
    public void AddMessage_valid()
    {
        var producerId = Guid.NewGuid();
        var consumerId = Guid.NewGuid();
        var state = StateWith(
            new ModelEvent.ElementAdded(producerId, ElementKind.System, "Orders", null, null, null, ElementStatus.Current, null, []),
            new ModelEvent.ElementAdded(consumerId, ElementKind.System, "Payments", null, null, null, ElementStatus.Current, null, []));
        var result = ModelDecider.AddMessage(state,
            new AddMessageCommand(Guid.NewGuid(), "OrderPlaced", "An order was placed", producerId, [consumerId], null, []));
        var success = Assert.IsType<CommandResult<IReadOnlyList<object>>.Success>(result);
        var added = Assert.IsType<ModelEvent.MessageAdded>(Assert.Single(success.Value));
        Assert.Equal("OrderPlaced", added.Name);
        Assert.Equal(producerId, added.ProducerId.Value);
        Assert.Single(added.ConsumerIds);
    }

    [Fact]
    public void AddMessage_empty_name_returns_invalid()
    {
        var producerId = Guid.NewGuid();
        var state = StateWith(
            new ModelEvent.ElementAdded(producerId, ElementKind.System, "Orders", null, null, null, ElementStatus.Current, null, []));
        var result = ModelDecider.AddMessage(state,
            new AddMessageCommand(Guid.NewGuid(), "", null, producerId, [], null, []));
        Assert.IsType<CommandResult<IReadOnlyList<object>>.Invalid>(result);
    }

    [Fact]
    public void AddMessage_missing_producer_returns_invalid()
    {
        var result = ModelDecider.AddMessage(ModelState.Initial,
            new AddMessageCommand(Guid.NewGuid(), "OrderPlaced", null, Guid.NewGuid(), [], null, []));
        Assert.IsType<CommandResult<IReadOnlyList<object>>.Invalid>(result);
    }

    [Fact]
    public void AddMessage_missing_consumer_returns_invalid()
    {
        var producerId = Guid.NewGuid();
        var state = StateWith(
            new ModelEvent.ElementAdded(producerId, ElementKind.System, "Orders", null, null, null, ElementStatus.Current, null, []));
        var result = ModelDecider.AddMessage(state,
            new AddMessageCommand(Guid.NewGuid(), "OrderPlaced", null, producerId, [Guid.NewGuid()], null, []));
        Assert.IsType<CommandResult<IReadOnlyList<object>>.Invalid>(result);
    }

    [Fact]
    public void AddMessage_duplicate_id_returns_conflict()
    {
        var id = Guid.NewGuid();
        var producerId = Guid.NewGuid();
        var state = StateWith(
            new ModelEvent.ElementAdded(producerId, ElementKind.System, "Orders", null, null, null, ElementStatus.Current, null, []),
            new ModelEvent.MessageAdded(id, "OrderPlaced", null, producerId, [], null, []));
        var result = ModelDecider.AddMessage(state,
            new AddMessageCommand(id, "Other", null, producerId, [], null, []));
        Assert.IsType<CommandResult<IReadOnlyList<object>>.Conflict>(result);
    }

    // --- UpdateMessage ---

    [Fact]
    public void UpdateMessage_rename_emits_only_renamed()
    {
        var id = Guid.NewGuid();
        var producerId = Guid.NewGuid();
        var state = StateWith(
            new ModelEvent.ElementAdded(producerId, ElementKind.System, "Orders", null, null, null, ElementStatus.Current, null, []),
            new ModelEvent.MessageAdded(id, "OrderPlaced", null, producerId, [], null, []));
        var result = ModelDecider.UpdateMessage(state,
            new UpdateMessageCommand(id, Name: "OrderCreated", SetName: true));
        var success = Assert.IsType<CommandResult<IReadOnlyList<object>>.Success>(result);
        var updated = Assert.IsType<ModelEvent.MessageUpdated>(Assert.Single(success.Value));
        Assert.Equal("OrderCreated", updated.Name);
    }

    [Fact]
    public void UpdateMessage_nonexistent_returns_not_found()
    {
        var result = ModelDecider.UpdateMessage(ModelState.Initial,
            new UpdateMessageCommand(Guid.NewGuid(), Name: "X", SetName: true));
        Assert.IsType<CommandResult<IReadOnlyList<object>>.NotFound>(result);
    }

    // --- RemoveMessage ---

    [Fact]
    public void RemoveMessage_valid()
    {
        var id = Guid.NewGuid();
        var producerId = Guid.NewGuid();
        var state = StateWith(
            new ModelEvent.ElementAdded(producerId, ElementKind.System, "Orders", null, null, null, ElementStatus.Current, null, []),
            new ModelEvent.MessageAdded(id, "OrderPlaced", null, producerId, [], null, []));
        var result = ModelDecider.RemoveMessage(state, id);
        var success = Assert.IsType<CommandResult<IReadOnlyList<object>>.Success>(result);
        Assert.IsType<ModelEvent.MessageRemoved>(Assert.Single(success.Value));
    }

    [Fact]
    public void RemoveMessage_nonexistent_returns_not_found()
    {
        var result = ModelDecider.RemoveMessage(ModelState.Initial, Guid.NewGuid());
        Assert.IsType<CommandResult<IReadOnlyList<object>>.NotFound>(result);
    }

    [Fact]
    public void RemoveElement_cascades_messages()
    {
        var producerId = Guid.NewGuid();
        var consumerId = Guid.NewGuid();
        var msgId = Guid.NewGuid();
        var state = StateWith(
            new ModelEvent.ElementAdded(producerId, ElementKind.System, "Orders", null, null, null, ElementStatus.Current, null, []),
            new ModelEvent.ElementAdded(consumerId, ElementKind.System, "Payments", null, null, null, ElementStatus.Current, null, []),
            new ModelEvent.MessageAdded(msgId, "OrderPlaced", null, producerId, [consumerId], null, []));
        var result = ModelDecider.RemoveElement(state, producerId);
        var success = Assert.IsType<CommandResult<IReadOnlyList<object>>.Success>(result);
        Assert.Contains(success.Value, e => e is ModelEvent.MessageRemoved mr && mr.MessageId.Value == msgId);
    }

    // --- UpdateRelationship lineShape ---

    [Fact]
    public void UpdateRelationship_sets_lineShape()
    {
        var relId = Guid.NewGuid();
        var state = StateWith(
            new ModelEvent.ElementAdded(SystemId, ElementKind.System, "A", null, null, null, ElementStatus.Current, null, []),
            new ModelEvent.ElementAdded(AppId, ElementKind.App, "B", null, null, null, ElementStatus.Current, SystemId, []),
            new ModelEvent.RelationshipAdded(relId, SystemId, AppId, null, null, null));

        var result = ModelDecider.UpdateRelationship(state,
            new UpdateRelationshipCommand(relId, LineShape: "step", SetLineShape: true));
        var success = Assert.IsType<CommandResult<IReadOnlyList<object>>.Success>(result);
        var changed = Assert.IsType<ModelEvent.RelationshipLineShapeChanged>(Assert.Single(success.Value));
        Assert.Equal("step", changed.LineShape);
    }

    [Fact]
    public void UpdateRelationship_lineShape_unchanged_emits_nothing()
    {
        var relId = Guid.NewGuid();
        var state = StateWith(
            new ModelEvent.ElementAdded(SystemId, ElementKind.System, "A", null, null, null, ElementStatus.Current, null, []),
            new ModelEvent.ElementAdded(AppId, ElementKind.App, "B", null, null, null, ElementStatus.Current, SystemId, []),
            new ModelEvent.RelationshipAdded(relId, SystemId, AppId, null, null, null));

        var result = ModelDecider.UpdateRelationship(state,
            new UpdateRelationshipCommand(relId, LineShape: null, SetLineShape: false));
        var success = Assert.IsType<CommandResult<IReadOnlyList<object>>.Success>(result);
        Assert.Empty(success.Value);
    }

    // --- UpdateElement icon ---

    [Fact]
    public void UpdateElement_sets_icon()
    {
        var state = StateWithSystemAndApp();
        var result = ModelDecider.UpdateElement(state,
            new UpdateElementCommand(SystemId, Icon: "server", SetIcon: true));
        var success = Assert.IsType<CommandResult<IReadOnlyList<object>>.Success>(result);
        var changed = Assert.IsType<ModelEvent.ElementIconChanged>(Assert.Single(success.Value));
        Assert.Equal("server", changed.Icon);
    }

    [Fact]
    public void UpdateElement_icon_unchanged_emits_nothing()
    {
        var state = StateWithSystemAndApp();
        var result = ModelDecider.UpdateElement(state,
            new UpdateElementCommand(SystemId, Icon: null, SetIcon: false));
        var success = Assert.IsType<CommandResult<IReadOnlyList<object>>.Success>(result);
        Assert.Empty(success.Value);
    }

    // --- AddRelationship ---

    [Fact]
    public void AddRelationship_valid()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var state = StateWith(
            new ModelEvent.ElementAdded(a, ElementKind.System, "A", null, null, null, ElementStatus.Current, null, []),
            new ModelEvent.ElementAdded(b, ElementKind.System, "B", null, null, null, ElementStatus.Current, null, []));
        var result = ModelDecider.AddRelationship(state,
            new AddRelationshipCommand(Guid.NewGuid(), a, b, "uses", null));
        Assert.IsType<CommandResult<IReadOnlyList<object>>.Success>(result);
    }

    [Fact]
    public void AddRelationship_missing_from_returns_invalid()
    {
        var b = Guid.NewGuid();
        var state = StateWith(
            new ModelEvent.ElementAdded(b, ElementKind.System, "B", null, null, null, ElementStatus.Current, null, []));
        var result = ModelDecider.AddRelationship(state,
            new AddRelationshipCommand(Guid.NewGuid(), Guid.NewGuid(), b, "uses", null));
        Assert.IsType<CommandResult<IReadOnlyList<object>>.Invalid>(result);
    }

    // --- RemoveRelationship ---

    [Fact]
    public void RemoveRelationship_nonexistent_returns_not_found()
    {
        var result = ModelDecider.RemoveRelationship(ModelState.Initial, Guid.NewGuid());
        Assert.IsType<CommandResult<IReadOnlyList<object>>.NotFound>(result);
    }

    // --- Relationship kind ---

    [Fact]
    public void AddRelationship_defaults_to_structural()
    {
        var state = StateWithSystemAndApp();
        var result = ModelDecider.AddRelationship(state,
            new AddRelationshipCommand(Guid.NewGuid(), SystemId, AppId, "uses", "HTTP"));
        var success = Assert.IsType<CommandResult<IReadOnlyList<object>>.Success>(result);
        var added = Assert.IsType<ModelEvent.RelationshipAdded>(Assert.Single(success.Value));
        Assert.Equal(RelationshipKind.Structural, added.Kind);
    }

    [Fact]
    public void AddRelationship_accepts_behavioural_kind()
    {
        var state = StateWithSystemAndApp();
        var result = ModelDecider.AddRelationship(state,
            new AddRelationshipCommand(Guid.NewGuid(), SystemId, AppId, "calls", "HTTP", RelationshipKind.Behavioural));
        var success = Assert.IsType<CommandResult<IReadOnlyList<object>>.Success>(result);
        var added = Assert.IsType<ModelEvent.RelationshipAdded>(Assert.Single(success.Value));
        Assert.Equal(RelationshipKind.Behavioural, added.Kind);
    }

    [Fact]
    public void UpdateRelationship_can_set_behavioural_kind()
    {
        var relId = Guid.NewGuid();
        var state = StateWith(
            new ModelEvent.ElementAdded(SystemId, ElementKind.System, "Orders", null, null, null, ElementStatus.Current, null, []),
            new ModelEvent.ElementAdded(AppId, ElementKind.App, "API", null, null, null, ElementStatus.Current, SystemId, []),
            new ModelEvent.RelationshipAdded(relId, SystemId, AppId, "uses", null, null));

        var result = ModelDecider.UpdateRelationship(state,
            new UpdateRelationshipCommand(relId, Kind: RelationshipKind.Behavioural, SetKind: true));
        var success = Assert.IsType<CommandResult<IReadOnlyList<object>>.Success>(result);
        var changed = Assert.IsType<ModelEvent.RelationshipKindChanged>(Assert.Single(success.Value));
        Assert.Equal(RelationshipKind.Behavioural, changed.Kind);
    }

    [Fact]
    public void UpdateRelationship_same_kind_emits_no_events()
    {
        var relId = Guid.NewGuid();
        var state = StateWith(
            new ModelEvent.ElementAdded(SystemId, ElementKind.System, "Orders", null, null, null, ElementStatus.Current, null, []),
            new ModelEvent.ElementAdded(AppId, ElementKind.App, "API", null, null, null, ElementStatus.Current, SystemId, []),
            new ModelEvent.RelationshipAdded(relId, SystemId, AppId, "uses", null, null));

        var result = ModelDecider.UpdateRelationship(state,
            new UpdateRelationshipCommand(relId, Kind: RelationshipKind.Structural, SetKind: true));
        var success = Assert.IsType<CommandResult<IReadOnlyList<object>>.Success>(result);
        Assert.Empty(success.Value);
    }

    // --- Fact provenance ---

    private static ModelState StateWithElement() => StateWith(
        new ModelEvent.ElementAdded(SystemId, ElementKind.System, "Orders", null, null, null, ElementStatus.Current, null, []));

    [Fact]
    public void RecordElementProvenance_declared_without_evidence_clears_evidence()
    {
        var state = ModelState.Evolve(StateWithElement(), new ModelEvent.ElementProvenanceRecorded(SystemId, FactStatus.ObservedInSource,
                new Provenance("github.com/acme/orders@abc:src/Orders.cs:1-10", "abc", null)));

        var result = ModelDecider.RecordElementProvenance(state, SystemId, FactStatus.Declared, null);
        var success = Assert.IsType<CommandResult<IReadOnlyList<object>>.Success>(result);
        var recorded = Assert.IsType<ModelEvent.ElementProvenanceRecorded>(Assert.Single(success.Value));
        Assert.Equal(FactStatus.Declared, recorded.Status);
        Assert.Null(recorded.Provenance);
    }

    [Fact]
    public void RecordElementProvenance_observed_requires_source()
    {
        var state = StateWithElement();
        var result = ModelDecider.RecordElementProvenance(state, SystemId, FactStatus.ObservedInSource, null);
        Assert.IsType<CommandResult<IReadOnlyList<object>>.Invalid>(result);
    }

    [Fact]
    public void RecordElementProvenance_inferred_requires_confidence()
    {
        var state = StateWithElement();
        var provenance = new Provenance("github.com/acme/orders@abc:src/Orders.cs:1-10", "abc", null);
        var result = ModelDecider.RecordElementProvenance(state, SystemId, FactStatus.Inferred, provenance);
        Assert.IsType<CommandResult<IReadOnlyList<object>>.Invalid>(result);
    }

    [Fact]
    public void RecordElementProvenance_measured_requires_evidence_pointer()
    {
        var state = StateWithElement();
        var result = ModelDecider.RecordElementProvenance(state, SystemId, FactStatus.Measured, null);
        Assert.IsType<CommandResult<IReadOnlyList<object>>.Invalid>(result);
    }

    [Fact]
    public void RecordElementProvenance_rejects_out_of_range_confidence()
    {
        var state = StateWithElement();
        var provenance = new Provenance("github.com/acme/orders@abc:src/Orders.cs:1-10", "abc", 1.5);
        var result = ModelDecider.RecordElementProvenance(state, SystemId, FactStatus.Inferred, provenance);
        Assert.IsType<CommandResult<IReadOnlyList<object>>.Invalid>(result);
    }

    [Fact]
    public void RecordElementProvenance_records_status_source_and_confidence()
    {
        var state = StateWithElement();
        var provenance = new Provenance("github.com/acme/orders@abc:src/Orders.cs:1-10", "abc", 0.6);
        var result = ModelDecider.RecordElementProvenance(state, SystemId, FactStatus.Inferred, provenance);
        var success = Assert.IsType<CommandResult<IReadOnlyList<object>>.Success>(result);
        var recorded = Assert.IsType<ModelEvent.ElementProvenanceRecorded>(Assert.Single(success.Value));
        Assert.Equal(FactStatus.Inferred, recorded.Status);
        Assert.Equal(0.6, recorded.Provenance!.Confidence);
    }

    [Fact]
    public void RecordElementProvenance_unchanged_emits_no_events()
    {
        var provenance = new Provenance("github.com/acme/orders@abc:src/Orders.cs:1-10", "abc", 0.6);
        var state = ModelState.Evolve(StateWithElement(), new ModelEvent.ElementProvenanceRecorded(SystemId, FactStatus.Inferred, provenance));

        var result = ModelDecider.RecordElementProvenance(state, SystemId, FactStatus.Inferred, provenance with { AcceptedAt = DateTimeOffset.UtcNow });
        var success = Assert.IsType<CommandResult<IReadOnlyList<object>>.Success>(result);
        Assert.Empty(success.Value);
    }

    [Fact]
    public void RecordElementProvenance_missing_element_returns_not_found()
    {
        var result = ModelDecider.RecordElementProvenance(ModelState.Initial, Guid.NewGuid(), FactStatus.Declared, null);
        Assert.IsType<CommandResult<IReadOnlyList<object>>.NotFound>(result);
    }

    [Fact]
    public void RecordRelationshipProvenance_inferred_without_source_is_invalid()
    {
        var relId = Guid.NewGuid();
        var state = StateWith(
            new ModelEvent.ElementAdded(SystemId, ElementKind.System, "Orders", null, null, null, ElementStatus.Current, null, []),
            new ModelEvent.RelationshipAdded(relId, SystemId, SystemId, "calls", null, null));

        var result = ModelDecider.RecordRelationshipProvenance(state, relId, FactStatus.Inferred,
            new Provenance(null, null, 0.5));
        Assert.IsType<CommandResult<IReadOnlyList<object>>.Invalid>(result);
    }

    [Fact]
    public void RecordMessageProvenance_records_observed_fact()
    {
        var msgId = Guid.NewGuid();
        var state = StateWith(
            new ModelEvent.ElementAdded(SystemId, ElementKind.System, "Orders", null, null, null, ElementStatus.Current, null, []),
            new ModelEvent.MessageAdded(msgId, "OrderPlaced", null, SystemId, [SystemId], null, []));

        var result = ModelDecider.RecordMessageProvenance(state, msgId, FactStatus.ObservedInSource,
            new Provenance("github.com/acme/orders@abc:src/Events.cs:4-8", "abc", null));
        var success = Assert.IsType<CommandResult<IReadOnlyList<object>>.Success>(result);
        var recorded = Assert.IsType<ModelEvent.MessageProvenanceRecorded>(Assert.Single(success.Value));
        Assert.Equal(FactStatus.ObservedInSource, recorded.Status);
    }
}
