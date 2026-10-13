using System.Security.Claims;
using Deedbox;
using Vellum.Modules.Costs;
using Vellum.Modules.Import;
using Vellum.Modules.Import.Scans;
using Vellum.Modules.Workspaces.Authorization;
using Vellum.Shared;

namespace Vellum.Modules.Workspaces;

public static class DeleteProject
{
    /// <summary>
    /// Deletes the project, then everything the importer holds for it: scans, snapshots,
    /// proposals, links and the repository text that nothing else needs. Imported text does
    /// not outlive the project it was imported for. The second step is safe to repeat, and
    /// the scan worker repeats it on its timer for a project whose delete was cut short
    /// between the two. The project's cost sheets, pins and saved estimates go as well.
    /// </summary>
    public static async Task<IResult> Handle(
        Guid projectId,
        ClaimsPrincipal user,
        WorkspacesDbContext db,
        ImportDbContext importDb,
        CostsDbContext costsDb,
        IEventStore store,
        TimeProvider time,
        WorkspaceAuthorizationService auth,
        CancellationToken ct)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier)!;
        await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Owner, ct);

        var project = await db.Projects.FindAsync([projectId], ct);
        if (project is null)
            return Results.NotFound(new ErrorResponse("not_found", "Project not found"));

        db.Projects.Remove(project);
        await db.SaveChangesAsync(ct);

        // The project is gone for every reader. The cleanup does not stop when the caller goes away.
        await ImportRetention.DeleteProjectAsync(importDb, projectId, time.GetUtcNow(), CancellationToken.None);
        await CostRetention.DeleteProjectAsync(costsDb, store, projectId, CancellationToken.None);
        return Results.NoContent();
    }
}
