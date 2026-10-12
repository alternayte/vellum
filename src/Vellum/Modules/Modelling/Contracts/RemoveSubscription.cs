using Deedbox;
using Vellum.Modules.Modelling.Model;
using Vellum.Shared;

namespace Vellum.Modules.Modelling.Contracts;

public sealed record RemoveSubscriptionCommandEnvelope(Guid ProjectId, Guid StreamId, Guid SubscriptionId);

public sealed class RemoveSubscriptionHandler(IEventStore store)
    : ICommandHandler<RemoveSubscriptionCommandEnvelope, CommandResult>
{
    public Task<CommandResult> HandleAsync(RemoveSubscriptionCommandEnvelope cmd, CancellationToken ct = default) =>
        store.ForProject(cmd.ProjectId).Decide<ModelState>(
            cmd.StreamId,
            state => ContractDecider.RemoveSubscription(state, cmd.SubscriptionId),
            ct);
}
