using System.Security.Claims;
using Deedbox;
using Vellum.Modules.Costs;
using Vellum.Modules.Import;
using Vellum.Modules.Import.Scans;
using Vellum.Modules.Promises;
using Vellum.Modules.Stress;
using Vellum.Modules.Review;
using Vellum.Modules.Workspaces.Authorization;
using Vellum.Shared;

namespace Vellum.Modules.Workspaces;

public static class DeleteProject
{
    /// <summary>
    /// Deletes the project with its stress profiles and runs, then everything the importer holds for it: scans, snapshots,
    /// proposals, links and the repository text that nothing else needs. Imported text does
    /// not outlive the project it was imported for. The second step is safe to repeat, and
    /// the scan worker repeats it on its timer for a project whose delete was cut short
    /// between the two. The promises of the project go the same way: their streams, their
    /// rows, their runs and the requirement numbering. A delete that was cut short before
    /// that step is finished by the next delete of any project. The project's cost sheets, pins and saved estimates go as well. The review data goes last, in
    /// the same way: the decisions with their streams, the dispositions, the snapshots, the
    /// open questions and the critiques.
    /// </summary>
    public static async Task<IResult> Handle(
        Guid projectId,
        ClaimsPrincipal user,
        WorkspacesDbContext db,
        ImportDbContext importDb,
        PromisesDbContext promisesDb,
        CostsDbContext costsDb,
        ReviewDbContext reviewDb,
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

        // One transaction: the project and its stress profiles and runs go together.
        await using (var transaction = await db.Database.BeginTransactionAsync(ct))
        {
            db.Projects.Remove(project);
            await db.SaveChangesAsync(ct);
            await StressDbContext.DeleteProjectAsync(db, projectId, ct);
            await transaction.CommitAsync(ct);
        }

        // The project is gone for every reader. The cleanup does not stop when the caller goes away.
        await ImportRetention.DeleteProjectAsync(importDb, projectId, time.GetUtcNow(), CancellationToken.None);
        await Identity.AgentTokens.AgentTokenEndpoints.DeleteForProjectAsync(db, projectId, userId, CancellationToken.None);
        await PromiseRetention.DeleteProjectAsync(promisesDb, store, projectId, CancellationToken.None);
        await PromiseRetention.DeleteOrphansAsync(promisesDb, store, CancellationToken.None);
        await CostRetention.DeleteProjectAsync(costsDb, store, projectId, CancellationToken.None);
        await ReviewRetention.DeleteProjectAsync(reviewDb, store, projectId, CancellationToken.None);
        await ReviewRetention.DeleteOrphansAsync(reviewDb, store, CancellationToken.None);
        return Results.NoContent();
    }
}
