namespace Vellum.Modules.Schemas;

public abstract record SchemaEvent
{
    public sealed record SchemaCreated(
        Guid Id, string Name, string? Description,
        string Content, int Version, Guid ProjectId) : SchemaEvent;

    public sealed record SchemaUpdated(
        Guid SchemaId, string? Name, string? Description,
        string? Content, int? Version) : SchemaEvent;

    public sealed record SchemaDeleted(Guid SchemaId) : SchemaEvent;

    private SchemaEvent() { }
}
