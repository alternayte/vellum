using Vellum.Kernel.Aggregates;
using Vellum.Kernel.CommandHandling;
using Vellum.Kernel.Results;
using Vellum.Modules.Modelling.Model;

namespace Vellum.Modules.Modelling.Relationships;

public sealed record UpdateRelationshipRequest(
    string? Label, string? Technology, string? LineShape,
    bool SetLabel = false, bool SetTechnology = false, bool SetLineShape = false,
    string? Kind = null, bool SetKind = false,
    string? FactStatus = null, ProvenanceInput? Provenance = null, bool SetProvenance = false);

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

        RelationshipKind? kind = null;
        if (cmd.Request.Kind is not null)
        {
            if (!ModelEnum.TryParseDefined(cmd.Request.Kind, out RelationshipKind parsed))
                return new CommandResult<RelationshipDto>.Invalid([new ValidationError("kind", "Invalid relationship kind")]);
            kind = parsed;
        }

        var updateCmd = new UpdateRelationshipCommand(
            cmd.RelationshipId,
            Label: cmd.Request.Label, SetLabel: cmd.Request.SetLabel || cmd.Request.Label is not null,
            Technology: cmd.Request.Technology, SetTechnology: cmd.Request.SetTechnology || cmd.Request.Technology is not null,
            LineShape: cmd.Request.LineShape, SetLineShape: cmd.Request.SetLineShape || cmd.Request.LineShape is not null,
            Kind: kind, SetKind: cmd.Request.SetKind || cmd.Request.Kind is not null);

        var result = ModelDecider.UpdateRelationship(state, updateCmd);
        if (result is not CommandResult<IReadOnlyList<ModelEvent>>.Success success)
        {
            return result switch
            {
                CommandResult<IReadOnlyList<ModelEvent>>.Invalid inv => new CommandResult<RelationshipDto>.Invalid(inv.Errors),
                CommandResult<IReadOnlyList<ModelEvent>>.NotFound n => new CommandResult<RelationshipDto>.NotFound(n.Message),
                _ => new CommandResult<RelationshipDto>.Conflict("Unexpected error")
            };
        }

        var events = new List<ModelEvent>(success.Value);
        var stateAfter = events.Aggregate(state, (s, e) => s.Evolve(e));

        FactStatus? requestedStatus = null;
        if (cmd.Request.FactStatus is not null)
        {
            if (!FactStatusFormat.TryParseApiString(cmd.Request.FactStatus, out var parsedStatus))
                return new CommandResult<RelationshipDto>.Invalid([new ValidationError("factStatus", "Invalid fact status")]);
            requestedStatus = parsedStatus;
        }

        if (requestedStatus is not null || cmd.Request.SetProvenance)
        {
            var current = stateAfter.Relationships[cmd.RelationshipId];
            var factStatus = requestedStatus ?? current.FactStatus;
            var provenance = cmd.Request.SetProvenance
                ? ProvenanceMapper.ToDomain(cmd.Request.Provenance, Guid.Parse(cmd.UserId))
                : current.Provenance;

            var provenanceResult = ModelDecider.RecordRelationshipProvenance(stateAfter, cmd.RelationshipId, factStatus, provenance);
            if (provenanceResult is not CommandResult<IReadOnlyList<ModelEvent>>.Success provenanceSuccess)
            {
                return provenanceResult switch
                {
                    CommandResult<IReadOnlyList<ModelEvent>>.Invalid inv => new CommandResult<RelationshipDto>.Invalid(inv.Errors),
                    CommandResult<IReadOnlyList<ModelEvent>>.NotFound n => new CommandResult<RelationshipDto>.NotFound(n.Message),
                    _ => new CommandResult<RelationshipDto>.Conflict("Unexpected error")
                };
            }

            events.AddRange(provenanceSuccess.Value);
            stateAfter = provenanceSuccess.Value.Aggregate(stateAfter, (s, e) => s.Evolve(e));
        }

        if (events.Count > 0)
        {
            var metadata = new EventMetadata { ActorId = Guid.Parse(cmd.UserId), CorrelationId = Guid.NewGuid() };
            _projection.SetContext(cmd.ProjectId, cmd.StreamId);
            await _store.SaveAsync(cmd.StreamId, "model", version, stateAfter, events, metadata, ct);
            state = stateAfter;
        }

        return new CommandResult<RelationshipDto>.Success(AddRelationshipHandler.ToDto(state.Relationships[cmd.RelationshipId]));
    }
}
