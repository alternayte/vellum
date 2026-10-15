using System.Security.Claims;
using Vellum.Modules.GitHosts.AzureDevOps;
using Vellum.Modules.GitHosts.Bitbucket;
using Vellum.Modules.GitHosts.GitLab;
using Vellum.Modules.GitHub;
using Vellum.Modules.Identity.AgentTokens;
using Vellum.Modules.Workspaces;
using Vellum.Shared;
using Vellum.Shared.Outbound;

namespace Vellum.Modules.GitHosts;

/// <summary>
/// The git hosts other than GitHub: GitLab (cloud and self-managed), Bitbucket Cloud and Azure
/// DevOps Repos, each through a Refit REST adapter on the outbound pipeline. No git protocol
/// client: a Source reads a commit's file list and its files over the REST API.
/// </summary>
public static class GitHostsModule
{
    public static IServiceCollection AddGitHostsModule(this IServiceCollection services)
    {
        services.AddOutboundClient<IGitLabApi>();
        services.AddOutboundClient<IBitbucketApi>();
        services.AddOutboundClient<IAzureDevOpsApi>();
        services.AddScoped<GitHostReader>();
        return services;
    }

    public static WebApplication MapGitHostEndpoints(this WebApplication app)
    {
        app.MapPost("/api/workspaces/{workspaceId}/git-hosts/{host}/test", TestGitHostConnection.Handle)
            .WithTags("Git hosts")
            .RequireHuman()
            .Produces<GitHostTestResponse>()
            .Produces<ErrorResponse>(StatusCodes.Status404NotFound);
        return app;
    }
}

/// <summary>
/// The result of one real call to the API of a git host of the workspace. A failure on the
/// host's side is a 200 with <c>ok: false</c>; the error says what to change.
/// <c>AuthenticatedAs</c> is the host's text: it is at most 100 characters and holds no
/// control character. It is null when the workspace has no token for the host. The error is
/// a fixed sentence and holds no text from the host.
/// </summary>
public sealed record GitHostTestResponse(bool Ok, string? AuthenticatedAs, ConnectionTestError? Error);

public static class TestGitHostConnection
{
    /// <param name="host"><c>gitlab</c>, <c>bitbucket</c> or <c>azure-devops</c>.</param>
    public static async Task<IResult> Handle(
        Guid workspaceId,
        string host,
        ClaimsPrincipal user,
        WorkspaceAuthorizationService auth,
        GitHostReader reader,
        CancellationToken ct)
    {
        var userId = user.UserId();
        await auth.RequireRoleAsync(workspaceId, userId, WorkspaceRole.Owner, ct);
        if (GitHostInfo.Of(host) is not { } info)
            return ResultExtensions.NotFound("Git host");

        var connection = await reader.ConnectAsync(info, workspaceId, ct);
        if (connection.Failure is { } notConnected)
            return Results.Ok(new GitHostTestResponse(false, null, Describe(info, notConnected)));

        var identity = await connection.Value!.IdentifyAsync(ct);
        return Results.Ok(identity.Failure is { } failure
            ? new GitHostTestResponse(false, null, Describe(info, failure))
            : new GitHostTestResponse(true, identity.Value!.Login, null));
    }

    internal static ConnectionTestError Describe(GitHostInfo host, GitHubFailure failure)
    {
        var (name, secret, field) = (host.Display, host.SecretName, host.SettingsField);
        return failure switch
        {
            GitHubFailure.Unauthorized => new("unauthorized", $"{name} refused the token. Save a new token in the workspace secret {secret}."),
            GitHubFailure.Forbidden => new("forbidden", $"The token is valid, and {name} does not allow it to make this call. Check the token's permissions."),
            GitHubFailure.NotFound => new("not_found", $"The {name} API base URL has no REST API at that address. Check {field}."),
            GitHubFailure.RateLimited limited => new("rate_limited", limited.ResetAt is { } at
                ? $"{name}'s rate limit is used up. It resets at {at.UtcDateTime:yyyy-MM-dd HH:mm:ss} UTC."
                : $"{name}'s rate limit is used up. Try again later."),
            GitHubFailure.Unavailable { Reason: "url_refused" } => new("url_refused", $"The instance policy refuses the {name} API base URL, or {field} is empty. It must be a public http or https address."),
            GitHubFailure.Unavailable { Reason: "invalid_setting" } => new("invalid_setting", $"The stored token or the {name} API base URL holds a line break, another control character, or (the token) a character outside ASCII. Nothing was sent. Save {secret} again and check {field}."),
            GitHubFailure.Unavailable { Reason: "token_saved_for_another_host" } => new("token_saved_for_another_host", $"The token was saved for another host. Enter {secret} again to use it with the current {field}."),
            GitHubFailure.Unavailable { Reason: "timeout" } => new("timeout", $"{name} did not answer in time."),
            GitHubFailure.Unavailable { Reason: "network" } => new("network", $"No connection to the {name} API base URL could be made. Check {field}."),
            GitHubFailure.Unavailable { Reason: "secret_unavailable" } => new("secret_unavailable", $"The stored token cannot be decrypted with the loaded master key. Save {secret} again."),
            GitHubFailure.Unavailable { Reason: "redirect_refused" } => new("redirect_refused", $"The {name} API base URL redirects to another address. Set {field} to the final address."),
            GitHubFailure.Unavailable { Reason: "server_error" } => new("server_error", $"{name} reported a server error. Try again later."),
            _ => new("unexpected_response", $"The answer was not a {name} API answer. Check {field}."),
        };
    }
}
