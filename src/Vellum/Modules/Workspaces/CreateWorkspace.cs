using System.Security.Claims;
using Vellum.Modules.Workspaces.Entities;

namespace Vellum.Modules.Workspaces;

public sealed record CreateWorkspaceRequest(Guid Id, string Name);
public sealed record WorkspaceDto(Guid Id, string Name, string CreatedBy, DateTimeOffset CreatedAt);

public static class CreateWorkspace
{
    public static async Task<IResult> Handle(
        CreateWorkspaceRequest request,
        ClaimsPrincipal user,
        WorkspacesDbContext db,
        CancellationToken ct)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier)!;

        var existing = await db.Workspaces.FindAsync([request.Id], ct);
        if (existing is not null)
            return Results.Ok(ToDto(existing));

        var workspace = new WorkspaceEntity
        {
            Id = request.Id,
            Name = request.Name,
            CreatedBy = userId
        };
        db.Workspaces.Add(workspace);

        db.Memberships.Add(new MembershipEntity
        {
            Id = Guid.NewGuid(),
            WorkspaceId = request.Id,
            UserId = userId,
            Role = "Owner"
        });

        await db.SaveChangesAsync(ct);
        return Results.Created($"/api/workspaces/{workspace.Id}", ToDto(workspace));
    }

    private static WorkspaceDto ToDto(WorkspaceEntity w) => new(w.Id, w.Name, w.CreatedBy, w.CreatedAt);
}
