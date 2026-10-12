using System.Collections.Immutable;
using Deedbox;
using Vellum.Modules.Trace.Engine;

namespace Vellum.Modules.Modelling.Model;

public sealed record ElementState(
    ElementId Id, ElementKind Kind, string Name, string? Description,
    string? Technology, Guid? OwnerId, ElementStatus Status,
    ElementId? ParentId, string[] Tags, string? Icon,
    FactStatus FactStatus = FactStatus.Declared,
    Provenance? Provenance = null)
{
    // The generated equality compares an array by reference. Two copies of one element that
    // were each read from stored JSON would then differ, and a merge would call that a change.
    public bool Equals(ElementState? other) =>
        other is not null
        && Id == other.Id && Kind == other.Kind && Name == other.Name && Description == other.Description
        && Technology == other.Technology && OwnerId == other.OwnerId && Status == other.Status
        && ParentId == other.ParentId && Tags.AsSpan().SequenceEqual(other.Tags) && Icon == other.Icon
        && FactStatus == other.FactStatus && Provenance == other.Provenance;

    public override int GetHashCode() => HashCode.Combine(Id, Kind, Name, Status, ParentId);
}

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
    Provenance? Provenance = null)
{
    // By value for the two arrays, for the reason given on ElementState.
    public bool Equals(MessageState? other) =>
        other is not null
        && Id == other.Id && Name == other.Name && Description == other.Description
        && ProducerId == other.ProducerId && ConsumerIds.AsSpan().SequenceEqual(other.ConsumerIds)
        && SchemaId == other.SchemaId && Tags.AsSpan().SequenceEqual(other.Tags)
        && FactStatus == other.FactStatus && Provenance == other.Provenance;

    public override int GetHashCode() => HashCode.Combine(Id, Name, ProducerId, SchemaId);
}

/// <summary>An invocable contract that an element serves. A timeout of 0 declares none.</summary>
public sealed record OperationState(
    OperationId Id, ElementId OwnerId, string Name, Transport Transport,
    SchemaId? RequestSchemaId, SchemaId? ResponseSchemaId,
    int TimeoutMs, RetryPolicy Retry,
    FactStatus FactStatus = FactStatus.Declared,
    Provenance? Provenance = null);

public sealed record ChannelState(
    ChannelId Id, string Name, ChannelKind Kind,
    FactStatus FactStatus = FactStatus.Declared,
    Provenance? Provenance = null);

public sealed record SubscriptionState(
    SubscriptionId Id, ChannelId ChannelId, ElementId ConsumerId,
    RetryPolicy Retry, ChannelId? DeadLetterChannelId,
    FactStatus FactStatus = FactStatus.Declared,
    Provenance? Provenance = null);

