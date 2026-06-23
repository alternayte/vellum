using Microsoft.EntityFrameworkCore;
using Vellum.Kernel.Aggregates;
using Vellum.Kernel.CommandHandling;
using Vellum.Kernel.Results;

namespace Vellum.Modules.Schemas;

public sealed record DeleteSchemaCommandEnvelope(
    Guid ProjectId, Guid SchemaId, string UserId);

public sealed class DeleteSchemaHandler : ICommandHandler<DeleteSchemaCommandEnvelope, CommandResult>
{
    private readonly AggregateStore _store;
    private readonly SchemaProjection _projection;
    private readonly SchemasDbContext _db;

    public DeleteSchemaHandler(AggregateStore store, SchemaProjection projection, SchemasDbContext db)
    {
        _store = store;
        _projection = projection;
        _db = db;
    }

    public async Task<CommandResult> HandleAsync(DeleteSchemaCommandEnvelope cmd, CancellationToken ct = default)
    {
        // Verify schema belongs to project
        var entity = await _db.Schemas.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == cmd.SchemaId && s.ProjectId == cmd.ProjectId, ct);
        if (entity is null)
            return new CommandResult.NotFound("Schema not found");

        var (state, version) = await _store.LoadAsync<SchemaState, SchemaEvent>(cmd.SchemaId, ct);
        var result = SchemaDecider.Delete(state, cmd.SchemaId);

        if (result is not CommandResult<IReadOnlyList<SchemaEvent>>.Success success)
        {
            return result switch
            {
                CommandResult<IReadOnlyList<SchemaEvent>>.NotFound n => new CommandResult.NotFound(n.Message),
                _ => new CommandResult.Conflict("Unexpected error")
            };
        }

        var metadata = new EventMetadata { ActorId = Guid.Parse(cmd.UserId), CorrelationId = Guid.NewGuid() };
        var newState = success.Value.Aggregate(state, (s, e) => s.Evolve(e));
        await _store.SaveAsync(cmd.SchemaId, "schema", version, newState, success.Value, metadata, ct);

        return new CommandResult.Success();
    }
}
