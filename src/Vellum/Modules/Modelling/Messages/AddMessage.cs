using Vellum.Kernel.Aggregates;
using Vellum.Kernel.CommandHandling;
using Vellum.Kernel.Results;
using Vellum.Modules.Modelling.Model;

namespace Vellum.Modules.Modelling.Messages;

public sealed record AddMessageRequest(
    Guid Id, string Name, string? Description,
    Guid ProducerId, Guid[] ConsumerIds,
    Guid? SchemaId, string[]? Tags);

public sealed record AddMessageCommandEnvelope(
    Guid ProjectId, Guid StreamId, string UserId, AddMessageRequest Request);

public sealed class AddMessageHandler : ICommandHandler<AddMessageCommandEnvelope, CommandResult<MessageDto>>
{
    private readonly AggregateStore _store;
    private readonly ModelProjection _projection;

    public AddMessageHandler(AggregateStore store, ModelProjection projection)
    {
        _store = store;
        _projection = projection;
    }

    public async Task<CommandResult<MessageDto>> HandleAsync(AddMessageCommandEnvelope cmd, CancellationToken ct = default)
    {
        var (state, version) = await _store.LoadAsync<ModelState, ModelEvent>(cmd.StreamId, ct);

        var addCmd = new AddMessageCommand(
            cmd.Request.Id, cmd.Request.Name, cmd.Request.Description,
            cmd.Request.ProducerId, cmd.Request.ConsumerIds,
            cmd.Request.SchemaId, cmd.Request.Tags ?? []);

        var result = ModelDecider.AddMessage(state, addCmd);
        if (result is not CommandResult<IReadOnlyList<ModelEvent>>.Success success)
        {
            return result switch
            {
                CommandResult<IReadOnlyList<ModelEvent>>.Invalid inv =>
                    new CommandResult<MessageDto>.Invalid(inv.Errors),
                CommandResult<IReadOnlyList<ModelEvent>>.Conflict c =>
                    new CommandResult<MessageDto>.Conflict(c.Message),
                _ => new CommandResult<MessageDto>.Conflict("Unexpected error")
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

        var msg = newState.Messages[cmd.Request.Id];
        return new CommandResult<MessageDto>.Success(new MessageDto(
            msg.Id, msg.Name, msg.Description,
            msg.ProducerId, msg.ConsumerIds, msg.SchemaId, msg.Tags));
    }
}
