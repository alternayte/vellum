using Deedbox;
using Microsoft.EntityFrameworkCore;
using Vellum.Shared;

namespace Vellum.Modules.Drafts;

/// <summary>What the draft tables keep of a deleted project: nothing. Each draft goes with its model stream and its comments.</summary>
public sealed class DraftsRetention(AppDbContext db, IEventStore store) : IProjectRetention
{
    public async Task DeleteProjectAsync(DeletedProject project, CancellationToken ct)
    {
        var streams = await db.Drafts.AsNoTracking()
            .Where(d => d.ProjectId == project.Id).Select(d => d.StreamId).ToListAsync(ct);
        foreach (var streamId in streams)
            await store.DeleteStream(StreamId.From(streamId), ct);

        // The comments of a draft go with its row (ON DELETE CASCADE).
        await db.Drafts.Where(d => d.ProjectId == project.Id).ExecuteDeleteAsync(ct);
    }
}
