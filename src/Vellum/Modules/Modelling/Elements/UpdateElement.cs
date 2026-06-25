using Vellum.Kernel.Aggregates;
using Vellum.Kernel.CommandHandling;
using Vellum.Kernel.Results;
using Vellum.Modules.Modelling.Model;

namespace Vellum.Modules.Modelling.Elements;

public sealed record UpdateElementRequest(
    string? Name, string? Description, string? Technology,
    Guid? OwnerId, Guid? ParentId, string? Status, string[]? Tags,
    string? Icon,
    bool SetDescription = false, bool SetTechnology = false,
    bool SetOwnerId = false, bool SetParentId = false, bool SetIcon = false);

public sealed record UpdateElementCommandEnvelope(
    Guid ProjectId, Guid StreamId, Guid ElementId, string UserId, UpdateElementRequest Request);

public sealed class UpdateElementHandler : ICommandHandler<UpdateElementCommandEnvelope, CommandResult<ElementDto>>
{
    private readonly AggregateStore _store;
    private readonly ModelProjection _projection;

    public UpdateElementHandler(AggregateStore store, ModelProjection projection)
    {
        _store = store;
        _projection = projection;
    }

    public async Task<CommandResult<ElementDto>> HandleAsync(UpdateElementCommandEnvelope cmd, CancellationToken ct = default)
    {
        var (state, version) = await _store.LoadAsync<ModelState, ModelEvent>(cmd.StreamId, ct);

        ElementStatus? status = null;
        if (cmd.Request.Status is not null)
        {
            if (!Enum.TryParse<ElementStatus>(cmd.Request.Status, ignoreCase: true, out var parsed))
                return new CommandResult<ElementDto>.Invalid([new ValidationError("status", "Invalid status")]);
            status = parsed;
        }

        var updateCmd = new UpdateElementCommand(
            cmd.ElementId,
            Name: cmd.Request.Name, SetName: cmd.Request.Name is not null,
            Description: cmd.Request.Description, SetDescription: cmd.Request.SetDescription || cmd.Request.Description is not null,
            Technology: cmd.Request.Technology, SetTechnology: cmd.Request.SetTechnology || cmd.Request.Technology is not null,
            OwnerId: cmd.Request.OwnerId, SetOwnerId: cmd.Request.SetOwnerId,
            ParentId: cmd.Request.ParentId, SetParentId: cmd.Request.SetParentId,
            Status: status,
            Tags: cmd.Request.Tags,
            Icon: cmd.Request.Icon, SetIcon: cmd.Request.SetIcon || cmd.Request.Icon is not null);

        var result = ModelDecider.UpdateElement(state, updateCmd);
        if (result is not CommandResult<IReadOnlyList<ModelEvent>>.Success success)
        {
            return result switch
            {
                CommandResult<IReadOnlyList<ModelEvent>>.Invalid inv => new CommandResult<ElementDto>.Invalid(inv.Errors),
                CommandResult<IReadOnlyList<ModelEvent>>.NotFound n => new CommandResult<ElementDto>.NotFound(n.Message),
                _ => new CommandResult<ElementDto>.Conflict("Unexpected error")
            };
        }

        if (success.Value.Count > 0)
        {
            var metadata = new EventMetadata { ActorId = Guid.Parse(cmd.UserId), CorrelationId = Guid.NewGuid() };
            _projection.SetContext(cmd.ProjectId, cmd.StreamId);
            var newState = success.Value.Aggregate(state, (s, e) => s.Evolve(e));
            await _store.SaveAsync(cmd.StreamId, "model", version, newState, success.Value, metadata, ct);
            state = newState;
        }

        var element = state.Elements[cmd.ElementId];
        return new CommandResult<ElementDto>.Success(new ElementDto(
            element.Id, element.Kind.ToString().ToLowerInvariant(), element.Name,
            element.Description, element.Technology, element.OwnerId,
            element.Status.ToString().ToLowerInvariant(), element.ParentId, element.Tags, element.Icon));
    }
}
