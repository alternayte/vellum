using Vellum.Modules.Modelling.Model;

namespace Vellum.Modules.Modelling.Relationships;

public sealed record RelationshipDto(
    Guid Id, Guid FromId, Guid ToId, string? Label,
    string? Technology, Guid? MessageId, string? LineShape,
    string Kind, string FactStatus, ProvenanceDto? Provenance, StaleDto? Stale = null);
