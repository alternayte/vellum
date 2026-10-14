using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Vellum.Modules.Modelling.Entities;
using Vellum.Modules.Modelling.Revisions;
using Vellum.Modules.Workspaces;
using Vellum.Modules.Workspaces.Authorization;
using Vellum.Shared;

namespace Vellum.Modules.Modelling.Variants;

public static class ListVariants
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

        var variants = await db.Variants.AsNoTracking()
            .Where(v => v.ProjectId == projectId)
            .OrderByDescending(v => v.CreatedAt).ThenBy(v => v.Id)
            .Select(v => new VariantDto(
                v.Id, v.ProjectId, v.Name,
                v.StreamId, v.BaseStreamId, v.BaseRevisionId,
                v.ForkVersion, v.Status, v.CreatedBy, v.CreatedAt))
            .ToListAsync(ct);

        return Results.Ok(variants);
    }
}

public static class GetVariant
{
    public static async Task<IResult> Handle(
        Guid projectId,
        Guid variantId,
        ClaimsPrincipal user,
        AppDbContext db,
        WorkspaceAuthorizationService auth,
        CancellationToken ct)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier)!;
        await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Viewer, ct);

        var variant = await db.Variants.AsNoTracking()
            .FirstOrDefaultAsync(v => v.Id == variantId && v.ProjectId == projectId, ct);
        if (variant is null) return Results.NotFound(new ErrorResponse("not_found", "Variant not found"));

        return Results.Ok(CreateVariant.ToDto(variant));
    }
}
