using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Vellum.Modules.Workspaces;
using Vellum.Modules.Workspaces.Authorization;
using Vellum.Modules.Workspaces.Entities;
using Vellum.Shared;

namespace Vellum.Modules.Identity.AgentTokens;

/// <summary>
/// Endpoint metadata: an agent token may call the endpoint. With scopes, the token needs one
/// of them and the route's <c>{workspaceId}</c> or <c>{projectId}</c> must be inside the
/// token's workspace and project restriction. Without a scope the endpoint checks each call
/// itself (the MCP endpoint: its tools name the project in the message, not in the route) or
/// gives nothing of a workspace (the agent reference).
/// </summary>
public sealed record AgentAllowed(IReadOnlyList<AgentScope> Scopes);

/// <summary>Endpoint metadata: only a person signed in to Vellum calls the endpoint.</summary>
public sealed class HumanOnly
{
    public static readonly HumanOnly Instance = new();

    private HumanOnly() { }
}

/// <summary>
/// The boundary between a person and an agent. An agent token is refused everywhere, unless
/// the endpoint opts in with <see cref="AllowAgent{TBuilder}"/>. A human-only endpoint
/// refuses every caller that the session cookie did not authenticate.
/// </summary>
public static class AgentBoundary
{
    public const string HumanOnlyError = "human_only";
    public const string AgentNotAllowedError = "agent_not_allowed";

    /// <summary>Opens the endpoint to an agent token that has <paramref name="scope"/>, or one of <paramref name="or"/>.</summary>
    public static TBuilder AllowAgent<TBuilder>(this TBuilder builder, AgentScope scope, params AgentScope[] or) where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(new AgentAllowed([scope, .. or]));

    /// <summary>
    /// Opens the endpoint to an agent token of any scope, beside the callers it already has.
    /// For an endpoint that names no workspace and no project and gives nothing of either.
    /// </summary>
    public static TBuilder AllowAnyAgent<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(new AgentAllowed([]));

    /// <summary>
    /// The endpoint takes agent tokens and nothing else: a session cookie gets 401. The
    /// endpoint checks the scope and the project of each call with <see cref="AgentAccess"/>.
    /// </summary>
    public static TBuilder RequireAgent<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder =>
        builder
            .RequireAuthorization(new AuthorizationPolicyBuilder(AgentPrincipal.Scheme).RequireAuthenticatedUser().Build())
            .WithMetadata(new AgentAllowed([]));

    /// <summary>
    /// Only a person signed in to Vellum calls the endpoint. No credential gets 401. An agent
    /// token, with any scope and any role behind it, gets 403 <c>human_only</c>.
    /// </summary>
    public static TBuilder RequireHuman<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder =>
        builder.RequireAuthorization().WithMetadata(HumanOnly.Instance);

    /// <summary>Runs after authentication and authorization, before any endpoint.</summary>
    public static IApplicationBuilder UseAgentBoundary(this IApplicationBuilder app) => app.Use(async (http, next) =>
    {
        var endpoint = http.GetEndpoint();
        var agent = AgentPrincipal.IsAgent(http.User);

        // A bearer credential that did not authenticate is refused. The request does not
        // continue as an anonymous request or as the session of a cookie beside it.
        if (!agent && AgentTokenSecret.Presented(http.Request))
        {
            await http.ChallengeAsync(AgentPrincipal.Scheme);
            return;
        }

        if (endpoint?.Metadata.GetMetadata<HumanOnly>() is not null && !AgentPrincipal.IsHuman(http))
        {
            if (http.User.Identity is not { IsAuthenticated: true })
            {
                await http.ChallengeAsync();
                return;
            }

            await RefuseAsync(http, "human-only", new ErrorResponse(HumanOnlyError,
                "Only a person signed in to Vellum does this",
                "Sign in to the app and do it there. An agent token cannot, whatever scopes it has and whatever role its user has."));
            return;
        }

        if (!agent)
        {
            await next(http);
            return;
        }

        if (endpoint?.Metadata.GetMetadata<AgentAllowed>() is not { } allowed)
        {
            await RefuseAsync(http, "not-open-to-agents", NotAllowed("This endpoint is not open to agent tokens."));
            return;
        }

        var access = http.RequestServices.GetRequiredService<AgentAccess>();
        string? refusal;
        if (allowed.Scopes.Count > 0)
        {
            // The scope the token has, or the first one, which then is the scope it lacks.
            var scope = allowed.Scopes.Where(s => AgentPrincipal.HasScope(http.User, s)).DefaultIfEmpty(allowed.Scopes[0]).First();

            // The route names what the call works on. A value that is present and is not an
            // ID, and a route that names neither, are outside every token.
            var workspace = RouteId(http, "workspaceId");
            var project = RouteId(http, "projectId");
            refusal = workspace.Invalid || project.Invalid || (workspace.Id is null && project.Id is null)
                ? AgentAccess.OutsideWorkspace
                : (await access.CheckAsync(http.User, scope, workspace.Id, project.Id, http.RequestAborted)).Refusal;
        }
        else
        {
            refusal = await access.CheckMembershipAsync(http.User, http.RequestAborted);
        }

        if (refusal is not null)
        {
            await RefuseAsync(http, refusal, NotAllowed(AgentAccess.Explain(refusal)));
            return;
        }

        await next(http);
    });

