using Deedbox.Testing;
using Vellum.Modules.Modelling.Model;

namespace Vellum.Tests.Modules.Modelling;

public class ModelEvolveTests
{
    [Fact]
    public void ElementAdded_adds_to_state()
    {
        var id = Guid.NewGuid();
        var state = Decider.Given<ModelState>(
            new ModelEvent.ElementAdded(id, ElementKind.System, "Orders", "desc", "dotnet", null, ElementStatus.Current, null, ["api"])).State;

        Assert.Single(state.Elements);
        var el = state.Elements[id];
        Assert.Equal("Orders", el.Name);
        Assert.Equal("desc", el.Description);
        Assert.Equal("dotnet", el.Technology);
        Assert.Equal(ElementStatus.Current, el.Status);
        Assert.Equal(["api"], el.Tags);
    }

    [Fact]
    public void ElementRenamed_updates_name()
    {
        var id = Guid.NewGuid();
        var state = Decider.Given<ModelState>(
            new ModelEvent.ElementAdded(id, ElementKind.System, "Old", null, null, null, ElementStatus.Current, null, []),
            new ModelEvent.ElementRenamed(id, "New")).State;
        Assert.Equal("New", state.Elements[id].Name);
    }

    [Fact]
    public void ElementRemoved_removes_from_state()
    {
        var id = Guid.NewGuid();
        var state = Decider.Given<ModelState>(
            new ModelEvent.ElementAdded(id, ElementKind.System, "Orders", null, null, null, ElementStatus.Current, null, []),
            new ModelEvent.ElementRemoved(id)).State;
        Assert.Empty(state.Elements);
    }

    [Fact]
    public void RelationshipAdded_adds_to_state()
    {
        var relId = Guid.NewGuid();
        var state = Decider.Given<ModelState>(
            new ModelEvent.RelationshipAdded(relId, Guid.NewGuid(), Guid.NewGuid(), "uses", "HTTP", null)).State;
        Assert.Single(state.Relationships);
        Assert.Equal("uses", state.Relationships[relId].Label);
    }

    [Fact]
    public void RelationshipRemoved_removes_from_state()
    {
        var relId = Guid.NewGuid();
        var state = Decider.Given<ModelState>(
            new ModelEvent.RelationshipAdded(relId, Guid.NewGuid(), Guid.NewGuid(), "uses", null, null),
            new ModelEvent.RelationshipRemoved(relId)).State;
        Assert.Empty(state.Relationships);
    }

    [Fact]
    public void RelationshipLineShapeChanged_updates_lineShape()
    {
        var relId = Guid.NewGuid();
        var state = Decider.Given<ModelState>(
            new ModelEvent.RelationshipAdded(relId, Guid.NewGuid(), Guid.NewGuid(), null, null, null),
            new ModelEvent.RelationshipLineShapeChanged(relId, "step")).State;
        Assert.Equal("step", state.Relationships[relId].LineShape);
    }

    [Fact]
    public void All_element_update_events_fold_correctly()
    {
        var id = Guid.NewGuid();
        var ownerId = Guid.NewGuid();
        var state = Decider.Given<ModelState>(
            new ModelEvent.ElementAdded(id, ElementKind.System, "X", null, null, null, ElementStatus.Current, null, []),
            new ModelEvent.ElementDescriptionChanged(id, "desc"),
            new ModelEvent.ElementTechnologyChanged(id, "go"),
            new ModelEvent.ElementOwnerChanged(id, ownerId),
            new ModelEvent.ElementStatusChanged(id, ElementStatus.Planned),
            new ModelEvent.ElementRetagged(id, ["a", "b"])).State;

        var el = state.Elements[id];
        Assert.Equal("desc", el.Description);
        Assert.Equal("go", el.Technology);
        Assert.Equal(ownerId, el.OwnerId);
        Assert.Equal(ElementStatus.Planned, el.Status);
        Assert.Equal(["a", "b"], el.Tags);
    }

    [Fact]
    public void ElementProvenanceRecorded_updates_status_and_provenance()
    {
        var id = Guid.NewGuid();
        var acceptedBy = Guid.NewGuid();
        var state = Decider.Given<ModelState>(
            new ModelEvent.ElementAdded(id, ElementKind.System, "Orders", null, null, null, ElementStatus.Current, null, []),
            new ModelEvent.ElementProvenanceRecorded(id, FactStatus.Inferred,
                new Provenance("github.com/acme/orders@abc:src/Orders.cs:1-10", "abc", 0.5, acceptedBy, DateTimeOffset.UtcNow))).State;

        var el = state.Elements[id];
        Assert.Equal(FactStatus.Inferred, el.FactStatus);
        Assert.Equal("abc", el.Provenance!.Revision);
        Assert.Equal(acceptedBy, el.Provenance.AcceptedBy);
    }

    [Fact]
    public void RelationshipKindChanged_updates_state()
    {
        var relId = Guid.NewGuid();
        var state = Decider.Given<ModelState>(
            new ModelEvent.RelationshipAdded(relId, Guid.NewGuid(), Guid.NewGuid(), "calls", null, null),
            new ModelEvent.RelationshipKindChanged(relId, RelationshipKind.Behavioural)).State;

        Assert.Equal(RelationshipKind.Behavioural, state.Relationships[relId].Kind);
    }

    [Fact]
    public void MessageProvenanceRecorded_updates_state()
    {
        var msgId = Guid.NewGuid();
        var state = Decider.Given<ModelState>(
            new ModelEvent.MessageAdded(msgId, "OrderPlaced", null, Guid.NewGuid(), [], null, []),
            new ModelEvent.MessageProvenanceRecorded(msgId, FactStatus.Unknown, null)).State;

        Assert.Equal(FactStatus.Unknown, state.Messages[msgId].FactStatus);
        Assert.Null(state.Messages[msgId].Provenance);
    }
}
