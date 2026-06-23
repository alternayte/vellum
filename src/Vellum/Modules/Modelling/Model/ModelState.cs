using System.Collections.Immutable;
using Vellum.Kernel.Aggregates;

namespace Vellum.Modules.Modelling.Model;

public sealed record ElementState(
    Guid Id, ElementKind Kind, string Name, string? Description,
    string? Technology, Guid? OwnerId, ElementStatus Status,
    Guid? ParentId, string[] Tags);

public sealed record RelationshipState(
    Guid Id, Guid FromId, Guid ToId, string? Label,
    string? Technology, Guid? MessageId);

public sealed record MessageState(
    Guid Id, string Name, string? Description,
    Guid ProducerId, Guid[] ConsumerIds,
    Guid? SchemaId, string[] Tags);

public sealed record ModelState(
    ImmutableDictionary<Guid, ElementState> Elements,
    ImmutableDictionary<Guid, RelationshipState> Relationships,
    ImmutableDictionary<Guid, MessageState> Messages)
    : IAggregateState<ModelState, ModelEvent>
{
    public static ModelState Initial => new(
        ImmutableDictionary<Guid, ElementState>.Empty,
        ImmutableDictionary<Guid, RelationshipState>.Empty,
        ImmutableDictionary<Guid, MessageState>.Empty);

    public ModelState Evolve(ModelEvent @event) => @event switch
    {
        ModelEvent.ElementAdded e => this with
        {
            Elements = Elements.Add(e.Id, new ElementState(
                e.Id, e.Kind, e.Name, e.Description, e.Technology,
                e.OwnerId, e.Status, e.ParentId, e.Tags))
        },
        ModelEvent.ElementRenamed e => WithElement(e.ElementId, el => el with { Name = e.Name }),
        ModelEvent.ElementDescriptionChanged e => WithElement(e.ElementId, el => el with { Description = e.Description }),
        ModelEvent.ElementTechnologyChanged e => WithElement(e.ElementId, el => el with { Technology = e.Technology }),
        ModelEvent.ElementOwnerChanged e => WithElement(e.ElementId, el => el with { OwnerId = e.OwnerId }),
        ModelEvent.ElementReparented e => WithElement(e.ElementId, el => el with { ParentId = e.ParentId }),
        ModelEvent.ElementStatusChanged e => WithElement(e.ElementId, el => el with { Status = e.Status }),
        ModelEvent.ElementRetagged e => WithElement(e.ElementId, el => el with { Tags = e.Tags }),
        ModelEvent.ElementRemoved e => this with { Elements = Elements.Remove(e.ElementId) },

        ModelEvent.RelationshipAdded e => this with
        {
            Relationships = Relationships.Add(e.Id, new RelationshipState(
                e.Id, e.FromId, e.ToId, e.Label, e.Technology, e.MessageId))
        },
        ModelEvent.RelationshipLabelChanged e => WithRelationship(e.RelationshipId, r => r with { Label = e.Label }),
        ModelEvent.RelationshipTechnologyChanged e => WithRelationship(e.RelationshipId, r => r with { Technology = e.Technology }),
        ModelEvent.RelationshipRemoved e => this with { Relationships = Relationships.Remove(e.RelationshipId) },

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

        _ => this
    };

    private ModelState WithElement(Guid id, Func<ElementState, ElementState> update) =>
        this with { Elements = Elements.SetItem(id, update(Elements[id])) };

    private ModelState WithRelationship(Guid id, Func<RelationshipState, RelationshipState> update) =>
        this with { Relationships = Relationships.SetItem(id, update(Relationships[id])) };

    private ModelState WithMessage(Guid id, Func<MessageState, MessageState> update) =>
        this with { Messages = Messages.SetItem(id, update(Messages[id])) };
}
