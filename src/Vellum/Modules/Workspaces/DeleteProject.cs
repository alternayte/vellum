using System.Security.Claims;
using Vellum.Modules.Import;
using Vellum.Modules.Import.Scans;
using Vellum.Modules.Stress;
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
    /// between the two.
    /// </summary>
    public static async Task<IResult> Handle(
        Guid projectId,
        ClaimsPrincipal user,
        WorkspacesDbContext db,
        ImportDbContext importDb,
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
        return Results.NoContent();
    }
}
