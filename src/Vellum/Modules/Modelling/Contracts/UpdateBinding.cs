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

public static class UpdateBinding
{
    public static async Task<CommandResult<BindingDto>> HandleAsync(
        IEventStore store, Guid projectId, Guid streamId, Guid bindingId, string userId, UpdateBindingRequest request, TimeProvider time, CancellationToken ct)
    {
        BindingId id = bindingId;

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
            ? ProvenanceMapper.ToDomain(request.Provenance, Guid.Parse(userId), time.GetUtcNow())
            : null;

        return await store.ForProject(projectId).Decide<ModelState, BindingDto>(
            streamId,
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
