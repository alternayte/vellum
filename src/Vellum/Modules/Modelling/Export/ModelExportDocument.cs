using Vellum.Modules.Modelling.Model;

namespace Vellum.Modules.Modelling.Export;

public sealed record ModelExportDocument(
    string Version,
    ProjectInfo Project,
    IReadOnlyList<ElementExport> Elements,
    IReadOnlyList<RelationshipExport> Relationships,
    IReadOnlyList<MessageExport> Messages,
    IReadOnlyList<SchemaExport> Schemas);

public sealed record ProjectInfo(string Name);

public sealed record ProvenanceExport(
    string? Source, string? Revision, double? Confidence,
    Guid? AcceptedBy, DateTimeOffset? AcceptedAt)
{
    public static ProvenanceExport? From(Provenance? provenance) =>
        provenance is null
            ? null
            : new ProvenanceExport(
                provenance.Source, provenance.Revision, provenance.Confidence,
                provenance.AcceptedBy, provenance.AcceptedAt);

    public static ProvenanceExport? From(
        string? source, string? revision, double? confidence, Guid? acceptedBy, DateTimeOffset? acceptedAt) =>
        source is null && revision is null && confidence is null && acceptedBy is null && acceptedAt is null
            ? null
            : new ProvenanceExport(source, revision, confidence, acceptedBy, acceptedAt);

    public Provenance ToDomain() => new(Source, Revision, Confidence, AcceptedBy, AcceptedAt);
}

public sealed record ElementExport(
    Guid Id, string Kind, string Name, string? Description,
    string? Technology, string Status, Guid? ParentId, string[] Tags,
    string? Icon = null,
    string FactStatus = "declared", ProvenanceExport? Provenance = null);

public sealed record RelationshipExport(
    Guid Id, Guid FromId, Guid ToId, string? Label, string? Technology,
    string Kind = "structural", string? LineShape = null,
    string FactStatus = "declared", ProvenanceExport? Provenance = null);

public sealed record MessageExport(
    Guid Id, string Name, string? Description,
    Guid ProducerId, Guid[] ConsumerIds, Guid? SchemaId, string[] Tags,
    string FactStatus = "declared", ProvenanceExport? Provenance = null);

public sealed record SchemaExport(
    Guid Id, string Name, string? Description, string Content, int Version);
