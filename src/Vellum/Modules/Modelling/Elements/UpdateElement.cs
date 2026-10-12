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

public sealed record UpdateElementCommandEnvelope(
    Guid ProjectId, Guid StreamId, Guid ElementId, string UserId, UpdateElementRequest Request);

public sealed class UpdateElementHandler : ICommandHandler<UpdateElementCommandEnvelope, CommandResult<ElementDto>>
{
    private readonly IEventStore _store;

    public UpdateElementHandler(IEventStore store) => _store = store;

    public async Task<CommandResult<ElementDto>> HandleAsync(UpdateElementCommandEnvelope cmd, CancellationToken ct = default)
    {
        ElementStatus? status = null;
        if (cmd.Request.Status is not null)
        {
            if (!ModelEnum.TryParseDefined(cmd.Request.Status, out ElementStatus parsed))
                return new CommandResult<ElementDto>.Invalid([new ValidationError("status", "Invalid status")]);
            status = parsed;
        }

        FactStatus? requestedStatus = null;
        if (cmd.Request.FactStatus is not null)
        {
            if (!FactStatusFormat.TryParseApiString(cmd.Request.FactStatus, out var parsedStatus))
                return new CommandResult<ElementDto>.Invalid([new ValidationError("factStatus", "Invalid fact status")]);
            requestedStatus = parsedStatus;
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

        // The acceptance stamp is taken once, so the decision stays a pure function of the state.
        var suppliedProvenance = cmd.Request.SetProvenance
            ? ProvenanceMapper.ToDomain(cmd.Request.Provenance, Guid.Parse(cmd.UserId))
            : null;

        return await _store.ForProject(cmd.ProjectId).Decide<ModelState, ElementDto>(
            cmd.StreamId,
            Decide,
            state => AddElementHandler.ToDto(state.Elements[cmd.ElementId]),
            ct);

        CommandResult<IReadOnlyList<object>> Decide(ModelState state)
        {
            var result = ModelDecider.UpdateElement(state, updateCmd);
            if (result is not CommandResult<IReadOnlyList<object>>.Success success)
                return result;
            if (requestedStatus is null && !cmd.Request.SetProvenance)
                return result;

            var events = new List<object>(success.Value);
            var stateAfter = events.Aggregate(state, ModelState.Evolve);
            var current = stateAfter.Elements[cmd.ElementId];

            var provenanceResult = ModelDecider.RecordElementProvenance(
                stateAfter, cmd.ElementId,
                requestedStatus ?? current.FactStatus,
                cmd.Request.SetProvenance ? suppliedProvenance : current.Provenance);
            if (provenanceResult is not CommandResult<IReadOnlyList<object>>.Success provenanceSuccess)
                return provenanceResult;

            events.AddRange(provenanceSuccess.Value);
            return new CommandResult<IReadOnlyList<object>>.Success(events);
        }
    }
}
