namespace Vellum.Modules.Schemas;

public sealed record SchemaDto(
    Guid Id, string Name, string? Description,
    string Content, int Version);
