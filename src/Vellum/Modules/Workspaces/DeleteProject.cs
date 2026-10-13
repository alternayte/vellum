using System.Security.Claims;
using Deedbox;
using Vellum.Modules.Import;
using Vellum.Modules.Import.Scans;
using Vellum.Modules.Promises;
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
    /// between the two. The promises of the project go the same way: their streams, their
    /// rows, their runs and the requirement numbering. A delete that was cut short before
    /// that step is finished by the next delete of any project.
    /// </summary>
    public static async Task<IResult> Handle(
        Guid projectId,
        ClaimsPrincipal user,
        WorkspacesDbContext db,
        ImportDbContext importDb,
        PromisesDbContext promisesDb,
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
        await Identity.AgentTokens.AgentTokenEndpoints.DeleteForProjectAsync(db, projectId, userId, CancellationToken.None);
        await PromiseRetention.DeleteProjectAsync(promisesDb, store, projectId, CancellationToken.None);
        await PromiseRetention.DeleteOrphansAsync(promisesDb, store, CancellationToken.None);
        return Results.NoContent();
    }
}
