using Deedbox;
using Vellum.Modules.Modelling.Model;
using Vellum.Shared;

namespace Vellum.Modules.Modelling.Relationships;

public sealed record RemoveRelationshipCommandEnvelope(Guid ProjectId, Guid StreamId, Guid RelationshipId);

public sealed class RemoveRelationshipHandler : ICommandHandler<RemoveRelationshipCommandEnvelope, CommandResult>
{
    private readonly IEventStore _store;

    public RemoveRelationshipHandler(IEventStore store) => _store = store;

    public Task<CommandResult> HandleAsync(RemoveRelationshipCommandEnvelope cmd, CancellationToken ct = default) =>
        _store.ForProject(cmd.ProjectId).Decide<ModelState>(
            cmd.StreamId,
            state => ModelDecider.RemoveRelationship(state, cmd.RelationshipId),
            ct);
}
