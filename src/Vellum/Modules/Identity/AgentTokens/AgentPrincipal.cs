using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;

namespace Vellum.Modules.Identity.AgentTokens;

/// <summary>
/// Tells an agent from a person. The answer comes from the authentication scheme that made
/// the principal, which only the server sets: never from a request body, a header a caller
/// chooses, or a role.
/// </summary>
public static class AgentPrincipal
{
    /// <summary>The authentication scheme of agent tokens, and the authentication type of the identity it makes.</summary>
    public const string Scheme = "AgentToken";

    public const string TokenIdClaim = "vellum:agent-token-id";
    public const string WorkspaceIdClaim = "vellum:agent-workspace-id";
    public const string ProjectIdClaim = "vellum:agent-project-id";
    public const string ScopeClaim = "vellum:agent-scope";

    /// <summary>
    /// The principal of an agent token. <see cref="ClaimTypes.NameIdentifier"/> is the user who
    /// created the token, so every role check asks for that user's role today.
    /// </summary>
    internal static ClaimsPrincipal Create(AgentTokenEntity token)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, token.CreatedBy),
            new(TokenIdClaim, token.Id.ToString()),
            new(WorkspaceIdClaim, token.WorkspaceId.ToString()),
        };
        claims.AddRange(token.Scopes.Select(scope => new Claim(ScopeClaim, scope)));
        if (token.ProjectId is { } projectId)
            claims.Add(new Claim(ProjectIdClaim, projectId.ToString()));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme));
    }

    /// <summary>True when an agent token authenticated the principal.</summary>
    public static bool IsAgent(ClaimsPrincipal user) => Identity(user) is not null;

    public static Guid? TokenId(ClaimsPrincipal user) => Id(user, TokenIdClaim);

    /// <summary>The one workspace the token works in.</summary>
    public static Guid? WorkspaceId(ClaimsPrincipal user) => Id(user, WorkspaceIdClaim);

    /// <summary>The one project the token is restricted to, or null when it has no restriction.</summary>
    public static Guid? ProjectId(ClaimsPrincipal user) => Id(user, ProjectIdClaim);

    public static bool HasScope(ClaimsPrincipal user, AgentScope scope) =>
        Identity(user)?.HasClaim(ScopeClaim, AgentScopes.Name(scope)) ?? false;

    /// <summary>
    /// Who caused an event: <c>agent-token:{tokenId}</c> for an agent, <c>user:{userId}</c>
    /// for a person, null for a request with no credential.
    /// </summary>
    public static string? Actor(ClaimsPrincipal user) =>
        TokenId(user) is { } tokenId ? $"agent-token:{tokenId}"
        : user.FindFirstValue(ClaimTypes.NameIdentifier) is { } userId ? $"user:{userId}"
        : null;

    /// <summary>
    /// True for a person signed in to Vellum: the session cookie scheme authenticated the
    /// request, and the request presents no bearer credential. A request with a bearer
    /// credential is never a person's request, whatever cookie comes with it.
    /// </summary>
    public static bool IsHuman(HttpContext http) =>
        http.User.Identity is { IsAuthenticated: true }
        && !IsAgent(http.User)
        && !AgentTokenSecret.Presented(http.Request)
        && http.Features.Get<IAuthenticateResultFeature>()?.AuthenticateResult
            is { Succeeded: true, Ticket.AuthenticationScheme: { } scheme }
        && scheme == IdentityConstants.ApplicationScheme;

    private static ClaimsIdentity? Identity(ClaimsPrincipal user) =>
        user.Identities.FirstOrDefault(identity => identity.AuthenticationType == Scheme);

    private static Guid? Id(ClaimsPrincipal user, string claimType) =>
        Guid.TryParse(Identity(user)?.FindFirst(claimType)?.Value, out var id) ? id : null;
}
