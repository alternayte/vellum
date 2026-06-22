using Vellum.Kernel.Results;
using Vellum.Modules.Modelling.Model;

namespace Vellum.Tests.Modules.Modelling;

public class ModelDeciderTests
{
    private static ModelState StateWith(params ModelEvent[] events) =>
        events.Aggregate(ModelState.Initial, (s, e) => s.Evolve(e));

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
        var success = Assert.IsType<CommandResult<IReadOnlyList<ModelEvent>>.Success>(result);
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
        Assert.IsType<CommandResult<IReadOnlyList<ModelEvent>>.Success>(result);
    }

    [Fact]
    public void AddElement_app_without_parent_fails()
    {
        var result = ModelDecider.AddElement(ModelState.Initial,
            new AddElementCommand(Guid.NewGuid(), ElementKind.App, "API", null, null, null, ElementStatus.Current, null, []));
        Assert.IsType<CommandResult<IReadOnlyList<ModelEvent>>.Invalid>(result);
    }

    [Fact]
    public void AddElement_system_with_parent_fails()
    {
        var state = StateWith(
            new ModelEvent.ElementAdded(SystemId, ElementKind.System, "Orders", null, null, null, ElementStatus.Current, null, []));
        var result = ModelDecider.AddElement(state,
            new AddElementCommand(Guid.NewGuid(), ElementKind.System, "Payments", null, null, null, ElementStatus.Current, SystemId, []));
        Assert.IsType<CommandResult<IReadOnlyList<ModelEvent>>.Invalid>(result);
    }

    [Fact]
    public void AddElement_component_with_app_parent_succeeds()
    {
        var state = StateWithSystemAndApp();
        var result = ModelDecider.AddElement(state,
            new AddElementCommand(Guid.NewGuid(), ElementKind.Component, "Handler", null, null, null, ElementStatus.Current, AppId, []));
        Assert.IsType<CommandResult<IReadOnlyList<ModelEvent>>.Success>(result);
    }

    [Fact]
    public void AddElement_component_with_system_parent_fails()
    {
        var state = StateWithSystemAndApp();
        var result = ModelDecider.AddElement(state,
            new AddElementCommand(Guid.NewGuid(), ElementKind.Component, "Handler", null, null, null, ElementStatus.Current, SystemId, []));
        Assert.IsType<CommandResult<IReadOnlyList<ModelEvent>>.Invalid>(result);
    }

    [Fact]
    public void AddElement_duplicate_id_returns_conflict()
    {
        var id = Guid.NewGuid();
        var state = StateWith(
            new ModelEvent.ElementAdded(id, ElementKind.System, "Orders", null, null, null, ElementStatus.Current, null, []));
        var result = ModelDecider.AddElement(state,
            new AddElementCommand(id, ElementKind.System, "Payments", null, null, null, ElementStatus.Current, null, []));
        Assert.IsType<CommandResult<IReadOnlyList<ModelEvent>>.Conflict>(result);
    }

    [Fact]
    public void AddElement_empty_name_returns_invalid()
    {
        var result = ModelDecider.AddElement(ModelState.Initial,
            new AddElementCommand(Guid.NewGuid(), ElementKind.System, "", null, null, null, ElementStatus.Current, null, []));
        Assert.IsType<CommandResult<IReadOnlyList<ModelEvent>>.Invalid>(result);
    }

    [Fact]
    public void AddElement_missing_parent_returns_invalid()
    {
        var result = ModelDecider.AddElement(ModelState.Initial,
            new AddElementCommand(Guid.NewGuid(), ElementKind.App, "API", null, null, null, ElementStatus.Current, Guid.NewGuid(), []));
        Assert.IsType<CommandResult<IReadOnlyList<ModelEvent>>.Invalid>(result);
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
        var success = Assert.IsType<CommandResult<IReadOnlyList<ModelEvent>>.Success>(result);
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
        var success = Assert.IsType<CommandResult<IReadOnlyList<ModelEvent>>.Success>(result);
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
        var success = Assert.IsType<CommandResult<IReadOnlyList<ModelEvent>>.Success>(result);
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
        Assert.IsType<CommandResult<IReadOnlyList<ModelEvent>>.Invalid>(result);
    }

    [Fact]
    public void UpdateElement_nonexistent_returns_not_found()
    {
        var result = ModelDecider.UpdateElement(ModelState.Initial,
            new UpdateElementCommand(Guid.NewGuid(), Name: "X", SetName: true));
        Assert.IsType<CommandResult<IReadOnlyList<ModelEvent>>.NotFound>(result);
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
        var success = Assert.IsType<CommandResult<IReadOnlyList<ModelEvent>>.Success>(result);
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
        var success = Assert.IsType<CommandResult<IReadOnlyList<ModelEvent>>.Success>(result);
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
        Assert.IsType<CommandResult<IReadOnlyList<ModelEvent>>.NotFound>(result);
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
        var success = Assert.IsType<CommandResult<IReadOnlyList<ModelEvent>>.Success>(result);

        var relationshipRemovals = success.Value.OfType<ModelEvent.RelationshipRemoved>().ToList();
        Assert.Single(relationshipRemovals);
        Assert.Equal(relId, relationshipRemovals[0].RelationshipId);
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
        Assert.IsType<CommandResult<IReadOnlyList<ModelEvent>>.Success>(result);
    }

    [Fact]
    public void AddRelationship_missing_from_returns_invalid()
    {
        var b = Guid.NewGuid();
        var state = StateWith(
            new ModelEvent.ElementAdded(b, ElementKind.System, "B", null, null, null, ElementStatus.Current, null, []));
        var result = ModelDecider.AddRelationship(state,
            new AddRelationshipCommand(Guid.NewGuid(), Guid.NewGuid(), b, "uses", null));
        Assert.IsType<CommandResult<IReadOnlyList<ModelEvent>>.Invalid>(result);
    }

    // --- RemoveRelationship ---

    [Fact]
    public void RemoveRelationship_nonexistent_returns_not_found()
    {
        var result = ModelDecider.RemoveRelationship(ModelState.Initial, Guid.NewGuid());
        Assert.IsType<CommandResult<IReadOnlyList<ModelEvent>>.NotFound>(result);
    }
}
