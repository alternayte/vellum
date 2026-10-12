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
    /// </summary>
    public sealed record BranchForked(
        Guid SourceStreamId, long SourceVersion,
        ElementState[] Elements, RelationshipState[] Relationships, MessageState[] Messages);

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
}
