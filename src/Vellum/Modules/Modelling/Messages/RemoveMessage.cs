using Vellum.Kernel.Aggregates;
using Vellum.Kernel.CommandHandling;
using Vellum.Kernel.Results;
using Vellum.Modules.Modelling.Model;

namespace Vellum.Modules.Modelling.Messages;

public sealed record RemoveMessageCommandEnvelope(Guid ProjectId, Guid StreamId, Guid MessageId, string UserId);

public sealed class RemoveMessageHandler : ICommandHandler<RemoveMessageCommandEnvelope, CommandResult>
{
    private readonly AggregateStore _store;
    private readonly ModelProjection _projection;

    public RemoveMessageHandler(AggregateStore store, ModelProjection projection)
    {
        _store = store;
        _projection = projection;
    }

    public async Task<CommandResult> HandleAsync(RemoveMessageCommandEnvelope cmd, CancellationToken ct = default)
    {
        var (state, version) = await _store.LoadAsync<ModelState, ModelEvent>(cmd.StreamId, ct);
        var result = ModelDecider.RemoveMessage(state, cmd.MessageId);

        if (result is not CommandResult<IReadOnlyList<ModelEvent>>.Success success)
        {
            return result switch
            {
                CommandResult<IReadOnlyList<ModelEvent>>.NotFound n => new CommandResult.NotFound(n.Message),
                _ => new CommandResult.Conflict("Unexpected error")
            };
        }

        var metadata = new EventMetadata { ActorId = Guid.Parse(cmd.UserId), CorrelationId = Guid.NewGuid() };
        _projection.SetContext(cmd.ProjectId, cmd.StreamId);
        var newState = success.Value.Aggregate(state, (s, e) => s.Evolve(e));
        await _store.SaveAsync(cmd.StreamId, "model", version, newState, success.Value, metadata, ct);

        return new CommandResult.Success();
    }
}
