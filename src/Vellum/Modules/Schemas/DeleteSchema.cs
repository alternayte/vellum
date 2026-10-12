using Deedbox;
using Microsoft.EntityFrameworkCore;
using Vellum.Modules.Drafts;
using Vellum.Modules.Modelling;
using Vellum.Modules.Modelling.Model;
using Vellum.Modules.Workspaces;
using Vellum.Shared;

namespace Vellum.Modules.Schemas;

public sealed record DeleteSchemaCommandEnvelope(Guid ProjectId, Guid SchemaId);

public sealed class DeleteSchemaHandler(
    IEventStore store, SchemasDbContext db,
    WorkspacesDbContext workspacesDb, DraftsDbContext draftsDb, ModellingDbContext modellingDb)
    : ICommandHandler<DeleteSchemaCommandEnvelope, CommandResult>
{
    public async Task<CommandResult> HandleAsync(DeleteSchemaCommandEnvelope cmd, CancellationToken ct = default)
    {
        // Verify schema belongs to project
        var belongsToProject = await db.Schemas.AsNoTracking()
            .AnyAsync(s => s.Id == cmd.SchemaId && s.ProjectId == cmd.ProjectId, ct);
        if (!belongsToProject)
            return new CommandResult.NotFound("Schema not found");

        // An operation names its request and response schema by ID. Operations live in model
        // streams, one per branch, so every live branch of the project is read.
        var references = new List<string>();
        foreach (var (streamId, branch) in await ModelBranches.OpenAsync(cmd.ProjectId, workspacesDb, draftsDb, modellingDb, ct))
        {
            var (model, _) = await store.Load<ModelState>(StreamId.From(streamId), ct);
            references.AddRange(ContractDecider.ReferencesToSchema(model, cmd.SchemaId).Select(r => $"{r} on {branch}"));
        }

        if (references.Count > 0)
        {
            return new CommandResult.Conflict(
                $"Schema cannot be deleted. It is referenced by {string.Join(", ", references)}. " +
                "Point those operations at another schema first.");
        }

        return await store.Decide<SchemaState>(
            cmd.SchemaId,
            state => SchemaDecider.Delete(state, cmd.SchemaId),
            ct);
    }
}
