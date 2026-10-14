using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Vellum.Modules.Workspaces.Authorization;
using Vellum.Shared;

namespace Vellum.Modules.Workspaces;

public static class DeleteProject
{
    /// <summary>
    /// Deletes the project, then everything each module holds for it (<see cref="IProjectRetention"/>):
    /// the model and its branches, drafts, schemas, scenarios, views, docs, promises, cost
    /// sheets, review data, load tests, stress runs, imports, AI proposals and agent tokens,
    /// with their event streams. Imported text and modelled data do not outlive the project.
    /// </summary>
    public static async Task<IResult> Handle(
        Guid projectId,
        ClaimsPrincipal user,
        AppDbContext db,
        [FromServices] IEnumerable<IProjectRetention> retentions,
        ILoggerFactory loggers,
        WorkspaceAuthorizationService auth,
        CancellationToken ct)
    {
        var userId = user.UserId();
        await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Owner, ct);

        var project = await db.Projects.FindAsync([projectId], ct);
        if (project is null)
            return Results.NotFound(new ErrorResponse("not_found", "Project not found"));

        // The row goes first: every endpoint resolves the project, so no request reaches the
        // data of a module while it goes.
        db.Projects.Remove(project);
        await db.SaveChangesAsync(ct);

        // The cleanup does not stop when the caller goes away. A module that fails does not
        // keep the others from deleting their data; the failure is reported afterwards.
        var deleted = new DeletedProject(project.Id, project.WorkspaceId, project.StreamId, userId);
        var failures = new List<Exception>();
        foreach (var retention in retentions)
        {
            try
            {
                await retention.DeleteProjectAsync(deleted, CancellationToken.None);
            }
            catch (Exception ex)
            {
                loggers.CreateLogger(typeof(DeleteProject)).LogError(ex,
                    "{Retention} did not delete the data of project {ProjectId}", retention.GetType().Name, projectId);
                failures.Add(ex);
            }
        }

        if (failures.Count > 0)
            throw new AggregateException($"The data of project {projectId} was not deleted in full.", failures);
        return Results.NoContent();
    }
}
