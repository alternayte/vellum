using Deedbox;
using Vellum.Modules.Modelling.Model;
using Vellum.Shared;

namespace Vellum.Modules.Modelling.Contracts;

/// <summary>
/// A missing member leaves the value as it is. To clear the dead-letter channel, send the
/// set flag with a null ID.
/// </summary>
public sealed record UpdateSubscriptionRequest(
    Guid? ChannelId, Guid? ConsumerId, RetryDto? Retry,
    Guid? DeadLetterChannelId, bool SetDeadLetterChannelId = false,
    string? FactStatus = null, ProvenanceInput? Provenance = null, bool SetProvenance = false);

public sealed record UpdateSubscriptionCommandEnvelope(
    Guid ProjectId, Guid StreamId, Guid SubscriptionId, string UserId, UpdateSubscriptionRequest Request);

public sealed class UpdateSubscriptionHandler(IEventStore store)
    : ICommandHandler<UpdateSubscriptionCommandEnvelope, CommandResult<SubscriptionDto>>
{
    public async Task<CommandResult<SubscriptionDto>> HandleAsync(UpdateSubscriptionCommandEnvelope cmd, CancellationToken ct = default)
    {
        var request = cmd.Request;
        SubscriptionId id = cmd.SubscriptionId;

        if (!ContractRequests.TryReadFactStatus(request.FactStatus, out var requestedStatus, out var statusError))
            return new CommandResult<SubscriptionDto>.Invalid([statusError!]);

        var update = new UpdateSubscriptionCommand(
            id,
            request.ChannelId is { } channelId ? new ChannelId(channelId) : null,
            request.ConsumerId is { } consumerId ? new ElementId(consumerId) : null,
            request.Retry?.ToDomain(),
            request.DeadLetterChannelId is { } deadLetter ? new ChannelId(deadLetter) : null,
            request.SetDeadLetterChannelId || request.DeadLetterChannelId is not null);

        // The acceptance stamp is taken once, so the decision stays a pure function of the state.
        var supplied = request.SetProvenance
            ? ProvenanceMapper.ToDomain(request.Provenance, Guid.Parse(cmd.UserId))
            : null;

        return await store.ForProject(cmd.ProjectId).Decide<ModelState, SubscriptionDto>(
            cmd.StreamId,
            state => ContractDecider.UpdateSubscription(state, update).Then(state, after =>
                requestedStatus is null && !request.SetProvenance
                    ? new CommandResult<IReadOnlyList<object>>.Success([])
                    : ContractDecider.RecordSubscriptionProvenance(
                        after, id,
                        requestedStatus ?? after.Subscriptions[id].FactStatus,
                        request.SetProvenance ? supplied : after.Subscriptions[id].Provenance)),
            state => SubscriptionDto.From(state.Subscriptions[id]),
            ct);
    }
}
