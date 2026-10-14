using Microsoft.EntityFrameworkCore;
using Vellum.Shared;

namespace Vellum.Modules.Workspaces;

public enum WorkspaceRole
{
    Viewer = 0,
    Editor = 1,
    Owner = 2
}

public sealed class WorkspaceAuthorizationService
{
    private readonly AppDbContext _db;

    public WorkspaceAuthorizationService(AppDbContext db) => _db = db;

    public async Task<WorkspaceRole?> GetRoleAsync(Guid workspaceId, string userId, CancellationToken ct = default)
    {
        var membership = await _db.Memberships
            .AsNoTracking()
            .FirstOrDefaultAsync(m => m.WorkspaceId == workspaceId && m.UserId == userId, ct);

        if (membership is null) return null;
        return membership.Role;
    }

    public async Task RequireRoleAsync(Guid workspaceId, string userId, WorkspaceRole minimumRole, CancellationToken ct = default)
    {
        var role = await GetRoleAsync(workspaceId, userId, ct);
        if (role is null || role < minimumRole)
            throw Refused(minimumRole);
    }

    public async Task<Guid> GetProjectWorkspaceIdAsync(Guid projectId, CancellationToken ct = default) =>
        await FindProjectWorkspaceIdAsync(projectId, ct) ?? throw Refused(WorkspaceRole.Viewer);

    public async Task RequireProjectRoleAsync(Guid projectId, string userId, WorkspaceRole minimumRole, CancellationToken ct = default)
    {
        // An unknown project answers exactly as a project the caller cannot reach: the answer
        // never tells whether an ID exists in another tenant.
        var workspaceId = await FindProjectWorkspaceIdAsync(projectId, ct) ?? throw Refused(minimumRole);
        await RequireRoleAsync(workspaceId, userId, minimumRole, ct);
    }

    private async Task<Guid?> FindProjectWorkspaceIdAsync(Guid projectId, CancellationToken ct) =>
        await _db.Projects.AsNoTracking().Where(p => p.Id == projectId).Select(p => (Guid?)p.WorkspaceId).FirstOrDefaultAsync(ct);

    private static UnauthorizedAccessException Refused(WorkspaceRole minimumRole) => new($"Requires at least {minimumRole} role");
}
