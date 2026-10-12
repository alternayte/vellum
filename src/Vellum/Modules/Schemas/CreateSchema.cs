using Deedbox;
using Vellum.Shared;

namespace Vellum.Modules.Schemas;

public sealed record CreateSchemaRequest(
    Guid Id, string Name, string? Description, string Content);

public sealed record CreateSchemaCommandEnvelope(Guid ProjectId, CreateSchemaRequest Request);

public sealed class CreateSchemaHandler : ICommandHandler<CreateSchemaCommandEnvelope, CommandResult<SchemaDto>>
{
    private readonly IEventStore _store;

    public CreateSchemaHandler(IEventStore store) => _store = store;

    public Task<CommandResult<SchemaDto>> HandleAsync(CreateSchemaCommandEnvelope cmd, CancellationToken ct = default)
    {
        var createCmd = new CreateSchemaCommand(
            cmd.Request.Id, cmd.Request.Name, cmd.Request.Description,
            cmd.Request.Content, cmd.ProjectId);

        return _store.Decide<SchemaState, SchemaDto>(
            cmd.Request.Id,
            state => SchemaDecider.Create(state, createCmd),
            state => ToDto(state.Schema!),
            ct);
    }

    internal static SchemaDto ToDto(SchemaStateEntry schema) => new(
        schema.Id, schema.Name, schema.Description, schema.Content, schema.Version);
}
