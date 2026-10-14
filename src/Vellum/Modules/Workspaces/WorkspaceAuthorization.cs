using Microsoft.EntityFrameworkCore;
using Vellum.Shared;

namespace Vellum.Modules.Workspaces;

/// <summary>
/// The roles of a workspace member, in ascending order of what they may do. A role check
/// compares the C# values with <c>&lt;</c>. The Postgres enum type lists its labels in
/// alphabetical order, so a query never compares roles in SQL; it reads the role and compares here.
/// A Commenter reads what a Viewer reads and writes comments; only an Editor changes the model.
/// </summary>
public enum WorkspaceRole
{
    Viewer = 0,
    Commenter = 1,
    Editor = 2,
    Owner = 3
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
