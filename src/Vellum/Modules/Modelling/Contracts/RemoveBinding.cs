using Deedbox;
using Vellum.Modules.Modelling.Model;
using Vellum.Shared;

namespace Vellum.Modules.Modelling.Contracts;

public sealed record RemoveBindingCommandEnvelope(Guid ProjectId, Guid StreamId, Guid BindingId);

public sealed class RemoveBindingHandler(IEventStore store)
    : ICommandHandler<RemoveBindingCommandEnvelope, CommandResult>
{
    public Task<CommandResult> HandleAsync(RemoveBindingCommandEnvelope cmd, CancellationToken ct = default) =>
        store.ForProject(cmd.ProjectId).Decide<ModelState>(
            cmd.StreamId,
            state => ContractDecider.RemoveBinding(state, cmd.BindingId),
            ct);
}
