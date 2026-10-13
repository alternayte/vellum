using Deedbox;
using Vellum.Modules.Modelling.Model;
using Vellum.Shared;

namespace Vellum.Modules.Modelling.Relationships;

public sealed record AddRelationshipRequest(
    Guid Id, Guid FromId, Guid ToId, string? Label, string? Technology,
    string? Kind = null);

public sealed record AddRelationshipCommandEnvelope(Guid ProjectId, Guid StreamId, AddRelationshipRequest Request);

public sealed class AddRelationshipHandler : ICommandHandler<AddRelationshipCommandEnvelope, CommandResult<RelationshipDto>>
{
    private readonly IEventStore _store;

    public AddRelationshipHandler(IEventStore store) => _store = store;

    public async Task<CommandResult<RelationshipDto>> HandleAsync(AddRelationshipCommandEnvelope cmd, CancellationToken ct = default)
    {
        var kind = RelationshipKind.Structural;
        if (cmd.Request.Kind is not null && !ModelEnum.TryParseDefined(cmd.Request.Kind, out kind))
            return new CommandResult<RelationshipDto>.Invalid([new ValidationError("kind", "Invalid relationship kind")]);

        var addCmd = new AddRelationshipCommand(
            cmd.Request.Id, cmd.Request.FromId, cmd.Request.ToId,
            cmd.Request.Label, cmd.Request.Technology, kind);

        return await _store.ForProject(cmd.ProjectId).Decide<ModelState, RelationshipDto>(
            cmd.StreamId,
            state => ModelDecider.AddRelationship(state, addCmd),
            state => ToDto(state.Relationships[cmd.Request.Id]),
            ct);
    }

    internal static RelationshipDto ToDto(RelationshipState relationship) => new(
        relationship.Id, relationship.FromId, relationship.ToId, relationship.Label,
        relationship.Technology, relationship.MessageId, relationship.LineShape,
        relationship.Kind.ToString().ToLowerInvariant(),
        relationship.FactStatus.ToApiString(), ProvenanceMapper.ToDto(relationship.Provenance), StaleDto.From(relationship.Stale));
}
