using Deedbox;
using Vellum.Modules.Schemas;
using Vellum.Shared;

namespace Vellum.Modules.Modelling;

/// <summary>
/// A write to a model stream that names schemas: an operation with a request or response
/// schema, or a message with a schema. The schema check and the append are one transaction
/// that holds the schema rows (see <see cref="SchemaReferences"/>), so a delete of the schema
/// cannot fall between them.
/// </summary>
public static class SchemaBoundWrite
{
    /// <param name="schemas">The field of the request and the schema it names; a null ID names none.</param>
    public static Task<CommandResult<TResult>> DecideAsync<TResult>(
        IServiceProvider requestServices, Guid projectId, Guid streamId,
        IReadOnlyList<(string Field, Guid? SchemaId)> schemas,
        Func<ModelState, CommandResult<IReadOnlyList<object>>> decide,
        Func<ModelState, TResult> read,
        CancellationToken ct)
    {
        var named = schemas.Where(s => s.SchemaId is not null).Select(s => (s.Field, Id: s.SchemaId!.Value)).ToList();
        if (named.Count == 0)
        {
            return requestServices.GetRequiredService<IEventStore>().ForProject(projectId)
                .Decide(streamId, decide, read, ct);
        }

        return SharedTransaction.RunAsync(requestServices, async services =>
        {
            var db = services.GetRequiredService<AppDbContext>();
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var store = services.GetRequiredService<IEventStore>().ForProject(projectId).UseDbContext(db);

            // The stream lock comes before the row locks, as in every other transaction.
            await store.Execute<ModelState>(StreamId.From(streamId), _ => [], ct);

            var missing = await SchemaReferences.HoldAsync(db, projectId, named.Select(s => s.Id), ct);
            foreach (var (field, id) in named)
            {
                if (missing.Contains(id))
                    return new CommandResult<TResult>.Invalid([new ValidationError(field, "Schema not found in this project")]);
            }

            var result = await store.DecideInTransaction(streamId, decide, read, ct);
            await transaction.CommitAsync(ct);
            return result;
        }, ct);
    }
}
