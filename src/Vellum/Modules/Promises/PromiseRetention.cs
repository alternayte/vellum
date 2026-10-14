using Deedbox;
using Microsoft.EntityFrameworkCore;
using Vellum.Shared;

namespace Vellum.Modules.Promises;

/// <summary>
/// What the promise tables keep when a project is deleted: nothing. The promises of a
/// project, their runs, the project's requirement numbering and its impact gate policy go with it. A promise is a
/// stream, so its stream is deleted too: a rebuild of the read model then does not bring the
/// rows back. Every statement is safe to run again, and <see cref="DeleteOrphansAsync"/>
/// repeats the work for a project whose delete was cut short.
/// </summary>
public sealed class PromiseRetention(AppDbContext db, IEventStore store) : IProjectRetention
{
    public async Task DeleteProjectAsync(DeletedProject project, CancellationToken ct)
    {
        await DeleteProjectAsync(project.Id, ct);
        await DeleteOrphansAsync(ct);
    }

    private async Task DeleteProjectAsync(Guid projectId, CancellationToken ct)
    {
        var promiseIds = await db.Promises.AsNoTracking().Where(p => p.ProjectId == projectId).Select(p => p.Id).ToListAsync(ct);
        // A row exists only for a promise stream, so each ID names one. The projection removes
        // the rows of the promise when it sees the tombstone. A stream that is already deleted
        // is left as it is.
        foreach (var promiseId in promiseIds)
            await store.DeleteStream(StreamId.From(promiseId), ct);

        // What is left when a stream was already gone: rows with no stream behind them.
        await db.PromiseRuns.Where(r => r.ProjectId == projectId).ExecuteDeleteAsync(ct);
        await db.Promises.Where(p => p.ProjectId == projectId).ExecuteDeleteAsync(ct);
        await db.Numbering.Where(n => n.ProjectId == projectId).ExecuteDeleteAsync(ct);
        await db.ImpactGates.Where(g => g.ProjectId == projectId).ExecuteDeleteAsync(ct);
    }

    /// <summary>Runs <see cref="DeleteProjectAsync"/> for every project that the promise tables name and that no longer exists.</summary>
    private async Task DeleteOrphansAsync(CancellationToken ct)
    {
        var gone = await db.Database.SqlQuery<Guid>($"""
            SELECT i.project_id AS "Value"
            FROM (SELECT project_id FROM promises.promises
                  UNION SELECT project_id FROM promises.runs
                  UNION SELECT project_id FROM promises.numbering
                  UNION SELECT project_id FROM promises.impact_gates) i
            WHERE NOT EXISTS (SELECT 1 FROM workspaces.projects p WHERE p.id = i.project_id)
            """).ToListAsync(ct);
        foreach (var projectId in gone)
            await DeleteProjectAsync(projectId, ct);
    }
}
