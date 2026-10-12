using Deedbox;
using Vellum.Modules.Modelling.Model;
using Vellum.Shared;

namespace Vellum.Modules.Modelling.Elements;

public sealed record RemoveElementCommandEnvelope(Guid ProjectId, Guid StreamId, Guid ElementId);

public sealed class RemoveElementHandler : ICommandHandler<RemoveElementCommandEnvelope, CommandResult>
{
    private readonly IEventStore _store;

    public RemoveElementHandler(IEventStore store) => _store = store;

    public Task<CommandResult> HandleAsync(RemoveElementCommandEnvelope cmd, CancellationToken ct = default) =>
        _store.ForProject(cmd.ProjectId).Decide<ModelState>(
            cmd.StreamId,
            state => ModelDecider.RemoveElement(state, cmd.ElementId),
            ct);
}
