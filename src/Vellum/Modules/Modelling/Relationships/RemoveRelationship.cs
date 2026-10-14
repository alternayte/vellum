using Deedbox;
using Vellum.Modules.Modelling.Model;
using Vellum.Shared;

namespace Vellum.Modules.Modelling.Relationships;

public static class RemoveRelationship
{
    public static Task<CommandResult> HandleAsync(
        IEventStore store, Guid projectId, Guid streamId, Guid relationshipId, CancellationToken ct) =>
        store.ForProject(projectId).Decide<ModelState>(
            streamId,
            state => ModelDecider.RemoveRelationship(state, relationshipId),
            ct);
}
