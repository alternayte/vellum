using Deedbox;
using Vellum.Modules.Modelling.Model;
using Vellum.Shared;

namespace Vellum.Modules.Modelling.Elements;

public static class RemoveElement
{
    public static Task<CommandResult> HandleAsync(
        IEventStore store, Guid projectId, Guid streamId, Guid elementId, CancellationToken ct) =>
        store.ForProject(projectId).Decide<ModelState>(
            streamId,
            state => ModelDecider.RemoveElement(state, elementId),
            ct);
}
