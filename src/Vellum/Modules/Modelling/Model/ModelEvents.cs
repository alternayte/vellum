using Vellum.Modules.Trace.Engine;

namespace Vellum.Modules.Modelling.Model;

public enum ElementKind { Actor, System, App, Store, Component }
public enum ElementStatus { Current, Planned, Deprecated, Removed }

/// <summary>
/// Structural edges carry no executable semantics. Behavioural edges (invokes, consumes,
/// publishes) are the only edges a scenario can reference. SDD §4: a line alone does not
/// imply executable semantics.
/// </summary>
public enum RelationshipKind { Structural = 0, Behavioural = 1 }

/// <summary>The events of a model stream. Plain records; the stream registers every nested type.</summary>
public static class ModelEvent
{
    /// <summary>
    /// The first event of a draft or variant stream: the complete model the branch starts
    /// from, read from <paramref name="SourceStreamId"/> at <paramref name="SourceVersion"/>.
    /// The contract collections were added later as nullable members, which keeps the event at
    /// shape version 1: a fork stored without them reads as null and means no contracts.
    /// </summary>
    public sealed record BranchForked(
        Guid SourceStreamId, long SourceVersion,
        ElementState[] Elements, RelationshipState[] Relationships, MessageState[] Messages,
        OperationState[]? Operations = null, ChannelState[]? Channels = null, SubscriptionState[]? Subscriptions = null);

    /// <summary>
    /// The branch <paramref name="BranchId"/> stopped being a live model: its draft was merged
    /// or abandoned. A merge appends it to the main stream with the merged changes; an abandon,
    /// or a merge that changes nothing, appends it to the draft's own stream. It changes no
    /// model state. The read model drops the branch's rows on it, so a rebuild does too.
    /// </summary>
    public sealed record BranchClosed(Guid BranchId);

    // Element events
    public sealed record ElementAdded(
        ElementId Id, ElementKind Kind, string Name, string? Description,
        string? Technology, Guid? OwnerId, ElementStatus Status,
        ElementId? ParentId, string[] Tags);

    public sealed record ElementRenamed(ElementId ElementId, string Name);
    public sealed record ElementDescriptionChanged(ElementId ElementId, string? Description);
    public sealed record ElementTechnologyChanged(ElementId ElementId, string? Technology);
    public sealed record ElementOwnerChanged(ElementId ElementId, Guid? OwnerId);
    public sealed record ElementReparented(ElementId ElementId, ElementId? ParentId);
    public sealed record ElementStatusChanged(ElementId ElementId, ElementStatus Status);
    public sealed record ElementRetagged(ElementId ElementId, string[] Tags);
    public sealed record ElementIconChanged(ElementId ElementId, string? Icon);
    public sealed record ElementRemoved(ElementId ElementId);
    public sealed record ElementProvenanceRecorded(ElementId ElementId, FactStatus Status, Provenance? Provenance);

    // Relationship events
    public sealed record RelationshipAdded(
        RelationshipId Id, ElementId FromId, ElementId ToId, string? Label,
        string? Technology, MessageId? MessageId,
        RelationshipKind Kind = RelationshipKind.Structural);

    public sealed record RelationshipLabelChanged(RelationshipId RelationshipId, string? Label);
    public sealed record RelationshipTechnologyChanged(RelationshipId RelationshipId, string? Technology);
    public sealed record RelationshipLineShapeChanged(RelationshipId RelationshipId, string? LineShape);
    public sealed record RelationshipKindChanged(RelationshipId RelationshipId, RelationshipKind Kind);
    public sealed record RelationshipRemoved(RelationshipId RelationshipId);
    public sealed record RelationshipProvenanceRecorded(RelationshipId RelationshipId, FactStatus Status, Provenance? Provenance);

    // Message events
    public sealed record MessageAdded(
        MessageId Id, string Name, string? Description,
        ElementId ProducerId, ElementId[] ConsumerIds,
        SchemaId? SchemaId, string[] Tags);

    public sealed record MessageUpdated(
        MessageId MessageId,
        string? Name, string? Description,
        ElementId? ProducerId, ElementId[]? ConsumerIds,
        SchemaId? SchemaId, bool SetSchemaId,
        string[]? Tags);

    public sealed record MessageRemoved(MessageId MessageId);
    public sealed record MessageProvenanceRecorded(MessageId MessageId, FactStatus Status, Provenance? Provenance);

    // Operation events. A change carries the whole contract, so a nullable schema ID is unambiguous.
    public sealed record OperationAdded(
        OperationId Id, ElementId OwnerId, string Name, Transport Transport,
        SchemaId? RequestSchemaId, SchemaId? ResponseSchemaId,
        int TimeoutMs, RetryPolicy Retry);

    public sealed record OperationChanged(
        OperationId OperationId, ElementId OwnerId, string Name, Transport Transport,
        SchemaId? RequestSchemaId, SchemaId? ResponseSchemaId,
        int TimeoutMs, RetryPolicy Retry);

    public sealed record OperationRemoved(OperationId OperationId);
    public sealed record OperationProvenanceRecorded(OperationId OperationId, FactStatus Status, Provenance? Provenance);

    // Channel events
    public sealed record ChannelAdded(ChannelId Id, string Name, ChannelKind Kind);
    public sealed record ChannelChanged(ChannelId ChannelId, string Name, ChannelKind Kind);
    public sealed record ChannelRemoved(ChannelId ChannelId);
    public sealed record ChannelProvenanceRecorded(ChannelId ChannelId, FactStatus Status, Provenance? Provenance);

    // Subscription events
    public sealed record SubscriptionAdded(
        SubscriptionId Id, ChannelId ChannelId, ElementId ConsumerId,
        RetryPolicy Retry, ChannelId? DeadLetterChannelId);

    public sealed record SubscriptionChanged(
        SubscriptionId SubscriptionId, ChannelId ChannelId, ElementId ConsumerId,
        RetryPolicy Retry, ChannelId? DeadLetterChannelId);

    public sealed record SubscriptionRemoved(SubscriptionId SubscriptionId);
    public sealed record SubscriptionProvenanceRecorded(SubscriptionId SubscriptionId, FactStatus Status, Provenance? Provenance);
}
