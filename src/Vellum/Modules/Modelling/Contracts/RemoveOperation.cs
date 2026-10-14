using Deedbox;
using Vellum.Modules.Modelling.Model;
using Vellum.Shared;

namespace Vellum.Modules.Modelling.Contracts;

public static class RemoveOperation
{
    public static Task<CommandResult> HandleAsync(
        IEventStore store, Guid projectId, Guid streamId, Guid operationId, CancellationToken ct) =>
        store.ForProject(projectId).Decide<ModelState>(
            streamId,
            state => ContractDecider.RemoveOperation(state, operationId),
            ct);
}
