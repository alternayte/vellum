using Deedbox;
using Microsoft.EntityFrameworkCore;
using Vellum.Shared;

namespace Vellum.Modules.Trace.Scenarios;

/// <summary>What the scenario table and the scenario streams keep of a deleted project: nothing.</summary>
public sealed class ScenariosRetention(AppDbContext db, IEventStore store) : IProjectRetention
{
    public async Task DeleteProjectAsync(DeletedProject project, CancellationToken ct)
    {
        // A deleted scenario has no row, but its stream keeps its events. Each save names the
        // project, so the saves find every scenario stream of the project.
        var streams = await db.Database.SqlQuery<string>($"""
            SELECT DISTINCT stream_id AS "Value" FROM deedbox.events
            WHERE stream_type = 'scenario' AND payload ->> 'projectId' = {project.Id.ToString()}
            """).ToListAsync(ct);
        var rows = await db.Scenarios.AsNoTracking()
            .Where(s => s.ProjectId == project.Id).Select(s => s.Id).ToListAsync(ct);
        foreach (var streamId in streams.Concat(rows.Select(StreamId.From)).Distinct())
            await store.DeleteStream(streamId, ct);

        await db.Scenarios.Where(s => s.ProjectId == project.Id).ExecuteDeleteAsync(ct);
    }
}
