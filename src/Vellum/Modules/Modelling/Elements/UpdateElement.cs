using Deedbox;
using Vellum.Modules.Modelling.Model;
using Vellum.Shared;

namespace Vellum.Modules.Modelling.Elements;

public sealed record UpdateElementRequest(
    string? Name, string? Description, string? Technology,
    Guid? OwnerId, Guid? ParentId, string? Status, string[]? Tags,
    string? Icon,
    bool SetDescription = false, bool SetTechnology = false,
    bool SetOwnerId = false, bool SetParentId = false, bool SetIcon = false,
    string? FactStatus = null, ProvenanceInput? Provenance = null, bool SetProvenance = false);

public static class UpdateElement
{
    public static async Task<CommandResult<ElementDto>> HandleAsync(
        IEventStore store, Guid projectId, Guid streamId, Guid elementId, string userId, UpdateElementRequest request, TimeProvider time, CancellationToken ct)
    {
        ElementStatus? status = null;
        if (request.Status is not null)
        {
            if (!ModelEnum.TryParseDefined(request.Status, out ElementStatus parsed))
                return new CommandResult<ElementDto>.Invalid([new ValidationError("status", "Invalid status")]);
            status = parsed;
        }

        FactStatus? requestedStatus = null;
        if (request.FactStatus is not null)
        {
            if (!FactStatusFormat.TryParseApiString(request.FactStatus, out var parsedStatus))
                return new CommandResult<ElementDto>.Invalid([new ValidationError("factStatus", "Invalid fact status")]);
            requestedStatus = parsedStatus;
        }

        var updateCmd = new UpdateElementCommand(
            elementId,
            Name: request.Name, SetName: request.Name is not null,
            Description: request.Description, SetDescription: request.SetDescription || request.Description is not null,
            Technology: request.Technology, SetTechnology: request.SetTechnology || request.Technology is not null,
            OwnerId: request.OwnerId, SetOwnerId: request.SetOwnerId,
            ParentId: request.ParentId, SetParentId: request.SetParentId,
            Status: status,
            Tags: request.Tags,
            Icon: request.Icon, SetIcon: request.SetIcon || request.Icon is not null);

        // The acceptance stamp is taken once, so the decision stays a pure function of the state.
        var suppliedProvenance = request.SetProvenance
            ? ProvenanceMapper.ToDomain(request.Provenance, Guid.Parse(userId), time.GetUtcNow())
            : null;

        return await store.ForProject(projectId).Decide<ModelState, ElementDto>(
            streamId,
            Decide,
            state => AddElement.ToDto(state.Elements[elementId]),
            ct);

        CommandResult<IReadOnlyList<object>> Decide(ModelState state)
        {
            var result = ModelDecider.UpdateElement(state, updateCmd);
            if (result is not CommandResult<IReadOnlyList<object>>.Success success)
                return result;
            if (requestedStatus is null && !request.SetProvenance)
                return result;

            var events = new List<object>(success.Value);
            var stateAfter = events.Aggregate(state, ModelState.Evolve);
            var current = stateAfter.Elements[elementId];

            var provenanceResult = ModelDecider.RecordElementProvenance(
                stateAfter, elementId,
                requestedStatus ?? current.FactStatus,
                request.SetProvenance ? suppliedProvenance : current.Provenance);
            if (provenanceResult is not CommandResult<IReadOnlyList<object>>.Success provenanceSuccess)
                return provenanceResult;

            events.AddRange(provenanceSuccess.Value);
            return new CommandResult<IReadOnlyList<object>>.Success(events);
        }
    }
}
