using Deedbox;
using Microsoft.EntityFrameworkCore;
using Vellum.Shared;

namespace Vellum.Modules.Schemas;

public sealed record DeleteSchemaCommandEnvelope(Guid ProjectId, Guid SchemaId);

public sealed class DeleteSchemaHandler : ICommandHandler<DeleteSchemaCommandEnvelope, CommandResult>
{
    private readonly IEventStore _store;
    private readonly SchemasDbContext _db;

    public DeleteSchemaHandler(IEventStore store, SchemasDbContext db)
    {
        _store = store;
        _db = db;
    }

    public async Task<CommandResult> HandleAsync(DeleteSchemaCommandEnvelope cmd, CancellationToken ct = default)
    {
        // Verify schema belongs to project
        var belongsToProject = await _db.Schemas.AsNoTracking()
            .AnyAsync(s => s.Id == cmd.SchemaId && s.ProjectId == cmd.ProjectId, ct);
        if (!belongsToProject)
            return new CommandResult.NotFound("Schema not found");

        return await _store.Decide<SchemaState>(
            cmd.SchemaId,
            state => SchemaDecider.Delete(state, cmd.SchemaId),
            ct);
    }
}
