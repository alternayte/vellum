using Deedbox;
using Vellum.Modules.Modelling.Model;
using Vellum.Modules.Trace.Engine;
using Vellum.Shared;

namespace Vellum.Modules.Modelling.Contracts;

/// <summary>
/// A missing retry means one attempt. Without a dead-letter channel, a message that fails
/// every attempt is dropped.
/// </summary>
public sealed record AddSubscriptionRequest(
    Guid Id, Guid ChannelId, Guid ConsumerId,
    RetryDto? Retry, Guid? DeadLetterChannelId);

public sealed record AddSubscriptionCommandEnvelope(Guid ProjectId, Guid StreamId, AddSubscriptionRequest Request);

public sealed class AddSubscriptionHandler(IEventStore store)
    : ICommandHandler<AddSubscriptionCommandEnvelope, CommandResult<SubscriptionDto>>
{
    public Task<CommandResult<SubscriptionDto>> HandleAsync(AddSubscriptionCommandEnvelope cmd, CancellationToken ct = default)
    {
        var request = cmd.Request;
        var add = new AddSubscriptionCommand(
            request.Id, request.ChannelId, request.ConsumerId,
            request.Retry?.ToDomain() ?? RetryPolicy.None,
            request.DeadLetterChannelId is { } deadLetter ? new ChannelId(deadLetter) : null);

        return store.ForProject(cmd.ProjectId).Decide<ModelState, SubscriptionDto>(
            cmd.StreamId,
            state => ContractDecider.AddSubscription(state, add),
            state => SubscriptionDto.From(state.Subscriptions[request.Id]),
            ct);
    }
}
