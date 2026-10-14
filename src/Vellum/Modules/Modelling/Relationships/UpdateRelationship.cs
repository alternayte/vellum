using Deedbox;
using Vellum.Modules.Modelling.Model;
using Vellum.Shared;

namespace Vellum.Modules.Modelling.Relationships;

public sealed record UpdateRelationshipRequest(
    string? Label, string? Technology, string? LineShape,
    bool SetLabel = false, bool SetTechnology = false, bool SetLineShape = false,
    string? Kind = null, bool SetKind = false,
    string? FactStatus = null, ProvenanceInput? Provenance = null, bool SetProvenance = false);

public static class UpdateRelationship
{
    public static async Task<CommandResult<RelationshipDto>> HandleAsync(
        IEventStore store, Guid projectId, Guid streamId, Guid relationshipId, string userId, UpdateRelationshipRequest request, TimeProvider time, CancellationToken ct)
    {
        RelationshipKind? kind = null;
        if (request.Kind is not null)
        {
            if (!ModelEnum.TryParseDefined(request.Kind, out RelationshipKind parsed))
                return new CommandResult<RelationshipDto>.Invalid([new ValidationError("kind", "Invalid relationship kind")]);
            kind = parsed;
        }

        FactStatus? requestedStatus = null;
        if (request.FactStatus is not null)
        {
            if (!FactStatusFormat.TryParseApiString(request.FactStatus, out var parsedStatus))
                return new CommandResult<RelationshipDto>.Invalid([new ValidationError("factStatus", "Invalid fact status")]);
            requestedStatus = parsedStatus;
        }

        var updateCmd = new UpdateRelationshipCommand(
            relationshipId,
            Label: request.Label, SetLabel: request.SetLabel || request.Label is not null,
            Technology: request.Technology, SetTechnology: request.SetTechnology || request.Technology is not null,
            LineShape: request.LineShape, SetLineShape: request.SetLineShape || request.LineShape is not null,
            Kind: kind, SetKind: request.SetKind || request.Kind is not null);

        // The acceptance stamp is taken once, so the decision stays a pure function of the state.
        var suppliedProvenance = request.SetProvenance
            ? ProvenanceMapper.ToDomain(request.Provenance, Guid.Parse(userId), time.GetUtcNow())
            : null;

        return await store.ForProject(projectId).Decide<ModelState, RelationshipDto>(
            streamId,
            Decide,
            state => AddRelationship.ToDto(state.Relationships[relationshipId]),
            ct);

        CommandResult<IReadOnlyList<object>> Decide(ModelState state)
        {
            var result = ModelDecider.UpdateRelationship(state, updateCmd);
            if (result is not CommandResult<IReadOnlyList<object>>.Success success)
                return result;
            if (requestedStatus is null && !request.SetProvenance)
                return result;

            var events = new List<object>(success.Value);
            var stateAfter = events.Aggregate(state, ModelState.Evolve);
            var current = stateAfter.Relationships[relationshipId];

            var provenanceResult = ModelDecider.RecordRelationshipProvenance(
                stateAfter, relationshipId,
                requestedStatus ?? current.FactStatus,
                request.SetProvenance ? suppliedProvenance : current.Provenance);
            if (provenanceResult is not CommandResult<IReadOnlyList<object>>.Success provenanceSuccess)
                return provenanceResult;

            events.AddRange(provenanceSuccess.Value);
            return new CommandResult<IReadOnlyList<object>>.Success(events);
        }
    }
}
