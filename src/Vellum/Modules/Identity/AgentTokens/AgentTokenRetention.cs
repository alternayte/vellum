using Microsoft.EntityFrameworkCore;
using Vellum.Shared;

namespace Vellum.Modules.Identity.AgentTokens;

/// <summary>
/// Deletes the tokens that are restricted to a project, when the project is deleted. A token
/// for one project must not stay behind as a row that names a project that is gone. Each
/// deleted token gets an audit entry in its workspace. It is safe to repeat.
/// </summary>
public sealed class AgentTokenRetention(AppDbContext db) : IProjectRetention
{
    public Task DeleteProjectAsync(DeletedProject project, CancellationToken ct)
    {
        var target = $" project-deleted:{project.Id}";
        return db.Database.ExecuteSqlAsync($"""
            WITH deleted AS (
                DELETE FROM identity.agent_tokens WHERE project_id = {project.Id} RETURNING id, workspace_id)
            INSERT INTO settings.audit_entries (workspace_id, actor_id, action, target)
            SELECT workspace_id, {project.ActorId}, {AgentTokenAudit.Deleted}, 'agent-token:' || id::text || {target} FROM deleted ORDER BY id
            """, ct);
    }
}
