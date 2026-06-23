using Vellum.Kernel.Aggregates;
using Vellum.Kernel.CommandHandling;
using Vellum.Kernel.Results;
using Vellum.Modules.Modelling.Model;

namespace Vellum.Modules.Modelling.Messages;

public sealed record UpdateMessageRequest(
    string? Name, string? Description,
    Guid? ProducerId, Guid[]? ConsumerIds,
    Guid? SchemaId, bool SetSchemaId = false,
    bool SetDescription = false, bool SetProducerId = false,
    bool SetConsumerIds = false, string[]? Tags = null);

public sealed record UpdateMessageCommandEnvelope(
    Guid ProjectId, Guid StreamId, Guid MessageId, string UserId, UpdateMessageRequest Request);

public sealed class UpdateMessageHandler : ICommandHandler<UpdateMessageCommandEnvelope, CommandResult<MessageDto>>
{
    private readonly AggregateStore _store;
    private readonly ModelProjection _projection;

    public UpdateMessageHandler(AggregateStore store, ModelProjection projection)
    {
        _store = store;
        _projection = projection;
    }

    public async Task<CommandResult<MessageDto>> HandleAsync(UpdateMessageCommandEnvelope cmd, CancellationToken ct = default)
    {
        var (state, version) = await _store.LoadAsync<ModelState, ModelEvent>(cmd.StreamId, ct);

        var updateCmd = new UpdateMessageCommand(
            cmd.MessageId,
            Name: cmd.Request.Name, SetName: cmd.Request.Name is not null,
            Description: cmd.Request.Description, SetDescription: cmd.Request.SetDescription,
            ProducerId: cmd.Request.ProducerId, SetProducerId: cmd.Request.SetProducerId,
            ConsumerIds: cmd.Request.ConsumerIds, SetConsumerIds: cmd.Request.SetConsumerIds,
            SchemaId: cmd.Request.SchemaId, SetSchemaId: cmd.Request.SetSchemaId,
            Tags: cmd.Request.Tags);

        var result = ModelDecider.UpdateMessage(state, updateCmd);
        if (result is not CommandResult<IReadOnlyList<ModelEvent>>.Success success)
        {
            return result switch
            {
                CommandResult<IReadOnlyList<ModelEvent>>.Invalid inv =>
                    new CommandResult<MessageDto>.Invalid(inv.Errors),
                CommandResult<IReadOnlyList<ModelEvent>>.NotFound n =>
                    new CommandResult<MessageDto>.NotFound(n.Message),
                _ => new CommandResult<MessageDto>.Conflict("Unexpected error")
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

        var msg = state.Messages[cmd.MessageId];
        return new CommandResult<MessageDto>.Success(new MessageDto(
            msg.Id, msg.Name, msg.Description,
            msg.ProducerId, msg.ConsumerIds, msg.SchemaId, msg.Tags));
    }
}
