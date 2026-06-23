namespace Vellum.Modules.Modelling.Model;

public enum ElementKind { Actor, System, App, Store, Component }
public enum ElementStatus { Current, Planned, Deprecated, Removed }

public abstract record ModelEvent
{
    // Element events
    public sealed record ElementAdded(
        Guid Id, ElementKind Kind, string Name, string? Description,
        string? Technology, Guid? OwnerId, ElementStatus Status,
        Guid? ParentId, string[] Tags) : ModelEvent;

    public sealed record ElementRenamed(Guid ElementId, string Name) : ModelEvent;
    public sealed record ElementDescriptionChanged(Guid ElementId, string? Description) : ModelEvent;
    public sealed record ElementTechnologyChanged(Guid ElementId, string? Technology) : ModelEvent;
    public sealed record ElementOwnerChanged(Guid ElementId, Guid? OwnerId) : ModelEvent;
    public sealed record ElementReparented(Guid ElementId, Guid? ParentId) : ModelEvent;
    public sealed record ElementStatusChanged(Guid ElementId, ElementStatus Status) : ModelEvent;
    public sealed record ElementRetagged(Guid ElementId, string[] Tags) : ModelEvent;
    public sealed record ElementRemoved(Guid ElementId) : ModelEvent;

    // Relationship events
    public sealed record RelationshipAdded(
        Guid Id, Guid FromId, Guid ToId, string? Label,
        string? Technology, Guid? MessageId) : ModelEvent;

    public sealed record RelationshipLabelChanged(Guid RelationshipId, string? Label) : ModelEvent;
    public sealed record RelationshipTechnologyChanged(Guid RelationshipId, string? Technology) : ModelEvent;
    public sealed record RelationshipRemoved(Guid RelationshipId) : ModelEvent;

    // Message events
    public sealed record MessageAdded(
        Guid Id, string Name, string? Description,
        Guid ProducerId, Guid[] ConsumerIds,
        Guid? SchemaId, string[] Tags) : ModelEvent;

    public sealed record MessageUpdated(
        Guid MessageId,
        string? Name, string? Description,
        Guid? ProducerId, Guid[]? ConsumerIds,
        Guid? SchemaId, bool SetSchemaId,
        string[]? Tags) : ModelEvent;

    public sealed record MessageRemoved(Guid MessageId) : ModelEvent;

    private ModelEvent() { }
}
