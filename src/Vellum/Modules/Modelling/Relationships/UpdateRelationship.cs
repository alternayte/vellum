using Vellum.Kernel.Aggregates;
using Vellum.Kernel.CommandHandling;
using Vellum.Kernel.Results;
using Vellum.Modules.Modelling.Model;

namespace Vellum.Modules.Modelling.Relationships;

public sealed record UpdateRelationshipRequest(
    string? Label, string? Technology, string? LineShape,
    bool SetLabel = false, bool SetTechnology = false, bool SetLineShape = false);

public sealed record UpdateRelationshipCommandEnvelope(
    Guid ProjectId, Guid StreamId, Guid RelationshipId, string UserId, UpdateRelationshipRequest Request);

public sealed class UpdateRelationshipHandler : ICommandHandler<UpdateRelationshipCommandEnvelope, CommandResult<RelationshipDto>>
{
    private readonly AggregateStore _store;
    private readonly ModelProjection _projection;

    public UpdateRelationshipHandler(AggregateStore store, ModelProjection projection)
    {
        _store = store;
        _projection = projection;
    }

    public async Task<CommandResult<RelationshipDto>> HandleAsync(UpdateRelationshipCommandEnvelope cmd, CancellationToken ct = default)
    {
        var (state, version) = await _store.LoadAsync<ModelState, ModelEvent>(cmd.StreamId, ct);

        var updateCmd = new UpdateRelationshipCommand(
            cmd.RelationshipId,
            Label: cmd.Request.Label, SetLabel: cmd.Request.SetLabel || cmd.Request.Label is not null,
            Technology: cmd.Request.Technology, SetTechnology: cmd.Request.SetTechnology || cmd.Request.Technology is not null,
            LineShape: cmd.Request.LineShape, SetLineShape: cmd.Request.SetLineShape || cmd.Request.LineShape is not null);

        var result = ModelDecider.UpdateRelationship(state, updateCmd);
        if (result is not CommandResult<IReadOnlyList<ModelEvent>>.Success success)
        {
            return result switch
            {
                CommandResult<IReadOnlyList<ModelEvent>>.NotFound n => new CommandResult<RelationshipDto>.NotFound(n.Message),
                _ => new CommandResult<RelationshipDto>.Conflict("Unexpected error")
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

        var rel = state.Relationships[cmd.RelationshipId];
        return new CommandResult<RelationshipDto>.Success(new RelationshipDto(
            rel.Id, rel.FromId, rel.ToId, rel.Label, rel.Technology, rel.MessageId, rel.LineShape));
    }
}
