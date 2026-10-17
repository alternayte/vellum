using Vellum.Modules.Settings;
using Vellum.Shared;

namespace Vellum.Modules.Workspaces;

/// <summary>The workspace and the project that a new account gets.</summary>
public sealed record StarterIds(Guid WorkspaceId, Guid ProjectId);

/// <summary>
/// A new account starts in an empty project of its own workspace, so the first screen after
/// setup or registration is the project, not a form for a workspace and a project. The person
/// renames them and adds more from the project menu.
/// </summary>
public static class StarterWorkspace
{
    public const string WorkspaceName = "My workspace";
    public const string ProjectName = "My system";

    /// <summary>
    /// Adds the workspace with <paramref name="userId"/> as its owner, and an empty project in it,
    /// to <paramref name="db"/>, and appends the audit entry of the owner. The caller saves the
    /// rows in its own transaction, so the account, the workspace and the project commit together.
    /// </summary>
    public static async Task<StarterIds> AddAsync(
        AppDbContext db, string userId, string? workspaceName, TimeProvider time, CancellationToken ct)
    {
        var ids = new StarterIds(Guid.NewGuid(), Guid.NewGuid());
        db.Workspaces.Add(new WorkspaceEntity
        {
            Id = ids.WorkspaceId,
            Name = string.IsNullOrWhiteSpace(workspaceName) ? WorkspaceName : workspaceName.Trim(),
            CreatedBy = userId,
        });
        db.Memberships.Add(new MembershipEntity
        {
            Id = Guid.NewGuid(),
            WorkspaceId = ids.WorkspaceId,
            UserId = userId,
            Role = WorkspaceRole.Owner,
            CreatedAt = time.GetUtcNow(),
        });
        db.Projects.Add(new ProjectEntity
        {
            Id = ids.ProjectId,
            WorkspaceId = ids.WorkspaceId,
            Name = ProjectName,
            StreamId = Guid.NewGuid(),
        });
        await db.SaveChangesAsync(ct);
        await AuditLog.AppendAsync(db, ids.WorkspaceId, userId, AuditLog.MemberAdded, $"user:{userId} role:owner", ct);
        return ids;
    }
}
