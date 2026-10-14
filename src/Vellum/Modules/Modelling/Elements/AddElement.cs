using Deedbox;
using Vellum.Modules.Modelling.Model;
using Vellum.Shared;

namespace Vellum.Modules.Modelling.Elements;

public sealed record AddElementRequest(
    Guid Id, string Kind, string Name, string? Description,
    string? Technology, Guid? OwnerId, string? Status,
    Guid? ParentId, string[]? Tags);

public static class AddElement
{
    public static async Task<CommandResult<ElementDto>> HandleAsync(
        IEventStore store, Guid projectId, Guid streamId, AddElementRequest request, CancellationToken ct)
    {
        if (!ModelEnum.TryParseDefined(request.Kind, out ElementKind kind))
            return new CommandResult<ElementDto>.Invalid([new ValidationError("kind", "Invalid element kind")]);

        var status = ElementStatus.Current;
        if (request.Status is not null && !ModelEnum.TryParseDefined(request.Status, out status))
            return new CommandResult<ElementDto>.Invalid([new ValidationError("status", "Invalid status")]);

        var addCmd = new AddElementCommand(
            request.Id, kind, request.Name, request.Description,
            request.Technology, request.OwnerId, status,
            request.ParentId, request.Tags ?? []);

        return await store.ForProject(projectId).Decide<ModelState, ElementDto>(
            streamId,
            state => ModelDecider.AddElement(state, addCmd),
            state => ToDto(state.Elements[request.Id]),
            ct);
    }

    internal static ElementDto ToDto(ElementState element) => new(
        element.Id, element.Kind.ToString().ToLowerInvariant(), element.Name,
        element.Description, element.Technology, element.OwnerId,
        element.Status.ToString().ToLowerInvariant(), element.ParentId, element.Tags, element.Icon,
        element.FactStatus.ToApiString(), ProvenanceMapper.ToDto(element.Provenance), StaleDto.From(element.Stale));
}
