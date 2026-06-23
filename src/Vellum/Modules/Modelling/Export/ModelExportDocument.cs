namespace Vellum.Modules.Modelling.Export;

public sealed record ModelExportDocument(
    string Version,
    ProjectInfo Project,
    IReadOnlyList<ElementExport> Elements,
    IReadOnlyList<RelationshipExport> Relationships,
    IReadOnlyList<MessageExport> Messages,
    IReadOnlyList<SchemaExport> Schemas);

public sealed record ProjectInfo(string Name);

public sealed record ElementExport(
    Guid Id, string Kind, string Name, string? Description,
    string? Technology, string Status, Guid? ParentId, string[] Tags);

public sealed record RelationshipExport(
    Guid Id, Guid FromId, Guid ToId, string? Label, string? Technology);

public sealed record MessageExport(
    Guid Id, string Name, string? Description,
    Guid ProducerId, Guid[] ConsumerIds, Guid? SchemaId, string[] Tags);

public sealed record SchemaExport(
    Guid Id, string Name, string? Description, string Content, int Version);
