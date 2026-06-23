using Microsoft.EntityFrameworkCore;
using Vellum.Kernel.Aggregates;
using Vellum.Kernel.CommandHandling;
using Vellum.Kernel.Results;

namespace Vellum.Modules.Schemas;

public sealed record UpdateSchemaRequest(
    string? Name, string? Description, string? Content,
    bool SetDescription = false, bool SetContent = false);

public sealed record UpdateSchemaCommandEnvelope(
    Guid ProjectId, Guid SchemaId, string UserId, UpdateSchemaRequest Request);

public sealed class UpdateSchemaHandler : ICommandHandler<UpdateSchemaCommandEnvelope, CommandResult<SchemaDto>>
{
    private readonly AggregateStore _store;
    private readonly SchemaProjection _projection;
    private readonly SchemasDbContext _db;

    public UpdateSchemaHandler(AggregateStore store, SchemaProjection projection, SchemasDbContext db)
    {
        _store = store;
        _projection = projection;
        _db = db;
    }

    public async Task<CommandResult<SchemaDto>> HandleAsync(UpdateSchemaCommandEnvelope cmd, CancellationToken ct = default)
    {
        // Verify schema belongs to project
        var entity = await _db.Schemas.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == cmd.SchemaId && s.ProjectId == cmd.ProjectId, ct);
        if (entity is null)
            return new CommandResult<SchemaDto>.NotFound("Schema not found");

        var (state, version) = await _store.LoadAsync<SchemaState, SchemaEvent>(cmd.SchemaId, ct);

        var updateCmd = new UpdateSchemaCommand(
            cmd.SchemaId,
            Name: cmd.Request.Name, SetName: cmd.Request.Name is not null,
            Description: cmd.Request.Description, SetDescription: cmd.Request.SetDescription || cmd.Request.Description is not null,
            Content: cmd.Request.Content, SetContent: cmd.Request.SetContent || cmd.Request.Content is not null);

        var result = SchemaDecider.Update(state, updateCmd);
        if (result is not CommandResult<IReadOnlyList<SchemaEvent>>.Success success)
        {
            return result switch
            {
                CommandResult<IReadOnlyList<SchemaEvent>>.Invalid inv =>
                    new CommandResult<SchemaDto>.Invalid(inv.Errors),
                CommandResult<IReadOnlyList<SchemaEvent>>.NotFound n =>
                    new CommandResult<SchemaDto>.NotFound(n.Message),
                _ => new CommandResult<SchemaDto>.Conflict("Unexpected error")
            };
        }

        if (success.Value.Count > 0)
        {
            var metadata = new EventMetadata { ActorId = Guid.Parse(cmd.UserId), CorrelationId = Guid.NewGuid() };
            var newState = success.Value.Aggregate(state, (s, e) => s.Evolve(e));
            await _store.SaveAsync(cmd.SchemaId, "schema", version, newState, success.Value, metadata, ct);
            state = newState;
        }

        var schema = state.Schema!;
        return new CommandResult<SchemaDto>.Success(new SchemaDto(
            schema.Id, schema.Name, schema.Description, schema.Content, schema.Version));
    }
}
