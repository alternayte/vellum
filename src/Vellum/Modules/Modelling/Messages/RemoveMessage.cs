using Deedbox;
using Vellum.Modules.Modelling.Model;
using Vellum.Shared;

namespace Vellum.Modules.Modelling.Messages;

public sealed record RemoveMessageCommandEnvelope(Guid ProjectId, Guid StreamId, Guid MessageId);

public sealed class RemoveMessageHandler : ICommandHandler<RemoveMessageCommandEnvelope, CommandResult>
{
    private readonly IEventStore _store;

    public RemoveMessageHandler(IEventStore store) => _store = store;

    public Task<CommandResult> HandleAsync(RemoveMessageCommandEnvelope cmd, CancellationToken ct = default) =>
        _store.ForProject(cmd.ProjectId).Decide<ModelState>(
            cmd.StreamId,
            state => ModelDecider.RemoveMessage(state, cmd.MessageId),
            ct);
}
