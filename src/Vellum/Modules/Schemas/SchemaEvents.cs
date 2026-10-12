namespace Vellum.Modules.Schemas;

/// <summary>The events of a schema stream. Plain records; the stream registers every nested type.</summary>
public static class SchemaEvent
{
    public sealed record SchemaCreated(
        Guid Id, string Name, string? Description,
        string Content, int Version, Guid ProjectId);

    public sealed record SchemaUpdated(
        Guid SchemaId, string? Name, string? Description,
        string? Content, int? Version);

    public sealed record SchemaDeleted(Guid SchemaId);
}
