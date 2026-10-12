using System.Collections.Immutable;
using Deedbox;

namespace Vellum.Modules.Modelling.Model;

public sealed record ElementState(
    ElementId Id, ElementKind Kind, string Name, string? Description,
    string? Technology, Guid? OwnerId, ElementStatus Status,
    ElementId? ParentId, string[] Tags, string? Icon,
    FactStatus FactStatus = FactStatus.Declared,
    Provenance? Provenance = null);

public sealed record RelationshipState(
    RelationshipId Id, ElementId FromId, ElementId ToId, string? Label,
    string? Technology, MessageId? MessageId, string? LineShape,
    RelationshipKind Kind = RelationshipKind.Structural,
    FactStatus FactStatus = FactStatus.Declared,
    Provenance? Provenance = null);

public sealed record MessageState(
    MessageId Id, string Name, string? Description,
    ElementId ProducerId, ElementId[] ConsumerIds,
    SchemaId? SchemaId, string[] Tags,
    FactStatus FactStatus = FactStatus.Declared,
    Provenance? Provenance = null);

public sealed record ModelState(
    ImmutableDictionary<ElementId, ElementState> Elements,
    ImmutableDictionary<RelationshipId, RelationshipState> Relationships,
    ImmutableDictionary<MessageId, MessageState> Messages)
    : IState<ModelState>
{
    public static ModelState Initial => new(
        ImmutableDictionary<ElementId, ElementState>.Empty,
        ImmutableDictionary<RelationshipId, RelationshipState>.Empty,
        ImmutableDictionary<MessageId, MessageState>.Empty);

    public static ModelState Evolve(ModelState state, object @event) => @event switch
    {
        ModelEvent.BranchForked e => new ModelState(
            e.Elements.ToImmutableDictionary(el => el.Id),
            e.Relationships.ToImmutableDictionary(r => r.Id),
            e.Messages.ToImmutableDictionary(m => m.Id)),

        ModelEvent.ElementAdded e => state with
        {
            Elements = state.Elements.SetItem(e.Id, new ElementState(
                e.Id, e.Kind, e.Name, e.Description, e.Technology,
                e.OwnerId, e.Status, e.ParentId, e.Tags, Icon: null))
        },
        ModelEvent.ElementRenamed e => state.WithElement(e.ElementId, el => el with { Name = e.Name }),
        ModelEvent.ElementDescriptionChanged e => state.WithElement(e.ElementId, el => el with { Description = e.Description }),
        ModelEvent.ElementTechnologyChanged e => state.WithElement(e.ElementId, el => el with { Technology = e.Technology }),
        ModelEvent.ElementOwnerChanged e => state.WithElement(e.ElementId, el => el with { OwnerId = e.OwnerId }),
        ModelEvent.ElementReparented e => state.WithElement(e.ElementId, el => el with { ParentId = e.ParentId }),
        ModelEvent.ElementStatusChanged e => state.WithElement(e.ElementId, el => el with { Status = e.Status }),
        ModelEvent.ElementRetagged e => state.WithElement(e.ElementId, el => el with { Tags = e.Tags }),
        ModelEvent.ElementIconChanged e => state.WithElement(e.ElementId, el => el with { Icon = e.Icon }),
        ModelEvent.ElementRemoved e => state with { Elements = state.Elements.Remove(e.ElementId) },
        ModelEvent.ElementProvenanceRecorded e => state.WithElement(e.ElementId,
            el => el with { FactStatus = e.Status, Provenance = e.Provenance }),

        ModelEvent.RelationshipAdded e => state with
        {
            Relationships = state.Relationships.SetItem(e.Id, new RelationshipState(
                e.Id, e.FromId, e.ToId, e.Label, e.Technology, e.MessageId, null, e.Kind))
        },
        ModelEvent.RelationshipLabelChanged e => state.WithRelationship(e.RelationshipId, r => r with { Label = e.Label }),
        ModelEvent.RelationshipTechnologyChanged e => state.WithRelationship(e.RelationshipId, r => r with { Technology = e.Technology }),
        ModelEvent.RelationshipLineShapeChanged e => state.WithRelationship(e.RelationshipId, r => r with { LineShape = e.LineShape }),
        ModelEvent.RelationshipKindChanged e => state.WithRelationship(e.RelationshipId, r => r with { Kind = e.Kind }),
        ModelEvent.RelationshipRemoved e => state with { Relationships = state.Relationships.Remove(e.RelationshipId) },
        ModelEvent.RelationshipProvenanceRecorded e => state.WithRelationship(e.RelationshipId,
            r => r with { FactStatus = e.Status, Provenance = e.Provenance }),

        ModelEvent.MessageAdded m => state with
        {
            Messages = state.Messages.SetItem(m.Id, new MessageState(
                m.Id, m.Name, m.Description, m.ProducerId, m.ConsumerIds, m.SchemaId, m.Tags))
        },
        ModelEvent.MessageUpdated m => state.WithMessage(m.MessageId, msg =>
        {
            var updated = msg;
            if (m.Name is not null) updated = updated with { Name = m.Name };
            if (m.Description is not null) updated = updated with { Description = m.Description };
            if (m.ProducerId is not null) updated = updated with { ProducerId = m.ProducerId.Value };
            if (m.ConsumerIds is not null) updated = updated with { ConsumerIds = m.ConsumerIds };
            if (m.SetSchemaId) updated = updated with { SchemaId = m.SchemaId };
            if (m.Tags is not null) updated = updated with { Tags = m.Tags };
            return updated;
        }),
        ModelEvent.MessageRemoved m => state with { Messages = state.Messages.Remove(m.MessageId) },
        ModelEvent.MessageProvenanceRecorded m => state.WithMessage(m.MessageId,
            msg => msg with { FactStatus = m.Status, Provenance = m.Provenance }),

        _ => state
    };

    private ModelState WithElement(ElementId id, Func<ElementState, ElementState> update) =>
        Elements.TryGetValue(id, out var element)
            ? this with { Elements = Elements.SetItem(id, update(element)) }
            : this;

    private ModelState WithRelationship(RelationshipId id, Func<RelationshipState, RelationshipState> update) =>
        Relationships.TryGetValue(id, out var relationship)
            ? this with { Relationships = Relationships.SetItem(id, update(relationship)) }
            : this;

    private ModelState WithMessage(MessageId id, Func<MessageState, MessageState> update) =>
        Messages.TryGetValue(id, out var message)
            ? this with { Messages = Messages.SetItem(id, update(message)) }
            : this;
}
