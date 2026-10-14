using Deedbox;
using Vellum.Modules.Modelling.Model;
using Vellum.Shared;

namespace Vellum.Modules.Modelling.Contracts;

public static class RemoveChannel
{
    public static Task<CommandResult> HandleAsync(
        IEventStore store, Guid projectId, Guid streamId, Guid channelId, CancellationToken ct) =>
        store.ForProject(projectId).Decide<ModelState>(
            streamId,
            state => ContractDecider.RemoveChannel(state, channelId),
            ct);
}
