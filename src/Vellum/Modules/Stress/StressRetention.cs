using Microsoft.EntityFrameworkCore;
using Vellum.Shared;

namespace Vellum.Modules.Stress;

/// <summary>What the stress tables keep of a deleted project: nothing.</summary>
public sealed class StressRetention(StressDbContext db) : IProjectRetention
{
    // The runs of a profile go with its row (ON DELETE CASCADE).
    public Task DeleteProjectAsync(DeletedProject project, CancellationToken ct) =>
        db.Profiles.Where(p => p.ProjectId == project.Id).ExecuteDeleteAsync(ct);
}
