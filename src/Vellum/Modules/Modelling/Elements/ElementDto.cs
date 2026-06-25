namespace Vellum.Modules.Modelling.Elements;

public sealed record ElementDto(
    Guid Id, string Kind, string Name, string? Description,
    string? Technology, Guid? OwnerId, string Status,
    Guid? ParentId, string[] Tags, string? Icon);
