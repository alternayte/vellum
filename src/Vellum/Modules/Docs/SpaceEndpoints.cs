using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Vellum.Modules.Docs.Entities;
using Vellum.Modules.Workspaces.Authorization;
using Vellum.Shared;

namespace Vellum.Modules.Docs;

public sealed record CreateSpaceRequest(Guid Id, string Name);

public sealed record UpdateSpaceRequest(string? Name);

public sealed record SpaceDto(
    Guid Id, Guid ProjectId, string Name,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

/// <summary>The spaces of a project: named groups of documents.</summary>
public static class SpaceEndpoints
{
    public static WebApplication MapSpaceEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/projects/{projectId}/spaces")
            .RequireAuthorization()
            .WithTags("Spaces");

        group.MapPost("/", Create);
        group.MapGet("/", List);
        group.MapPatch("/{spaceId}", Update);
        group.MapDelete("/{spaceId}", Delete);

        return app;
    }

    private static async Task<IResult> Create(
        Guid projectId,
        CreateSpaceRequest request,
        ClaimsPrincipal user,
        AppDbContext db,
        WorkspaceAuthorizationService auth,
        CancellationToken ct)
    {
        await auth.RequireProjectRoleAsync(projectId, user.UserId(), WorkspaceRole.Editor, ct);

        if (ResultExtensions.Refusal(ResultExtensions.Required("name", request.Name), TextRule.Line("name", request.Name)) is { } refusal)
            return refusal;

        // A retry in the same project gets the space back. A space of another project is not
        // shown: the caller learns only that the ID is taken.
        var existing = await db.Spaces.FindAsync([request.Id], ct);
        if (existing is not null)
            return existing.ProjectId == projectId ? Results.Ok(ToDto(existing)) : IdInUse.Conflict();

        var space = new SpaceEntity
        {
            Id = request.Id,
            ProjectId = projectId,
            Name = request.Name
        };
        db.Spaces.Add(space);
        await db.SaveChangesAsync(ct);

        return Results.Created($"/api/projects/{projectId}/spaces/{space.Id}", ToDto(space));
    }

    private static async Task<IResult> List(
        Guid projectId,
        ClaimsPrincipal user,
        AppDbContext db,
        WorkspaceAuthorizationService auth,
        CancellationToken ct)
    {
        await auth.RequireProjectRoleAsync(projectId, user.UserId(), WorkspaceRole.Viewer, ct);

        var spaces = await db.Spaces.AsNoTracking()
            .Where(s => s.ProjectId == projectId)
            .OrderBy(s => s.Name)
            .Select(s => new SpaceDto(s.Id, s.ProjectId, s.Name, s.CreatedAt, s.UpdatedAt))
            .ToListAsync(ct);

        return Results.Ok(spaces);
    }

    private static async Task<IResult> Update(
        Guid projectId, Guid spaceId,
        UpdateSpaceRequest request,
        ClaimsPrincipal user,
        AppDbContext db,
        WorkspaceAuthorizationService auth,
        TimeProvider time,
        CancellationToken ct)
    {
        await auth.RequireProjectRoleAsync(projectId, user.UserId(), WorkspaceRole.Editor, ct);

        if (ResultExtensions.Refusal(
                request.Name is null ? null : ResultExtensions.Required("name", request.Name),
                TextRule.Line("name", request.Name)) is { } refusal)
            return refusal;

        var space = await db.Spaces.FindAsync([spaceId], ct);
        if (space is null || space.ProjectId != projectId)
            return NotFound();

        if (request.Name is not null) space.Name = request.Name;
        space.UpdatedAt = time.GetUtcNow();

        await db.SaveChangesAsync(ct);
        return Results.Ok(ToDto(space));
    }

    private static async Task<IResult> Delete(
        Guid projectId, Guid spaceId,
        ClaimsPrincipal user,
        AppDbContext db,
        WorkspaceAuthorizationService auth,
        CancellationToken ct)
    {
        await auth.RequireProjectRoleAsync(projectId, user.UserId(), WorkspaceRole.Editor, ct);

        var space = await db.Spaces.FindAsync([spaceId], ct);
        if (space is null || space.ProjectId != projectId)
            return NotFound();

        db.Spaces.Remove(space);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static IResult NotFound() => Results.NotFound(new ErrorResponse("not_found", "Space not found"));

    private static SpaceDto ToDto(SpaceEntity s) =>
        new(s.Id, s.ProjectId, s.Name, s.CreatedAt, s.UpdatedAt);
}
