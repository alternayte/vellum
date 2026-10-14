using Deedbox;
using Vellum.Shared;

namespace Vellum.Modules.Schemas;

public sealed record CreateSchemaRequest(
    Guid Id, string Name, string? Description, string Content);

public static class CreateSchema
{
    public static Task<CommandResult<SchemaDto>> HandleAsync(
        IEventStore store, Guid projectId, CreateSchemaRequest request, CancellationToken ct)
    {
        var createCmd = new CreateSchemaCommand(
            request.Id, request.Name, request.Description,
            request.Content, projectId);

        return store.Decide<SchemaState, SchemaDto>(
            request.Id,
            state => SchemaDecider.Create(state, createCmd),
            state => ToDto(state.Schema!),
            ct);
    }

    internal static SchemaDto ToDto(SchemaStateEntry schema) => new(
        schema.Id, schema.Name, schema.Description, schema.Content, schema.Version);
}
