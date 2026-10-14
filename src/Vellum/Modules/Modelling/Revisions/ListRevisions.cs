using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Vellum.Modules.Modelling.Entities;
using Vellum.Modules.Workspaces;
using Vellum.Modules.Workspaces.Authorization;
using Vellum.Shared;

namespace Vellum.Modules.Modelling.Revisions;

public static class ListRevisions
{
    public static async Task<IResult> Handle(
        Guid projectId,
        ClaimsPrincipal user,
        AppDbContext db,
        WorkspaceAuthorizationService auth,
        CancellationToken ct)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier)!;
        await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Viewer, ct);

        var revisions = await db.Revisions.AsNoTracking()
            .Where(r => r.ProjectId == projectId)
            .OrderByDescending(r => r.CreatedAt).ThenBy(r => r.Id)
            .Select(r => new RevisionDto(
                r.Id, r.ProjectId, r.Name, r.Note,
                r.StreamId, r.Version, r.CreatedBy, r.CreatedAt))
            .ToListAsync(ct);

        return Results.Ok(revisions);
    }
}

public static class GetRevision
{
    public static async Task<IResult> Handle(
        Guid projectId,
        Guid revisionId,
        ClaimsPrincipal user,
        AppDbContext db,
        WorkspaceAuthorizationService auth,
        CancellationToken ct)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier)!;
        await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Viewer, ct);

        var revision = await db.Revisions.AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == revisionId && r.ProjectId == projectId, ct);
        if (revision is null) return Results.NotFound(new ErrorResponse("not_found", "Revision not found"));

        return Results.Ok(CreateRevision.ToDto(revision));
    }
}
