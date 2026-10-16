using Vellum.Modules.Modelling;

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

    /// <summary>
    /// The truth label and the provenance of the schema, as an accepted import proposal or a
    /// model document gives them. A stream without this event holds a schema that is
    /// <c>declared</c> and has no provenance. A stream written before the event existed is
    /// read the same way: a replay relabels nothing.
    /// </summary>
    public sealed record SchemaProvenanceRecorded(Guid SchemaId, FactStatus Status, Provenance? Provenance);
}
