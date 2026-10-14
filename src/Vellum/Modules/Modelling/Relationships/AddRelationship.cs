using Deedbox;
using Vellum.Modules.Modelling.Model;
using Vellum.Shared;

namespace Vellum.Modules.Modelling.Relationships;

public sealed record AddRelationshipRequest(
    Guid Id, Guid FromId, Guid ToId, string? Label, string? Technology,
    string? Kind = null);

public static class AddRelationship
{
    public static async Task<CommandResult<RelationshipDto>> HandleAsync(
        IEventStore store, Guid projectId, Guid streamId, AddRelationshipRequest request, CancellationToken ct)
    {
        var kind = RelationshipKind.Structural;
        if (request.Kind is not null && !ModelEnum.TryParseDefined(request.Kind, out kind))
            return new CommandResult<RelationshipDto>.Invalid([new ValidationError("kind", "Invalid relationship kind")]);

        var addCmd = new AddRelationshipCommand(
            request.Id, request.FromId, request.ToId,
            request.Label, request.Technology, kind);

        return await store.ForProject(projectId).Decide<ModelState, RelationshipDto>(
            streamId,
            state => ModelDecider.AddRelationship(state, addCmd),
            state => ToDto(state.Relationships[request.Id]),
            ct);
    }

    internal static RelationshipDto ToDto(RelationshipState relationship) => new(
        relationship.Id, relationship.FromId, relationship.ToId, relationship.Label,
        relationship.Technology, relationship.MessageId, relationship.LineShape,
        relationship.Kind.ToString().ToLowerInvariant(),
        relationship.FactStatus.ToApiString(), ProvenanceMapper.ToDto(relationship.Provenance), StaleDto.From(relationship.Stale));
}
