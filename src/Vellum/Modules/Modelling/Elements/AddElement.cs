using Vellum.Kernel.Aggregates;
using Vellum.Kernel.CommandHandling;
using Vellum.Kernel.Results;
using Vellum.Modules.Modelling.Model;

namespace Vellum.Modules.Modelling.Elements;

public sealed record AddElementRequest(
    Guid Id, string Kind, string Name, string? Description,
    string? Technology, Guid? OwnerId, string? Status,
    Guid? ParentId, string[]? Tags);

public sealed record AddElementCommandEnvelope(
    Guid ProjectId, Guid StreamId, string UserId, AddElementRequest Request);

public sealed class AddElementHandler : ICommandHandler<AddElementCommandEnvelope, CommandResult<ElementDto>>
{
    private readonly AggregateStore _store;
    private readonly ModelProjection _projection;

    public AddElementHandler(AggregateStore store, ModelProjection projection)
    {
        _store = store;
        _projection = projection;
    }

    public async Task<CommandResult<ElementDto>> HandleAsync(AddElementCommandEnvelope cmd, CancellationToken ct = default)
    {
        var (state, version) = await _store.LoadAsync<ModelState, ModelEvent>(cmd.StreamId, ct);

        if (!Enum.TryParse<ElementKind>(cmd.Request.Kind, ignoreCase: true, out var kind))
            return new CommandResult<ElementDto>.Invalid([new ValidationError("kind", "Invalid element kind")]);

        var status = ElementStatus.Current;
        if (cmd.Request.Status is not null && !Enum.TryParse(cmd.Request.Status, ignoreCase: true, out status))
            return new CommandResult<ElementDto>.Invalid([new ValidationError("status", "Invalid status")]);

        var addCmd = new AddElementCommand(
            cmd.Request.Id, kind, cmd.Request.Name, cmd.Request.Description,
            cmd.Request.Technology, cmd.Request.OwnerId, status,
            cmd.Request.ParentId, cmd.Request.Tags ?? []);

        var result = ModelDecider.AddElement(state, addCmd);
        if (result is not CommandResult<IReadOnlyList<ModelEvent>>.Success success)
        {
            return result switch
            {
                CommandResult<IReadOnlyList<ModelEvent>>.Invalid inv =>
                    new CommandResult<ElementDto>.Invalid(inv.Errors),
                CommandResult<IReadOnlyList<ModelEvent>>.Conflict c =>
                    new CommandResult<ElementDto>.Conflict(c.Message),
                _ => new CommandResult<ElementDto>.Conflict("Unexpected error")
            };
        }

        var metadata = new EventMetadata
        {
            ActorId = Guid.Parse(cmd.UserId),
            CorrelationId = Guid.NewGuid()
        };

        _projection.SetContext(cmd.ProjectId, cmd.StreamId);
        var newState = success.Value.Aggregate(state, (s, e) => s.Evolve(e));
        await _store.SaveAsync(cmd.StreamId, "model", version, newState, success.Value, metadata, ct);

        var element = newState.Elements[cmd.Request.Id];
        return new CommandResult<ElementDto>.Success(new ElementDto(
            element.Id, element.Kind.ToString().ToLowerInvariant(), element.Name,
            element.Description, element.Technology, element.OwnerId,
            element.Status.ToString().ToLowerInvariant(), element.ParentId, element.Tags, element.Icon));
    }
}
