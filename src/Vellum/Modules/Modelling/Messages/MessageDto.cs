namespace Vellum.Modules.Modelling.Messages;

public sealed record MessageDto(
    Guid Id, string Name, string? Description,
    Guid ProducerId, Guid[] ConsumerIds,
    Guid? SchemaId, string[] Tags);
