using Deedbox;
using Vellum.Modules.Modelling.Model;
using Vellum.Shared;

namespace Vellum.Modules.Modelling.Elements;

public sealed record AddElementRequest(
    Guid Id, string Kind, string Name, string? Description,
    string? Technology, Guid? OwnerId, string? Status,
    Guid? ParentId, string[]? Tags);

public sealed record AddElementCommandEnvelope(Guid ProjectId, Guid StreamId, AddElementRequest Request);

public sealed class AddElementHandler : ICommandHandler<AddElementCommandEnvelope, CommandResult<ElementDto>>
{
    private readonly IEventStore _store;

    public AddElementHandler(IEventStore store) => _store = store;

    public async Task<CommandResult<ElementDto>> HandleAsync(AddElementCommandEnvelope cmd, CancellationToken ct = default)
    {
        if (!ModelEnum.TryParseDefined(cmd.Request.Kind, out ElementKind kind))
            return new CommandResult<ElementDto>.Invalid([new ValidationError("kind", "Invalid element kind")]);

        var status = ElementStatus.Current;
        if (cmd.Request.Status is not null && !ModelEnum.TryParseDefined(cmd.Request.Status, out status))
            return new CommandResult<ElementDto>.Invalid([new ValidationError("status", "Invalid status")]);

        var addCmd = new AddElementCommand(
            cmd.Request.Id, kind, cmd.Request.Name, cmd.Request.Description,
            cmd.Request.Technology, cmd.Request.OwnerId, status,
            cmd.Request.ParentId, cmd.Request.Tags ?? []);

        return await _store.ForProject(cmd.ProjectId).Decide<ModelState, ElementDto>(
            cmd.StreamId,
            state => ModelDecider.AddElement(state, addCmd),
            state => ToDto(state.Elements[cmd.Request.Id]),
            ct);
    }

    internal static ElementDto ToDto(ElementState element) => new(
        element.Id, element.Kind.ToString().ToLowerInvariant(), element.Name,
        element.Description, element.Technology, element.OwnerId,
        element.Status.ToString().ToLowerInvariant(), element.ParentId, element.Tags, element.Icon,
        element.FactStatus.ToApiString(), ProvenanceMapper.ToDto(element.Provenance));
}