public sealed record ModelState(
    ImmutableDictionary<ElementId, ElementState> Elements,
    ImmutableDictionary<RelationshipId, RelationshipState> Relationships,
    ImmutableDictionary<MessageId, MessageState> Messages)
    : IState<ModelState>
{
    // Contracts live on the model stream, so drafts, variants and revisions carry them.
    // They are init properties with empty defaults: a serialized state written before
    // contracts existed (a revision or a draft base snapshot) reads back with none.
    public ImmutableDictionary<OperationId, OperationState> Operations { get; init; } =
        ImmutableDictionary<OperationId, OperationState>.Empty;

    public ImmutableDictionary<ChannelId, ChannelState> Channels { get; init; } =
        ImmutableDictionary<ChannelId, ChannelState>.Empty;

    public ImmutableDictionary<SubscriptionId, SubscriptionState> Subscriptions { get; init; } =
        ImmutableDictionary<SubscriptionId, SubscriptionState>.Empty;

    public static ModelState Initial => new(
        ImmutableDictionary<ElementId, ElementState>.Empty,
        ImmutableDictionary<RelationshipId, RelationshipState>.Empty,
        ImmutableDictionary<MessageId, MessageState>.Empty);

    public static ModelState Evolve(ModelState state, object @event) => @event switch
    {
        ModelEvent.BranchForked e => new ModelState(
            e.Elements.ToImmutableDictionary(el => el.Id),
            e.Relationships.ToImmutableDictionary(r => r.Id),
            e.Messages.ToImmutableDictionary(m => m.Id))
        {
            Operations = (e.Operations ?? []).ToImmutableDictionary(o => o.Id),
            Channels = (e.Channels ?? []).ToImmutableDictionary(c => c.Id),
            Subscriptions = (e.Subscriptions ?? []).ToImmutableDictionary(s => s.Id),
        },

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

        ModelEvent.OperationAdded e => state with
        {
            Operations = state.Operations.SetItem(e.Id, new OperationState(
                e.Id, e.OwnerId, e.Name, e.Transport, e.RequestSchemaId, e.ResponseSchemaId, e.TimeoutMs, e.Retry))
        },
        ModelEvent.OperationChanged e => state.WithOperation(e.OperationId, o => o with
        {
            OwnerId = e.OwnerId, Name = e.Name, Transport = e.Transport,
            RequestSchemaId = e.RequestSchemaId, ResponseSchemaId = e.ResponseSchemaId,
            TimeoutMs = e.TimeoutMs, Retry = e.Retry,
        }),
        ModelEvent.OperationRemoved e => state with { Operations = state.Operations.Remove(e.OperationId) },
        ModelEvent.OperationProvenanceRecorded e => state.WithOperation(e.OperationId,
            o => o with { FactStatus = e.Status, Provenance = e.Provenance }),

        ModelEvent.ChannelAdded e => state with
        {
            Channels = state.Channels.SetItem(e.Id, new ChannelState(e.Id, e.Name, e.Kind))
        },
        ModelEvent.ChannelChanged e => state.WithChannel(e.ChannelId, c => c with { Name = e.Name, Kind = e.Kind }),
        ModelEvent.ChannelRemoved e => state with { Channels = state.Channels.Remove(e.ChannelId) },
        ModelEvent.ChannelProvenanceRecorded e => state.WithChannel(e.ChannelId,
            c => c with { FactStatus = e.Status, Provenance = e.Provenance }),

        ModelEvent.SubscriptionAdded e => state with
        {
            Subscriptions = state.Subscriptions.SetItem(e.Id, new SubscriptionState(
                e.Id, e.ChannelId, e.ConsumerId, e.Retry, e.DeadLetterChannelId))
        },
        ModelEvent.SubscriptionChanged e => state.WithSubscription(e.SubscriptionId, s => s with
        {
            ChannelId = e.ChannelId, ConsumerId = e.ConsumerId,
            Retry = e.Retry, DeadLetterChannelId = e.DeadLetterChannelId,
        }),
        ModelEvent.SubscriptionRemoved e => state with { Subscriptions = state.Subscriptions.Remove(e.SubscriptionId) },
        ModelEvent.SubscriptionProvenanceRecorded e => state.WithSubscription(e.SubscriptionId,
            s => s with { FactStatus = e.Status, Provenance = e.Provenance }),

        _ => state
    };

    private ModelState WithOperation(OperationId id, Func<OperationState, OperationState> update) =>
        Operations.TryGetValue(id, out var operation)
            ? this with { Operations = Operations.SetItem(id, update(operation)) }
            : this;

    private ModelState WithChannel(ChannelId id, Func<ChannelState, ChannelState> update) =>
        Channels.TryGetValue(id, out var channel)
            ? this with { Channels = Channels.SetItem(id, update(channel)) }
            : this;

    private ModelState WithSubscription(SubscriptionId id, Func<SubscriptionState, SubscriptionState> update) =>
        Subscriptions.TryGetValue(id, out var subscription)
            ? this with { Subscriptions = Subscriptions.SetItem(id, update(subscription)) }
            : this;

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
