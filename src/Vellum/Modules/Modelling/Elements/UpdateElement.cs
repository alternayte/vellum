using Vellum.Kernel.Aggregates;
using Vellum.Kernel.CommandHandling;
using Vellum.Kernel.Results;
using Vellum.Modules.Modelling.Model;

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
    private readonly AggregateStore _store;
    private readonly ModelProjection _projection;

    public UpdateElementHandler(AggregateStore store, ModelProjection projection)
    {
        _store = store;
        _projection = projection;
    }

    public async Task<CommandResult<ElementDto>> HandleAsync(UpdateElementCommandEnvelope cmd, CancellationToken ct = default)
    {
        var (state, version) = await _store.LoadAsync<ModelState, ModelEvent>(cmd.StreamId, ct);

        ElementStatus? status = null;
        if (cmd.Request.Status is not null)
        {
            if (!ModelEnum.TryParseDefined(cmd.Request.Status, out ElementStatus parsed))
                return new CommandResult<ElementDto>.Invalid([new ValidationError("status", "Invalid status")]);
            status = parsed;
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

        var result = ModelDecider.UpdateElement(state, updateCmd);
        if (result is not CommandResult<IReadOnlyList<ModelEvent>>.Success success)
        {
            return result switch
            {
                CommandResult<IReadOnlyList<ModelEvent>>.Invalid inv => new CommandResult<ElementDto>.Invalid(inv.Errors),
                CommandResult<IReadOnlyList<ModelEvent>>.NotFound n => new CommandResult<ElementDto>.NotFound(n.Message),
                _ => new CommandResult<ElementDto>.Conflict("Unexpected error")
            };
        }

        var events = new List<ModelEvent>(success.Value);
        var stateAfter = events.Aggregate(state, (s, e) => s.Evolve(e));

        FactStatus? requestedStatus = null;
        if (cmd.Request.FactStatus is not null)
        {
            if (!FactStatusFormat.TryParseApiString(cmd.Request.FactStatus, out var parsedStatus))
                return new CommandResult<ElementDto>.Invalid([new ValidationError("factStatus", "Invalid fact status")]);
            requestedStatus = parsedStatus;
        }

        if (requestedStatus is not null || cmd.Request.SetProvenance)
        {
            var current = stateAfter.Elements[cmd.ElementId];
            var factStatus = requestedStatus ?? current.FactStatus;
            var provenance = cmd.Request.SetProvenance
                ? ProvenanceMapper.ToDomain(cmd.Request.Provenance, Guid.Parse(cmd.UserId))
                : current.Provenance;

            var provenanceResult = ModelDecider.RecordElementProvenance(stateAfter, cmd.ElementId, factStatus, provenance);
            if (provenanceResult is not CommandResult<IReadOnlyList<ModelEvent>>.Success provenanceSuccess)
            {
                return provenanceResult switch
                {
                    CommandResult<IReadOnlyList<ModelEvent>>.Invalid inv => new CommandResult<ElementDto>.Invalid(inv.Errors),
                    CommandResult<IReadOnlyList<ModelEvent>>.NotFound n => new CommandResult<ElementDto>.NotFound(n.Message),
                    _ => new CommandResult<ElementDto>.Conflict("Unexpected error")
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

        return new CommandResult<ElementDto>.Success(AddElementHandler.ToDto(state.Elements[cmd.ElementId]));
    }
}
