using Deedbox;
using Microsoft.EntityFrameworkCore;
using Vellum.Modules.Drafts;
using Vellum.Modules.Modelling;
using Vellum.Modules.Modelling.Model;
using Vellum.Modules.Modelling.Revisions;
using Vellum.Modules.Workspaces;
using Vellum.Shared;

namespace Vellum.Modules.Schemas;

public sealed record DeleteSchemaCommandEnvelope(Guid ProjectId, Guid SchemaId);

/// <summary>
/// A schema that a live branch or a stored revision names stays. A revision is a fixed
/// snapshot that a variant can be forked from at any later time, so a schema it names can
/// never be deleted.
/// </summary>
public sealed class DeleteSchemaHandler(IServiceProvider requestServices)
    : ICommandHandler<DeleteSchemaCommandEnvelope, CommandResult>
{
    public async Task<CommandResult> HandleAsync(DeleteSchemaCommandEnvelope cmd, CancellationToken ct = default)
    {
        // An ID that is no schema of this project is not looked up in the event store: it can
        // name a stream of another kind.
        var belongsToProject = await requestServices.GetRequiredService<SchemasDbContext>().Schemas.AsNoTracking()
            .AnyAsync(s => s.Id == cmd.SchemaId && s.ProjectId == cmd.ProjectId, ct);
        if (!belongsToProject)
            return new CommandResult.NotFound("Schema not found");

        // The locks, the search for references and the delete are one transaction. A unit that
        // fails with DBX038 or a deadlock wrote nothing and runs again.
        return await SharedTransaction.RunAsync<CommandResult>(requestServices, async services =>
        {
            var db = services.GetRequiredService<SchemasDbContext>();
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var store = services.GetRequiredService<IEventStore>().UseDbContext(db);

            // The stream lock first, then the row lock (see SchemaReferences). From here until
            // the commit no transaction can write a new reference to this schema.
            var locked = await store.Execute<SchemaState>(StreamId.From(cmd.SchemaId), _ => [], ct);
            if (locked.State.Schema?.ProjectId != cmd.ProjectId)
                return new CommandResult.NotFound("Schema not found");

            var decision = SchemaDecider.Delete(locked.State, cmd.SchemaId);
            if (decision is not CommandResult<IReadOnlyList<object>>.Success delete)
                return new CommandResult.NotFound("Schema not found");

            // The stream has the schema and the read model has no row for it: the read model
            // is being rebuilt. The row is the guard, so the delete waits for it.
            if (!await SchemaReferences.HoldForDeleteAsync(db, cmd.SchemaId, ct))
                return new CommandResult.Conflict("The schema list is being rebuilt. Nothing was deleted; try again.");

            if (await ReferrersAsync(services, store, cmd, ct) is { } refusal)
                return new CommandResult.Conflict(refusal);

            await store.Append(StreamId.From(cmd.SchemaId), ExpectedVersion.Exact(locked.Version), delete.Value, ct);
            await transaction.CommitAsync(ct);
            return new CommandResult.Success();
        }, ct);
    }

    /// <summary>
    /// Why the schema must stay, or null. Each read sees what is committed when it runs, and
    /// every transaction that could add a reference has either committed or waits for the row.
    /// </summary>
    internal static async Task<string?> ReferrersAsync(
        IServiceProvider services, IEventStore store, DeleteSchemaCommandEnvelope cmd, CancellationToken ct)
    {
        var modellingDb = services.GetRequiredService<ModellingDbContext>();

        // Operations and messages live in model streams, one per branch. The list of open
        // branches is read before their states: a merge moves a reference from a draft to main
        // in one commit, and the draft's stream keeps it, so one of the two reads finds it.
        var references = new List<string>();
        var branches = await ModelBranches.OpenAsync(
            cmd.ProjectId, services.GetRequiredService<WorkspacesDbContext>(),
            services.GetRequiredService<DraftsDbContext>(), modellingDb, ct);
        foreach (var (streamId, branch) in branches)
        {
            var (model, _) = await store.Load<ModelState>(StreamId.From(streamId), ct);
            references.AddRange(ContractDecider.ReferencesToSchema(model, cmd.SchemaId).Select(r => $"{r} on {branch}"));
        }

        if (references.Count > 0)
        {
            return $"Schema cannot be deleted. It is referenced by {string.Join(", ", references)}. " +
                "Point them at another schema first.";
        }

        // A revision stores its model as JSON text, in which a GUID has one spelling. The text
        // search selects the few revisions that can name the schema; each is then read as a model.
        var idText = cmd.SchemaId.ToString();
        var candidates = modellingDb.Revisions.AsNoTracking()
            .Where(r => r.ProjectId == cmd.ProjectId && r.State.Contains(idText))
            .OrderBy(r => r.CreatedAt).ThenBy(r => r.Id)
            .Select(r => new { r.Name, r.State })
            .AsAsyncEnumerable();
        await foreach (var revision in candidates.WithCancellation(ct))
        {
            var pinned = ContractDecider.ReferencesToSchema(CreateRevision.ReadState(revision.State), cmd.SchemaId);
            if (pinned.Count > 0)
            {
                return $"Schema cannot be deleted. It is referenced by {string.Join(", ", pinned)} in revision '{revision.Name}'. " +
                    "A revision is a fixed snapshot, so a schema it names stays.";
            }
        }

        return null;
    }
}
