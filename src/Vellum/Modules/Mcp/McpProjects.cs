using System.Security.Claims;
using ModelContextProtocol;
using Vellum.Modules.Identity;
using Vellum.Modules.Identity.AgentTokens;
using Vellum.Modules.Workspaces.Entities;

namespace Vellum.Modules.Mcp;

/// <summary>
/// The one way an MCP tool reaches a project. A tool names the project in its arguments, so
/// the HTTP boundary cannot check it; every tool asks here before it reads anything.
/// </summary>
public sealed class McpProjects(AgentAccess access, AppIdentityDbContext identity, TimeProvider time)
{
    /// <summary>
    /// The project, for a caller that is an agent token with the <c>read</c> scope, whose
    /// workspace holds the project, whose project restriction (when set) names it, and whose
    /// user is still a member of the workspace.
    /// </summary>
    /// <exception cref="McpException">
    /// The token may not read the project. The tool call ends as an error result. A project
    /// of another workspace and a project that does not exist get the same message.
    /// </exception>
    public async Task<ProjectEntity> ReadAsync(ClaimsPrincipal? user, Guid projectId, CancellationToken ct)
    {
        if (user is null || AgentPrincipal.TokenId(user) is not { } tokenId || AgentPrincipal.WorkspaceId(user) is not { } workspaceId)
            throw new McpException("An agent token is required.");

        var (refusal, project) = await access.CheckAsync(user, AgentScope.Read, null, projectId, ct);
        if (refusal is null)
            return project!;

        await AgentTokenAudit.RefusedAsync(identity, tokenId, workspaceId, refusal, "mcp tools/call", time.GetUtcNow(), ct);
        throw new McpException(refusal == AgentAccess.OutsideWorkspace ? "Project not found." : AgentAccess.Explain(refusal));
    }
}
