using System.Collections.Immutable;
using Vellum.Kernel.Aggregates;

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
    : IAggregateState<ModelState, ModelEvent>
{
    public static ModelState Initial => new(
        ImmutableDictionary<ElementId, ElementState>.Empty,
        ImmutableDictionary<RelationshipId, RelationshipState>.Empty,
        ImmutableDictionary<MessageId, MessageState>.Empty);

    public ModelState Evolve(ModelEvent @event) => @event switch
    {
        ModelEvent.ElementAdded e => this with
        {
            Elements = Elements.Add(e.Id, new ElementState(
                e.Id, e.Kind, e.Name, e.Description, e.Technology,
                e.OwnerId, e.Status, e.ParentId, e.Tags, Icon: null))
        },
        ModelEvent.ElementRenamed e => WithElement(e.ElementId, el => el with { Name = e.Name }),
        ModelEvent.ElementDescriptionChanged e => WithElement(e.ElementId, el => el with { Description = e.Description }),
        ModelEvent.ElementTechnologyChanged e => WithElement(e.ElementId, el => el with { Technology = e.Technology }),
        ModelEvent.ElementOwnerChanged e => WithElement(e.ElementId, el => el with { OwnerId = e.OwnerId }),
        ModelEvent.ElementReparented e => WithElement(e.ElementId, el => el with { ParentId = e.ParentId }),
        ModelEvent.ElementStatusChanged e => WithElement(e.ElementId, el => el with { Status = e.Status }),
        ModelEvent.ElementRetagged e => WithElement(e.ElementId, el => el with { Tags = e.Tags }),
        ModelEvent.ElementIconChanged e => WithElement(e.ElementId, el => el with { Icon = e.Icon }),
        ModelEvent.ElementRemoved e => this with { Elements = Elements.Remove(e.ElementId) },
        ModelEvent.ElementProvenanceRecorded e => WithElement(e.ElementId,
            el => el with { FactStatus = e.Status, Provenance = e.Provenance }),

        ModelEvent.RelationshipAdded e => this with
        {
            Relationships = Relationships.Add(e.Id, new RelationshipState(
                e.Id, e.FromId, e.ToId, e.Label, e.Technology, e.MessageId, null, e.Kind))
        },
        ModelEvent.RelationshipLabelChanged e => WithRelationship(e.RelationshipId, r => r with { Label = e.Label }),
        ModelEvent.RelationshipTechnologyChanged e => WithRelationship(e.RelationshipId, r => r with { Technology = e.Technology }),
        ModelEvent.RelationshipLineShapeChanged e => WithRelationship(e.RelationshipId, r => r with { LineShape = e.LineShape }),
        ModelEvent.RelationshipKindChanged e => WithRelationship(e.RelationshipId, r => r with { Kind = e.Kind }),
        ModelEvent.RelationshipRemoved e => this with { Relationships = Relationships.Remove(e.RelationshipId) },
        ModelEvent.RelationshipProvenanceRecorded e => WithRelationship(e.RelationshipId,
            r => r with { FactStatus = e.Status, Provenance = e.Provenance }),

        ModelEvent.MessageAdded m => this with
        {
            Messages = Messages.Add(m.Id, new MessageState(
                m.Id, m.Name, m.Description, m.ProducerId, m.ConsumerIds, m.SchemaId, m.Tags))
        },
        ModelEvent.MessageUpdated m => WithMessage(m.MessageId, msg =>
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
        ModelEvent.MessageRemoved m => this with { Messages = Messages.Remove(m.MessageId) },
        ModelEvent.MessageProvenanceRecorded m => WithMessage(m.MessageId,
            msg => msg with { FactStatus = m.Status, Provenance = m.Provenance }),

        _ => this
    };

    private ModelState WithElement(ElementId id, Func<ElementState, ElementState> update) =>
        this with { Elements = Elements.SetItem(id, update(Elements[id])) };

    private ModelState WithRelationship(RelationshipId id, Func<RelationshipState, RelationshipState> update) =>
        this with { Relationships = Relationships.SetItem(id, update(Relationships[id])) };

    private ModelState WithMessage(MessageId id, Func<MessageState, MessageState> update) =>
        this with { Messages = Messages.SetItem(id, update(Messages[id])) };
}
