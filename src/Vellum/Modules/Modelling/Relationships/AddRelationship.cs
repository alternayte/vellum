using Vellum.Kernel.Aggregates;
using Vellum.Kernel.CommandHandling;
using Vellum.Kernel.Results;
using Vellum.Modules.Modelling.Model;

namespace Vellum.Modules.Modelling.Relationships;

public sealed record AddRelationshipRequest(Guid Id, Guid FromId, Guid ToId, string? Label, string? Technology);

public sealed record AddRelationshipCommandEnvelope(
    Guid ProjectId, Guid StreamId, string UserId, AddRelationshipRequest Request);

public sealed class AddRelationshipHandler : ICommandHandler<AddRelationshipCommandEnvelope, CommandResult<RelationshipDto>>
{
    private readonly AggregateStore _store;
    private readonly ModelProjection _projection;

    public AddRelationshipHandler(AggregateStore store, ModelProjection projection)
    {
        _store = store;
        _projection = projection;
    }

    public async Task<CommandResult<RelationshipDto>> HandleAsync(AddRelationshipCommandEnvelope cmd, CancellationToken ct = default)
    {
        var (state, version) = await _store.LoadAsync<ModelState, ModelEvent>(cmd.StreamId, ct);

        var addCmd = new AddRelationshipCommand(cmd.Request.Id, cmd.Request.FromId, cmd.Request.ToId, cmd.Request.Label, cmd.Request.Technology);
        var result = ModelDecider.AddRelationship(state, addCmd);

        if (result is not CommandResult<IReadOnlyList<ModelEvent>>.Success success)
        {
            return result switch
            {
                CommandResult<IReadOnlyList<ModelEvent>>.Invalid inv => new CommandResult<RelationshipDto>.Invalid(inv.Errors),
                CommandResult<IReadOnlyList<ModelEvent>>.Conflict c => new CommandResult<RelationshipDto>.Conflict(c.Message),
                _ => new CommandResult<RelationshipDto>.Conflict("Unexpected error")
            };
        }

        var metadata = new EventMetadata { ActorId = Guid.Parse(cmd.UserId), CorrelationId = Guid.NewGuid() };
        _projection.SetContext(cmd.ProjectId, cmd.StreamId);
        var newState = success.Value.Aggregate(state, (s, e) => s.Evolve(e));
        await _store.SaveAsync(cmd.StreamId, "model", version, newState, success.Value, metadata, ct);

        var rel = newState.Relationships[cmd.Request.Id];
        return new CommandResult<RelationshipDto>.Success(new RelationshipDto(
            rel.Id, rel.FromId, rel.ToId, rel.Label, rel.Technology, rel.MessageId, rel.LineShape));
    }
}
