using Deedbox;
using Vellum.Modules.Modelling.Model;
using Vellum.Shared;

namespace Vellum.Modules.Modelling.Contracts;

/// <summary>
/// A missing member leaves the value as it is. To clear the routing key, send the set flag
/// with a null key.
/// </summary>
public sealed record UpdateBindingRequest(
    Guid? SourceChannelId, Guid? TargetChannelId,
    string? RoutingKey, bool SetRoutingKey = false,
    string? FactStatus = null, ProvenanceInput? Provenance = null, bool SetProvenance = false);

public sealed record UpdateBindingCommandEnvelope(
    Guid ProjectId, Guid StreamId, Guid BindingId, string UserId, UpdateBindingRequest Request);

public sealed class UpdateBindingHandler(IEventStore store)
    : ICommandHandler<UpdateBindingCommandEnvelope, CommandResult<BindingDto>>
{
    public async Task<CommandResult<BindingDto>> HandleAsync(UpdateBindingCommandEnvelope cmd, CancellationToken ct = default)
    {
        var request = cmd.Request;
        BindingId id = cmd.BindingId;

        if (!ContractRequests.TryReadFactStatus(request.FactStatus, out var requestedStatus, out var statusError))
            return new CommandResult<BindingDto>.Invalid([statusError!]);

        var update = new UpdateBindingCommand(
            id,
            request.SourceChannelId is { } sourceId ? new ChannelId(sourceId) : null,
            request.TargetChannelId is { } targetId ? new ChannelId(targetId) : null,
            request.RoutingKey,
            request.SetRoutingKey || request.RoutingKey is not null);

        // The acceptance stamp is taken once, so the decision stays a pure function of the state.
        var supplied = request.SetProvenance
            ? ProvenanceMapper.ToDomain(request.Provenance, Guid.Parse(cmd.UserId))
            : null;

        return await store.ForProject(cmd.ProjectId).Decide<ModelState, BindingDto>(
            cmd.StreamId,
            state => ContractDecider.UpdateBinding(state, update).Then(state, after =>
                requestedStatus is null && !request.SetProvenance
                    ? new CommandResult<IReadOnlyList<object>>.Success([])
                    : ContractDecider.RecordBindingProvenance(
                        after, id,
                        requestedStatus ?? after.Bindings[id].FactStatus,
                        request.SetProvenance ? supplied : after.Bindings[id].Provenance)),
            state => BindingDto.From(state.Bindings[id]),
            ct);
    }
}
