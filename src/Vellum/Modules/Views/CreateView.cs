using System.Security.Claims;
using Vellum.Modules.Views.Entities;
using Vellum.Modules.Workspaces.Authorization;

namespace Vellum.Modules.Views;

public sealed record CreateViewRequest(Guid Id, string Name, Guid? RootElementId);

public static class CreateView
{
    public static async Task<IResult> Handle(
        Guid projectId,
        CreateViewRequest request,
        ClaimsPrincipal user,
        ViewsDbContext db,
        WorkspaceAuthorizationService auth,
        CancellationToken ct)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier)!;
        await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Editor, ct);

        var existing = await db.Views.FindAsync([request.Id], ct);
        if (existing is not null)
            return Results.Ok(ToDto(existing));

        var view = new ViewEntity
        {
            Id = request.Id,
            ProjectId = projectId,
            Name = request.Name,
            RootElementId = request.RootElementId
        };
        db.Views.Add(view);
        await db.SaveChangesAsync(ct);

        return Results.Created($"/api/projects/{projectId}/views/{view.Id}", ToDto(view));
    }

    private static ViewDto ToDto(ViewEntity v) =>
        new(v.Id, v.ProjectId, v.Name, v.RootElementId, v.VisibleElementIds,
            v.ActiveLens, v.ActiveFlowId, v.CreatedAt, v.UpdatedAt);
}
