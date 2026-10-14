using Microsoft.EntityFrameworkCore;
using Vellum.Shared;

namespace Vellum.Modules.Ai.Design;

/// <summary>
/// What the design proposals keep of a deleted project: nothing. The usage records stay: they
/// belong to the workspace, name no project, and hold the spend that the workspace budget counts.
/// </summary>
public sealed class DesignRetention(AppDbContext db) : IProjectRetention
{
    public Task DeleteProjectAsync(DeletedProject project, CancellationToken ct) =>
        db.DesignProposals.Where(p => p.ProjectId == project.Id).ExecuteDeleteAsync(ct);
}
