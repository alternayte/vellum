using Deedbox;
using Vellum.Modules.Modelling.Model;
using Vellum.Shared;

namespace Vellum.Modules.Modelling.Contracts;

public sealed record RemoveChannelCommandEnvelope(Guid ProjectId, Guid StreamId, Guid ChannelId);

public sealed class RemoveChannelHandler(IEventStore store)
    : ICommandHandler<RemoveChannelCommandEnvelope, CommandResult>
{
    public Task<CommandResult> HandleAsync(RemoveChannelCommandEnvelope cmd, CancellationToken ct = default) =>
        store.ForProject(cmd.ProjectId).Decide<ModelState>(
            cmd.StreamId,
            state => ContractDecider.RemoveChannel(state, cmd.ChannelId),
            ct);
}
