using Vellum.Kernel.Aggregates;
using Vellum.Kernel.CommandHandling;
using Vellum.Kernel.Results;

namespace Vellum.Modules.Schemas;

public sealed record CreateSchemaRequest(
    Guid Id, string Name, string? Description, string Content);

public sealed record CreateSchemaCommandEnvelope(
    Guid ProjectId, string UserId, CreateSchemaRequest Request);

public sealed class CreateSchemaHandler : ICommandHandler<CreateSchemaCommandEnvelope, CommandResult<SchemaDto>>
{
    private readonly AggregateStore _store;
    private readonly SchemaProjection _projection;

    public CreateSchemaHandler(AggregateStore store, SchemaProjection projection)
    {
        _store = store;
        _projection = projection;
    }

    public async Task<CommandResult<SchemaDto>> HandleAsync(CreateSchemaCommandEnvelope cmd, CancellationToken ct = default)
    {
        var schemaId = cmd.Request.Id;
        var (state, version) = await _store.LoadAsync<SchemaState, SchemaEvent>(schemaId, ct);

        var createCmd = new CreateSchemaCommand(
            schemaId, cmd.Request.Name, cmd.Request.Description,
            cmd.Request.Content, cmd.ProjectId);

        var result = SchemaDecider.Create(state, createCmd);
        if (result is not CommandResult<IReadOnlyList<SchemaEvent>>.Success success)
        {
            return result switch
            {
                CommandResult<IReadOnlyList<SchemaEvent>>.Invalid inv =>
                    new CommandResult<SchemaDto>.Invalid(inv.Errors),
                CommandResult<IReadOnlyList<SchemaEvent>>.Conflict c =>
                    new CommandResult<SchemaDto>.Conflict(c.Message),
                _ => new CommandResult<SchemaDto>.Conflict("Unexpected error")
            };
        }

        var metadata = new EventMetadata
        {
            ActorId = Guid.Parse(cmd.UserId),
            CorrelationId = Guid.NewGuid()
        };

        var newState = success.Value.Aggregate(state, (s, e) => s.Evolve(e));
        await _store.SaveAsync(schemaId, "schema", version, newState, success.Value, metadata, ct);

        var schema = newState.Schema!;
        return new CommandResult<SchemaDto>.Success(new SchemaDto(
            schema.Id, schema.Name, schema.Description, schema.Content, schema.Version));
    }
}
