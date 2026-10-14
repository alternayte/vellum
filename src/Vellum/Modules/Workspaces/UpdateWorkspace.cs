using System.Security.Claims;
using Vellum.Modules.Workspaces.Authorization;
using Vellum.Shared;

namespace Vellum.Modules.Workspaces;

public sealed record UpdateWorkspaceRequest(string? Name);

public static class UpdateWorkspace
{
    public static async Task<IResult> Handle(
        Guid id,
        UpdateWorkspaceRequest request,
        ClaimsPrincipal user,
        AppDbContext db,
        WorkspaceAuthorizationService auth,
        CancellationToken ct)
    {
        var userId = user.UserId();
        await auth.RequireRoleAsync(id, userId, WorkspaceRole.Owner, ct);

        if (WorkspaceText.Refusal(request.Name, nameRequired: false) is { } refusal)
            return refusal;

        var workspace = await db.Workspaces.FindAsync([id], ct);
        if (workspace is null)
            return Results.NotFound(new ErrorResponse("not_found", "Workspace not found"));

        if (request.Name is not null)
            workspace.Name = request.Name;

        workspace.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        return Results.Ok(new WorkspaceDto(workspace.Id, workspace.Name, workspace.CreatedBy, workspace.CreatedAt));
    }
}
