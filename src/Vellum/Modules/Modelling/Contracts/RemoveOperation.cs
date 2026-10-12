using Deedbox;
using Vellum.Modules.Modelling.Model;
using Vellum.Shared;

namespace Vellum.Modules.Modelling.Contracts;

public sealed record RemoveOperationCommandEnvelope(Guid ProjectId, Guid StreamId, Guid OperationId);

public sealed class RemoveOperationHandler(IEventStore store)
    : ICommandHandler<RemoveOperationCommandEnvelope, CommandResult>
{
    public Task<CommandResult> HandleAsync(RemoveOperationCommandEnvelope cmd, CancellationToken ct = default) =>
        store.ForProject(cmd.ProjectId).Decide<ModelState>(
            cmd.StreamId,
            state => ContractDecider.RemoveOperation(state, cmd.OperationId),
            ct);
}
