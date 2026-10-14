using System.Security.Claims;
using Vellum.Modules.Workspaces.Authorization;
using Vellum.Shared.Outbound;

namespace Vellum.Modules.GitHub;

public static class GitHubModule
{
    public static IServiceCollection AddGitHubModule(this IServiceCollection services)
    {
        services.AddOutboundClient<IGitHubApi>();
        services.AddSingleton<EnvironmentGitHubToken>();
        services.AddScoped<GitHubReader>();
        return services;
    }

    public static WebApplication MapGitHubEndpoints(this WebApplication app)
    {
        app.MapPost("/api/workspaces/{workspaceId}/github/test", TestGitHubConnection.Handle)
            .WithTags("GitHub")
            .RequireAuthorization()
            .Produces<GitHubTestResponse>();
        app.MapGet("/api/workspaces/{workspaceId}/github/credential", GetGitHubCredential.Handle)
            .WithTags("GitHub")
            .RequireAuthorization()
            .Produces<GitHubCredentialResponse>();
        return app;
    }
}

/// <summary>
/// The result of one real call to the GitHub API of the workspace. A failure on GitHub's side
/// is a 200 with <c>ok: false</c>; the error says what to change. <c>AuthenticatedAs</c> is
/// GitHub's text: it is at most 100 characters and holds no control character. The error is
/// a fixed sentence and holds no text from GitHub.
/// </summary>
public sealed record GitHubTestResponse(bool Ok, string? AuthenticatedAs, int? RateLimitRemaining, ConnectionTestError? Error);

/// <summary>
/// Which token the GitHub reads of the workspace send: <c>workspace</c>, <c>gh</c> (the
/// <c>GH_TOKEN</c> of a local instance) or <c>anonymous</c>. <c>GhNotUsed</c> says why a set
/// <c>GH_TOKEN</c> does not apply here. <c>Login</c> is the GitHub account of a <c>gh</c>
/// token, as GitHub names it; null when GitHub did not answer. The token is never in it.
/// </summary>
public sealed record GitHubCredentialResponse(string Source, string? GhNotUsed, string? Login);

public static class GetGitHubCredential
{
    public static async Task<IResult> Handle(
        Guid workspaceId,
        ClaimsPrincipal user,
        WorkspaceAuthorizationService auth,
        GitHubReader reader,
        CancellationToken ct)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier)!;
        await auth.RequireRoleAsync(workspaceId, userId, WorkspaceRole.Owner, ct);

        if (await reader.DescribeCredentialAsync(workspaceId, ct) is not { } credential)
            return Results.Ok(new GitHubCredentialResponse("anonymous", null, null));

        string? login = null;
        if (credential.Source == "gh" && await reader.ConnectAsync(workspaceId, ct) is { Value: { } connection })
            login = (await connection.IdentifyAsync(ct)).Value?.Login;
        return Results.Ok(new GitHubCredentialResponse(credential.Source, credential.GhNotUsed, login));
    }
}

public static class TestGitHubConnection
{
    public static async Task<IResult> Handle(
        Guid workspaceId,
        ClaimsPrincipal user,
        WorkspaceAuthorizationService auth,
        GitHubReader reader,
        CancellationToken ct)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier)!;
        await auth.RequireRoleAsync(workspaceId, userId, WorkspaceRole.Owner, ct);

        var connection = await reader.ConnectAsync(workspaceId, ct);
        if (connection.Failure is { } notConnected)
            return Results.Ok(new GitHubTestResponse(false, null, null, Describe(notConnected)));

        var identity = await connection.Value!.IdentifyAsync(ct);
        return Results.Ok(identity.Failure is { } failure
            ? new GitHubTestResponse(false, null, null, Describe(failure))
            : new GitHubTestResponse(true, identity.Value!.Login, identity.Value.RateLimitRemaining, null));
    }

    internal static ConnectionTestError Describe(GitHubFailure failure) => failure switch
    {
        GitHubFailure.Unauthorized => new("unauthorized", "GitHub refused the token. Save a new token in the workspace secrets."),
        GitHubFailure.Forbidden => new("forbidden", "The token is valid, and GitHub does not allow it to make this call. Check the token's permissions."),
        GitHubFailure.NotFound => new("not_found", "The GitHub API base URL has no REST API at that address. Check github.apiBaseUrl."),
        GitHubFailure.RateLimited limited => new("rate_limited", limited.ResetAt is { } at
            ? $"GitHub's rate limit is used up. It resets at {at.UtcDateTime:yyyy-MM-dd HH:mm:ss} UTC."
            : "GitHub's rate limit is used up. Try again later."),
        GitHubFailure.Unavailable { Reason: "url_refused" } => new("url_refused", "The instance policy refuses the GitHub API base URL. It must be a public http or https address."),
        GitHubFailure.Unavailable { Reason: "invalid_setting" } => new("invalid_setting", "The stored token or the GitHub API base URL holds a line break, another control character, or (the token) a character outside ASCII. Nothing was sent. Save the token again and check github.apiBaseUrl."),
        GitHubFailure.Unavailable { Reason: "token_saved_for_another_host" } => new("token_saved_for_another_host", "The token was saved for another host. Enter it again to use it with the current github.apiBaseUrl."),
        GitHubFailure.Unavailable { Reason: "timeout" } => new("timeout", "GitHub did not answer in time."),
        GitHubFailure.Unavailable { Reason: "network" } => new("network", "No connection to the GitHub API base URL could be made."),
        GitHubFailure.Unavailable { Reason: "secret_unavailable" } => new("secret_unavailable", "The stored token cannot be decrypted with the loaded master key. Save the token again."),
        GitHubFailure.Unavailable { Reason: "redirect_refused" } => new("redirect_refused", "The GitHub API base URL redirects to another address. Set github.apiBaseUrl to the final address."),
        GitHubFailure.Unavailable { Reason: "server_error" } => new("server_error", "GitHub reported a server error. Try again later."),
        _ => new("unexpected_response", "The answer was not a GitHub API answer. Check github.apiBaseUrl."),
    };
}
