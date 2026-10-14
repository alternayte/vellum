using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Vellum.Modules.Identity;
using Vellum.Modules.Identity.AgentTokens;
using Vellum.Modules.Workspaces;
using Vellum.Shared;

namespace Vellum.Modules.Mcp;

/// <summary>
/// The one way an MCP tool reaches a project. A tool names the project in its arguments, so
/// the HTTP boundary cannot check it; every tool call passes here before its tool runs.
/// </summary>
public sealed class McpProjects(AgentAccess access, AppDbContext db, TimeProvider time)
{
    public const string AgentRequired = "An agent token is required.";
    public const string NotFound = "Project not found.";

    /// <summary>
    /// The project, for a caller that is an agent token with <paramref name="scope"/>, whose
    /// workspace holds the project, whose project restriction (when set) names it, and whose
    /// user is still a member of the workspace. Else the refusal as a sentence. A project of
    /// another workspace and a project that does not exist get the same sentence.
    /// </summary>
    public async Task<(ProjectEntity? Project, string? Refusal)> CheckAsync(
        ClaimsPrincipal? user, AgentScope scope, Guid projectId, CancellationToken ct)
    {
        if (user is null || AgentPrincipal.TokenId(user) is not { } tokenId || AgentPrincipal.WorkspaceId(user) is not { } workspaceId)
            return (null, AgentRequired);

        var (refusal, project) = await access.CheckAsync(user, scope, null, projectId, ct);
        if (refusal is null)
            return (project, null);

        await AgentTokenAudit.RefusedAsync(db, tokenId, workspaceId, refusal, "mcp tools/call", time.GetUtcNow(), ct);
        return (null, refusal == AgentAccess.OutsideWorkspace ? NotFound : AgentAccess.Explain(refusal));
    }

    /// <summary>
    /// One page of the projects the token may reach with <paramref name="scope"/>, by name and
    /// then by ID: the one project of a restricted token, else the projects of its workspace.
    /// </summary>
    public async Task<(IReadOnlyList<ProjectEntity>? Projects, string? Refusal)> VisibleAsync(
        ClaimsPrincipal? user, AgentScope scope, (string Name, Guid Id)? after, int take, CancellationToken ct)
    {
        if (user is null || AgentPrincipal.TokenId(user) is not { } tokenId || AgentPrincipal.WorkspaceId(user) is not { } workspaceId)
            return (null, AgentRequired);

        // A restricted token is checked for its project; any other for its workspace.
        var restriction = AgentPrincipal.ProjectId(user);
        var (refusal, _) = await access.CheckAsync(user, scope, restriction is null ? workspaceId : null, restriction, ct);
        if (refusal is not null)
        {
            await AgentTokenAudit.RefusedAsync(db, tokenId, workspaceId, refusal, "mcp tools/call", time.GetUtcNow(), ct);
            return (null, AgentAccess.Explain(refusal));
        }

        var query = db.Projects.AsNoTracking().Where(p => p.WorkspaceId == workspaceId);
        if (restriction is { } only)
            query = query.Where(p => p.Id == only);
        if (after is var (name, id))
            query = query.Where(p => string.Compare(p.Name, name) > 0 || (p.Name == name && p.Id.CompareTo(id) > 0));
        return (await query.OrderBy(p => p.Name).ThenBy(p => p.Id).Take(take).ToListAsync(ct), null);
    }
}
