using System.Security.Claims;
using Vellum.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc;

namespace Vellum.Modules.Workspaces;

public sealed record CreateProjectRequest(Guid Id, string Name, string? Description);
public sealed record ProjectDto(Guid Id, Guid WorkspaceId, string Name, string? Description, Guid StreamId, DateTimeOffset CreatedAt);

public static class CreateProject
{
    public static async Task<IResult> Handle(
        Guid workspaceId,
        CreateProjectRequest request,
        ClaimsPrincipal user,
        AppDbContext db,
        WorkspaceAuthorizationService auth,
        CancellationToken ct)
    {
        var userId = user.UserId();
        await auth.RequireRoleAsync(workspaceId, userId, WorkspaceRole.Editor, ct);

        if (WorkspaceText.Refusal(request.Name, nameRequired: true, request.Description) is { } refusal)
            return refusal;
        if (request.Id == Guid.Empty)
            return Results.BadRequest(new ErrorResponse("validation_error", "Validation failed",
                Errors: [new FieldError("id", "An ID is required")]));

        // A retry in the same workspace gets the project back. A project of another workspace
        // is not shown: the caller learns only that the ID is taken.
        var existing = await db.Projects.FindAsync([request.Id], ct);
        if (existing is not null)
            return existing.WorkspaceId == workspaceId ? Results.Ok(ToDto(existing)) : IdInUse.Conflict();

        var project = new ProjectEntity
        {
            Id = request.Id,
            WorkspaceId = workspaceId,
            Name = request.Name,
            Description = request.Description,
            StreamId = Guid.NewGuid()
        };
        db.Projects.Add(project);
        await db.SaveChangesAsync(ct);

        return Results.Created($"/api/projects/{project.Id}", ToDto(project));
    }

    private static ProjectDto ToDto(ProjectEntity p) =>
        new(p.Id, p.WorkspaceId, p.Name, p.Description, p.StreamId, p.CreatedAt);
}

/// <param name="YourRole">The role of the caller in the workspace of the project.</param>
public sealed record ProjectDetailDto(
    Guid Id, Guid WorkspaceId, string Name, string? Description, Guid StreamId, DateTimeOffset CreatedAt, string YourRole);

public static class GetProject
{
    public static async Task<IResult> Handle(
        Guid projectId,
        ClaimsPrincipal user,
        AppDbContext db,
        WorkspaceAuthorizationService auth,
        CancellationToken ct)
    {
        var userId = user.UserId();
        var project = await db.Projects.AsNoTracking().FirstOrDefaultAsync(p => p.Id == projectId, ct);
        // A project of a workspace the caller is no member of reads as a project that does not exist.
        if (project is null || await auth.GetRoleAsync(project.WorkspaceId, userId, ct) is not { } role)
            return Results.NotFound(new ErrorResponse("not_found", "Project not found"));

        return Results.Ok(new ProjectDetailDto(
            project.Id, project.WorkspaceId, project.Name, project.Description, project.StreamId, project.CreatedAt, role.ToString()));
    }
}

public static class ListProjects
{
    public static async Task<IResult> Handle(
        Guid workspaceId,
        ClaimsPrincipal user,
        AppDbContext db,
        WorkspaceAuthorizationService auth,
        CancellationToken ct)
    {
        var userId = user.UserId();
        await auth.RequireRoleAsync(workspaceId, userId, WorkspaceRole.Viewer, ct);

        var projects = await db.Projects
            .AsNoTracking()
            .Where(p => p.WorkspaceId == workspaceId)
            .OrderBy(p => p.Name)
            .Select(p => new ProjectDto(p.Id, p.WorkspaceId, p.Name, p.Description, p.StreamId, p.CreatedAt))
            .ToListAsync(ct);

        return Results.Ok(projects);
    }
}

public sealed record UpdateProjectRequest(string? Name, string? Description);

public static class UpdateProject
{
    public static async Task<IResult> Handle(
        Guid projectId,
        UpdateProjectRequest request,
        ClaimsPrincipal user,
        AppDbContext db,
        WorkspaceAuthorizationService auth,
        TimeProvider time,
        CancellationToken ct)
    {
        var userId = user.UserId();
        await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Editor, ct);

        if (WorkspaceText.Refusal(request.Name, nameRequired: false, request.Description) is { } refusal)
            return refusal;

        var project = await db.Projects.FindAsync([projectId], ct);
        if (project is null)
            return Results.NotFound(new ErrorResponse("not_found", "Project not found"));

        if (request.Name is not null) project.Name = request.Name;
        if (request.Description is not null) project.Description = request.Description;
        project.UpdatedAt = time.GetUtcNow();

        await db.SaveChangesAsync(ct);
        return Results.Ok(new ProjectDto(project.Id, project.WorkspaceId, project.Name, project.Description, project.StreamId, project.CreatedAt));
    }
}

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

        var failures = await RunRetentionsAsync(
            new DeletedProject(project.Id, project.WorkspaceId, project.StreamId, userId), retentions, loggers);
        if (failures.Count > 0)
            throw new AggregateException($"The data of project {projectId} was not deleted in full.", failures);
        return Results.NoContent();
    }

    /// <summary>
    /// Runs every retention for a project whose row is gone. The cleanup does not stop when the
    /// caller goes away. A module that fails does not keep the others from deleting their data;
    /// the failures are returned for the caller to report.
    /// </summary>
    internal static async Task<List<Exception>> RunRetentionsAsync(
        DeletedProject deleted, IEnumerable<IProjectRetention> retentions, ILoggerFactory loggers)
    {
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
                    "{Retention} did not delete the data of project {ProjectId}", retention.GetType().Name, deleted.Id);
                failures.Add(ex);
            }
        }

        return failures;
    }
}
