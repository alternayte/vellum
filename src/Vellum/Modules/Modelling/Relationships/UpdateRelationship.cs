using Deedbox;
using Vellum.Modules.Modelling.Model;
using Vellum.Shared;

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
    private readonly IEventStore _store;

    public UpdateRelationshipHandler(IEventStore store) => _store = store;

    public async Task<CommandResult<RelationshipDto>> HandleAsync(UpdateRelationshipCommandEnvelope cmd, CancellationToken ct = default)
    {
        RelationshipKind? kind = null;
        if (cmd.Request.Kind is not null)
        {
            if (!ModelEnum.TryParseDefined(cmd.Request.Kind, out RelationshipKind parsed))
                return new CommandResult<RelationshipDto>.Invalid([new ValidationError("kind", "Invalid relationship kind")]);
            kind = parsed;
        }

        FactStatus? requestedStatus = null;
        if (cmd.Request.FactStatus is not null)
        {
            if (!FactStatusFormat.TryParseApiString(cmd.Request.FactStatus, out var parsedStatus))
                return new CommandResult<RelationshipDto>.Invalid([new ValidationError("factStatus", "Invalid fact status")]);
            requestedStatus = parsedStatus;
        }

        var updateCmd = new UpdateRelationshipCommand(
            cmd.RelationshipId,
            Label: cmd.Request.Label, SetLabel: cmd.Request.SetLabel || cmd.Request.Label is not null,
            Technology: cmd.Request.Technology, SetTechnology: cmd.Request.SetTechnology || cmd.Request.Technology is not null,
            LineShape: cmd.Request.LineShape, SetLineShape: cmd.Request.SetLineShape || cmd.Request.LineShape is not null,
            Kind: kind, SetKind: cmd.Request.SetKind || cmd.Request.Kind is not null);

        // The acceptance stamp is taken once, so the decision stays a pure function of the state.
        var suppliedProvenance = cmd.Request.SetProvenance
            ? ProvenanceMapper.ToDomain(cmd.Request.Provenance, Guid.Parse(cmd.UserId))
            : null;

        return await _store.ForProject(cmd.ProjectId).Decide<ModelState, RelationshipDto>(
            cmd.StreamId,
            Decide,
            state => AddRelationshipHandler.ToDto(state.Relationships[cmd.RelationshipId]),
            ct);

        CommandResult<IReadOnlyList<object>> Decide(ModelState state)
        {
            var result = ModelDecider.UpdateRelationship(state, updateCmd);
            if (result is not CommandResult<IReadOnlyList<object>>.Success success)
                return result;
            if (requestedStatus is null && !cmd.Request.SetProvenance)
                return result;

            var events = new List<object>(success.Value);
            var stateAfter = events.Aggregate(state, ModelState.Evolve);
            var current = stateAfter.Relationships[cmd.RelationshipId];

            var provenanceResult = ModelDecider.RecordRelationshipProvenance(
                stateAfter, cmd.RelationshipId,
                requestedStatus ?? current.FactStatus,
                cmd.Request.SetProvenance ? suppliedProvenance : current.Provenance);
            if (provenanceResult is not CommandResult<IReadOnlyList<object>>.Success provenanceSuccess)
                return provenanceResult;

            events.AddRange(provenanceSuccess.Value);
            return new CommandResult<IReadOnlyList<object>>.Success(events);
        }
    }
}
