using System.Security.Claims;
using Vellum.Modules.Workspaces.Authorization;
using Vellum.Modules.Workspaces.Entities;
using Vellum.Shared;

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
