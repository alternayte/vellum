using Microsoft.EntityFrameworkCore;

namespace Vellum.Modules.Workspaces.Authorization;

public sealed class WorkspaceAuthorizationService
{
    private readonly WorkspacesDbContext _db;

    public WorkspaceAuthorizationService(WorkspacesDbContext db) => _db = db;

    public async Task<WorkspaceRole?> GetRoleAsync(Guid workspaceId, string userId, CancellationToken ct = default)
    {
        var membership = await _db.Memberships
            .AsNoTracking()
            .FirstOrDefaultAsync(m => m.WorkspaceId == workspaceId && m.UserId == userId, ct);

        if (membership is null) return null;
        return Enum.Parse<WorkspaceRole>(membership.Role, ignoreCase: true);
    }

    public async Task RequireRoleAsync(Guid workspaceId, string userId, WorkspaceRole minimumRole, CancellationToken ct = default)
    {
        var role = await GetRoleAsync(workspaceId, userId, ct);
        if (role is null || role < minimumRole)
            throw new UnauthorizedAccessException($"Requires at least {minimumRole} role");
    }

    public async Task<Guid> GetProjectWorkspaceIdAsync(Guid projectId, CancellationToken ct = default)
    {
        var project = await _db.Projects.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == projectId, ct)
            ?? throw new KeyNotFoundException($"Project {projectId} not found");
        return project.WorkspaceId;
    }

    public async Task RequireProjectRoleAsync(Guid projectId, string userId, WorkspaceRole minimumRole, CancellationToken ct = default)
    {
        var workspaceId = await GetProjectWorkspaceIdAsync(projectId, ct);
        await RequireRoleAsync(workspaceId, userId, minimumRole, ct);
    }
}
