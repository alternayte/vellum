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

public static class AddSubscription
{
    public static Task<CommandResult<SubscriptionDto>> HandleAsync(
        IEventStore store, Guid projectId, Guid streamId, AddSubscriptionRequest request, CancellationToken ct)
    {
        var add = new AddSubscriptionCommand(
            request.Id, request.ChannelId, request.ConsumerId,
            request.Retry?.ToDomain() ?? RetryPolicy.None,
            request.DeadLetterChannelId is { } deadLetter ? new ChannelId(deadLetter) : null);

        return store.ForProject(projectId).Decide<ModelState, SubscriptionDto>(
            streamId,
            state => ContractDecider.AddSubscription(state, add),
            state => SubscriptionDto.From(state.Subscriptions[request.Id]),
            ct);
    }
}
