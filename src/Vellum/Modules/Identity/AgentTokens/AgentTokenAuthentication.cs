using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using Vellum.Modules.Settings;
using Vellum.Modules.Settings.Audit;

namespace Vellum.Modules.Identity.AgentTokens;

/// <summary>
/// Authenticates <c>Authorization: Bearer vlm_agt_…</c>. A token authenticates while it is
/// not revoked and not expired and its digest matches; nothing else about the request counts.
/// </summary>
internal sealed class AgentTokenAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    AppIdentityDbContext db,
    Bootstrap bootstrap,
    TimeProvider clock)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!AgentTokenSecret.Presented(Request))
            return AuthenticateResult.NoResult();
        if (AgentTokenSecret.Read(Request) is not { } token || !AgentTokenSecret.TryParse(token, out var tokenId))
            return AuthenticateResult.Fail("The bearer credential is not an agent token.");

        var ct = Context.RequestAborted;
        var row = await db.AgentTokens.AsNoTracking().FirstOrDefaultAsync(t => t.Id == tokenId, ct);
        if (row is null)
            return AuthenticateResult.Fail("The agent token is not known.");

        // The digest is computed under the key the row names. A row under a key that is no
        // longer loaded never matches.
        var keys = bootstrap.Keys;
        if (AgentTokenSecret.Digest(keys, row.MasterKeyId, token) is not { } digest
            || !AgentTokenSecret.Matches(row.TokenHash, digest))
            return AuthenticateResult.Fail("The agent token is not known.");

        // From here the caller holds the real token, so a refusal is a refused use of it.
        var now = clock.GetUtcNow();
        if (row.RevokedAt is not null)
            return await RefuseAsync(row, "revoked", now, ct);
        if (row.ExpiresAt <= now)
            return await RefuseAsync(row, "expired", now, ct);

        if (!keys.IsCurrent(row.MasterKeyId))
        {
            // The master key changed. The token is in hand, so its digest moves to the current key.
            var current = AgentTokenSecret.Digest(keys, keys.Current.Id, token)!;
            var currentId = keys.Current.Id;
            await db.Database.ExecuteSqlAsync(
                $"UPDATE identity.agent_tokens SET token_hash = {current}, master_key_id = {currentId} WHERE id = {row.Id} AND master_key_id = {row.MasterKeyId}",
                ct);
        }

        if (row.LastUsedAt is null || now - row.LastUsedAt >= AgentTokenRules.LastUsedInterval)
        {
            // The condition repeats in the statement, so requests that arrive together write once.
            var before = now - AgentTokenRules.LastUsedInterval;
            await db.Database.ExecuteSqlAsync(
                $"UPDATE identity.agent_tokens SET last_used_at = {now} WHERE id = {row.Id} AND (last_used_at IS NULL OR last_used_at <= {before})",
                ct);
        }

        return AuthenticateResult.Success(new AuthenticationTicket(AgentPrincipal.Create(row), Scheme.Name));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.Append(HeaderNames.WWWAuthenticate, "Bearer");
        return Task.CompletedTask;
    }

    private async Task<AuthenticateResult> RefuseAsync(AgentTokenEntity row, string reason, DateTimeOffset now, CancellationToken ct)
    {
        await AgentTokenAudit.RefusedAsync(db, row.Id, row.WorkspaceId, reason, AgentTokenAudit.Describe(Context), now, ct);
        return AuthenticateResult.Fail($"The agent token is {reason}.");
    }
}

/// <summary>
/// The audit entries of agent tokens, in the audit log of the Settings module. An entry names
/// the token by its ID and never holds the token, its digest or its name.
/// </summary>
internal static class AgentTokenAudit
{
    public const string Created = "agent-token.created";
    public const string Revoked = "agent-token.revoked";
    public const string Deleted = "agent-token.deleted";
    public const string Refused = "agent-token.refused";

    /// <summary>The method and the route pattern of the request: text the server chose, not the caller.</summary>
    public static string Describe(HttpContext http) =>
        $"{http.Request.Method} {(http.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? "(no endpoint)"}";

    /// <summary>
    /// Records a refused use, at most once in <see cref="AgentTokenRules.RefusedAuditInterval"/>
    /// for one token: a client that repeats a refused request does not fill the audit log.
    /// The first statement claims the interval, so requests that arrive together write once.
    /// </summary>
    public static async Task RefusedAsync(
        DbContext db, Guid tokenId, Guid workspaceId, string reason, string what, DateTimeOffset now, CancellationToken ct)
    {
        var before = now - AgentTokenRules.RefusedAuditInterval;
        var claimed = await db.Database.ExecuteSqlAsync(
            $"UPDATE identity.agent_tokens SET last_refused_at = {now} WHERE id = {tokenId} AND (last_refused_at IS NULL OR last_refused_at <= {before})",
            ct);
        if (claimed == 1)
            await AuditLog.AppendAsync(db, workspaceId, $"agent-token:{tokenId}", Refused, $"agent-token:{tokenId} reason:{reason} at:{what}", ct);
    }
}

public static class AgentTokenAuthentication
{
    /// <summary>
    /// The scheme every request starts with. It picks one scheme for the request and never
    /// two: agent tokens when the request presents a bearer credential, the session cookie
    /// otherwise. So a bearer request is an agent request even when a cookie comes with it.
    /// </summary>
    public const string SelectorScheme = "Vellum";

    public static AuthenticationBuilder AddAgentTokens(this AuthenticationBuilder builder)
    {
        builder.AddScheme<AuthenticationSchemeOptions, AgentTokenAuthenticationHandler>(AgentPrincipal.Scheme, null);
        builder.AddPolicyScheme(SelectorScheme, null, options => options.ForwardDefaultSelector = http =>
            AgentTokenSecret.Presented(http.Request) ? AgentPrincipal.Scheme : IdentityConstants.ApplicationScheme);

        // Identity made the cookie scheme the default. The selector replaces it for
        // authenticate, challenge and forbid; sign-in and sign-out name their scheme themselves.
        builder.Services.Configure<AuthenticationOptions>(options =>
        {
            options.DefaultAuthenticateScheme = SelectorScheme;
            options.DefaultChallengeScheme = SelectorScheme;
            options.DefaultForbidScheme = SelectorScheme;
        });

        builder.Services.AddScoped<AgentAccess>();
        builder.Services.TryAddSingleton(TimeProvider.System);
        return builder;
    }
}
