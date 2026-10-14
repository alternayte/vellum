using Microsoft.EntityFrameworkCore;
using Vellum.Shared;

namespace Vellum.Modules.Docs;

/// <summary>What the doc tables keep of a deleted project: nothing.</summary>
public sealed class DocsRetention(DocsDbContext db) : IProjectRetention
{
    public async Task DeleteProjectAsync(DeletedProject project, CancellationToken ct)
    {
        await db.Documents.Where(d => d.ProjectId == project.Id).ExecuteDeleteAsync(ct);
        await db.Spaces.Where(s => s.ProjectId == project.Id).ExecuteDeleteAsync(ct);
    }
}
