namespace Vellum.Modules.Modelling.Relationships;

public sealed record RelationshipDto(
    Guid Id, Guid FromId, Guid ToId, string? Label,
    string? Technology, Guid? MessageId);