    private static ErrorResponse NotAllowed(string detail) =>
        new(AgentNotAllowedError, "This agent token cannot do this", detail);

    private static (Guid? Id, bool Invalid) RouteId(HttpContext http, string name) =>
        http.GetRouteValue(name) is not { } value ? (null, false)
        : Guid.TryParse(value.ToString(), out var id) ? (id, false)
        : (null, true);

    private static async Task RefuseAsync(HttpContext http, string reason, ErrorResponse error)
    {
        if (AgentPrincipal.TokenId(http.User) is { } tokenId && AgentPrincipal.WorkspaceId(http.User) is { } workspaceId)
        {
            await AgentTokenAudit.RefusedAsync(
                http.RequestServices.GetRequiredService<AppDbContext>(), tokenId, workspaceId, reason,
                AgentTokenAudit.Describe(http), http.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow(), http.RequestAborted);
        }

        await Results.Json(error, statusCode: StatusCodes.Status403Forbidden).ExecuteAsync(http);
    }
}

/// <summary>
/// The one check of what an agent token may reach: its scope, its workspace, its project
/// restriction, and the role its user has in the workspace today. The HTTP boundary and the
/// MCP tools both ask here.
/// </summary>
public sealed class AgentAccess(AppDbContext workspaces, WorkspaceAuthorizationService roles)
{
    public const string MissingScope = "scope";
    public const string OutsideWorkspace = "workspace";
    public const string OutsideProject = "project";
    public const string NoMembership = "membership";

    public static string Explain(string refusal) => refusal switch
    {
        MissingScope => "The token does not have the scope this needs.",
        OutsideProject => "The token is restricted to another project.",
        NoMembership => "The user who created the token is no longer a member of the workspace.",
        _ => "This is outside the workspace of the token.",
    };

    /// <summary>
    /// Null when the token may work with <paramref name="scope"/> on the named workspace or
    /// project. A project that does not exist is outside every workspace: the answer does not
    /// say whether it exists.
    /// </summary>
    public async Task<(string? Refusal, ProjectEntity? Project)> CheckAsync(
        ClaimsPrincipal agent, AgentScope scope, Guid? workspaceId, Guid? projectId, CancellationToken ct)
    {
        if (AgentPrincipal.WorkspaceId(agent) is not { } tokenWorkspace)
            return (OutsideWorkspace, null);
        if (!AgentPrincipal.HasScope(agent, scope))
            return (MissingScope, null);
        if (workspaceId is not null && workspaceId != tokenWorkspace)
            return (OutsideWorkspace, null);

        ProjectEntity? project = null;
        var restriction = AgentPrincipal.ProjectId(agent);
        if (projectId is not null)
        {
            project = await workspaces.Projects.AsNoTracking().FirstOrDefaultAsync(p => p.Id == projectId, ct);
            if (project is null || project.WorkspaceId != tokenWorkspace)
                return (OutsideWorkspace, null);
            if (restriction is not null && restriction != projectId)
                return (OutsideProject, null);
        }
        else if (restriction is not null)
        {
            // A token for one project does nothing that is for the whole workspace.
            return (OutsideProject, null);
        }

        return await CheckMembershipAsync(agent, ct) is { } refusal ? (refusal, null) : (null, project);
    }

    /// <summary>
    /// A token acts for the user who created it. When that user leaves the workspace, the
    /// token stops; a lower role gives the token that lower role, through the role check of
    /// each endpoint.
    /// </summary>
    public async Task<string?> CheckMembershipAsync(ClaimsPrincipal agent, CancellationToken ct) =>
        AgentPrincipal.WorkspaceId(agent) is { } tokenWorkspace
        && agent.FindFirstValue(ClaimTypes.NameIdentifier) is { } userId
        && await roles.GetRoleAsync(tokenWorkspace, userId, ct) is not null
            ? null
            : NoMembership;
}
