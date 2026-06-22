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

public sealed record ModelState(
    ImmutableDictionary<Guid, ElementState> Elements,
    ImmutableDictionary<Guid, RelationshipState> Relationships)
    : IAggregateState<ModelState, ModelEvent>
{
    public static ModelState Initial => new(
        ImmutableDictionary<Guid, ElementState>.Empty,
        ImmutableDictionary<Guid, RelationshipState>.Empty);

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

        _ => this
    };

    private ModelState WithElement(Guid id, Func<ElementState, ElementState> update) =>
        this with { Elements = Elements.SetItem(id, update(Elements[id])) };

    private ModelState WithRelationship(Guid id, Func<RelationshipState, RelationshipState> update) =>
        this with { Relationships = Relationships.SetItem(id, update(Relationships[id])) };
}
