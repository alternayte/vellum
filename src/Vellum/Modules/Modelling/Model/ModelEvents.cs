namespace Vellum.Modules.Modelling.Model;

public enum ElementKind { Actor, System, App, Store, Component }
public enum ElementStatus { Current, Planned, Deprecated, Removed }

/// <summary>
/// Structural edges carry no executable semantics. Behavioural edges (invokes, consumes,
/// publishes) are the only edges a scenario can reference. SDD §4: a line alone does not
/// imply executable semantics.
/// </summary>
public enum RelationshipKind { Structural = 0, Behavioural = 1 }

public abstract record ModelEvent
{
    // Element events
    public sealed record ElementAdded(
        ElementId Id, ElementKind Kind, string Name, string? Description,
        string? Technology, Guid? OwnerId, ElementStatus Status,
        ElementId? ParentId, string[] Tags) : ModelEvent;

    public sealed record ElementRenamed(ElementId ElementId, string Name) : ModelEvent;
    public sealed record ElementDescriptionChanged(ElementId ElementId, string? Description) : ModelEvent;
    public sealed record ElementTechnologyChanged(ElementId ElementId, string? Technology) : ModelEvent;
    public sealed record ElementOwnerChanged(ElementId ElementId, Guid? OwnerId) : ModelEvent;
    public sealed record ElementReparented(ElementId ElementId, ElementId? ParentId) : ModelEvent;
    public sealed record ElementStatusChanged(ElementId ElementId, ElementStatus Status) : ModelEvent;
    public sealed record ElementRetagged(ElementId ElementId, string[] Tags) : ModelEvent;
    public sealed record ElementIconChanged(ElementId ElementId, string? Icon) : ModelEvent;
    public sealed record ElementRemoved(ElementId ElementId) : ModelEvent;
    public sealed record ElementProvenanceRecorded(ElementId ElementId, FactStatus Status, Provenance? Provenance) : ModelEvent;

    // Relationship events
    public sealed record RelationshipAdded(
        RelationshipId Id, ElementId FromId, ElementId ToId, string? Label,
        string? Technology, MessageId? MessageId,
        RelationshipKind Kind = RelationshipKind.Structural) : ModelEvent;

    public sealed record RelationshipLabelChanged(RelationshipId RelationshipId, string? Label) : ModelEvent;
    public sealed record RelationshipTechnologyChanged(RelationshipId RelationshipId, string? Technology) : ModelEvent;
    public sealed record RelationshipLineShapeChanged(RelationshipId RelationshipId, string? LineShape) : ModelEvent;
    public sealed record RelationshipKindChanged(RelationshipId RelationshipId, RelationshipKind Kind) : ModelEvent;
    public sealed record RelationshipRemoved(RelationshipId RelationshipId) : ModelEvent;
    public sealed record RelationshipProvenanceRecorded(RelationshipId RelationshipId, FactStatus Status, Provenance? Provenance) : ModelEvent;

    // Message events
    public sealed record MessageAdded(
        MessageId Id, string Name, string? Description,
        ElementId ProducerId, ElementId[] ConsumerIds,
        SchemaId? SchemaId, string[] Tags) : ModelEvent;

    public sealed record MessageUpdated(
        MessageId MessageId,
        string? Name, string? Description,
        ElementId? ProducerId, ElementId[]? ConsumerIds,
        SchemaId? SchemaId, bool SetSchemaId,
        string[]? Tags) : ModelEvent;

    public sealed record MessageRemoved(MessageId MessageId) : ModelEvent;
    public sealed record MessageProvenanceRecorded(MessageId MessageId, FactStatus Status, Provenance? Provenance) : ModelEvent;

    private ModelEvent() { }
}
