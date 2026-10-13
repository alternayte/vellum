using Microsoft.EntityFrameworkCore;

namespace Vellum.Modules.Schemas;

/// <summary>
/// The guard that keeps a schema from being deleted under a model that names it. Schemas and
/// models are separate streams, so no single decision sees both. The lock object is the
/// schema's read-model row:
/// <list type="bullet">
/// <item>A transaction that writes a reference to a schema (an operation or a message that
/// names it, an import, or a fork or a revision that copies such a model) holds the row
/// <c>FOR KEY SHARE</c> until it commits, and writes the reference only when the row exists.</item>
/// <item>The delete holds the row <c>FOR UPDATE</c>, which waits for every holder above and
/// stops new ones, and only then looks for references. Its commit removes the row.</item>
/// </list>
/// So a reference that commits before the delete looks is found, and one that would commit
/// after it finds no row. Every transaction takes its stream locks first and these row locks
/// second, and takes no stream lock after a row lock, so the two kinds cannot form a cycle.
/// </summary>
public static class SchemaReferences
{
    /// <summary>
    /// Holds the rows of <paramref name="schemaIds"/> for the open transaction of
    /// <paramref name="db"/> and returns the IDs that are not schemas of the project.
    /// </summary>
    public static async Task<IReadOnlySet<Guid>> HoldAsync(
        DbContext db, Guid projectId, IEnumerable<Guid> schemaIds, CancellationToken ct)
    {
        var ids = schemaIds.Distinct().ToArray();
        if (ids.Length == 0)
            return new HashSet<Guid>();
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("A schema row is held for a transaction; open one first.");

        // The table of SchemasDbContext, named here because the lock must be taken on the
        // connection of the transaction that writes the reference.
        var held = await db.Database
            .SqlQuery<Guid>($"""
                SELECT id AS "Value" FROM schemas.schemas
                WHERE id = ANY({ids}) AND project_id = {projectId}
                ORDER BY id
                FOR KEY SHARE
                """)
            .ToListAsync(ct);

        var missing = ids.ToHashSet();
        missing.ExceptWith(held);
        return missing;
    }

    /// <summary>
    /// Takes the row for a delete. It returns when no transaction holds the row for a
    /// reference, and no new one can until this transaction ends. False when there is no row
    /// to take: without it the delete has no guard and must not go on.
    /// </summary>
    public static async Task<bool> HoldForDeleteAsync(DbContext db, Guid schemaId, CancellationToken ct)
    {
        var held = await db.Database
            .SqlQuery<Guid>($"""SELECT id AS "Value" FROM schemas.schemas WHERE id = {schemaId} FOR UPDATE""")
            .ToListAsync(ct);
        return held.Count == 1;
    }
}
