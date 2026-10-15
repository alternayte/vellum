using Vellum.Modules.GitHosts.AzureDevOps;
using Vellum.Modules.GitHosts.Bitbucket;
using Vellum.Modules.GitHosts.GitLab;
using Vellum.Modules.GitHub;
using Vellum.Modules.Settings;
using Vellum.Shared.Outbound;

namespace Vellum.Modules.GitHosts;

/// <summary>What every git host connection is opened with: the address and the rules of one workspace, and its token.</summary>
/// <param name="BaseUrl">The API base URL from the workspace settings. It passed the instance policy.</param>
/// <param name="Credential">Null when the workspace saved no token; the reads are then anonymous.</param>
public sealed record GitHostContext(
    TimeProvider Time, Uri BaseUrl, bool AllowPrivateNetwork, bool RequireHttps, OutboundCredential? Credential);

/// <summary>
/// Opens a read-only connection to a git host for one workspace. The base URL comes from the
/// workspace settings (<c>gitlab.apiBaseUrl</c>, <c>bitbucket.apiBaseUrl</c>,
/// <c>azureDevops.apiBaseUrl</c>) and passes the instance policy; the token comes from the
/// workspace secret of the host and is sent only to the origin it was saved for.
/// </summary>
public sealed class GitHostReader(IServiceProvider services, SettingsResolver settings, SecretReader secrets)
{
    public async Task<GitHubResult<IGitHostConnection>> ConnectAsync(GitHostInfo host, Guid workspaceId, CancellationToken ct = default)
    {
        var workspace = await settings.GetWorkspaceAsync(workspaceId, ct);
        if (host.ApiBaseUrl(workspace) is not SettingsUrl.Usable destination)
            return new GitHubFailure.Unavailable("url_refused");

        string? token;
        try
        {
            switch (await secrets.ReadAsync(workspaceId, host.SecretName, destination.Url, ct))
            {
                case SecretRead.Found found:
                    token = found.Value;
                    break;
                case SecretRead.SavedForAnotherHost:
                    return new GitHubFailure.Unavailable("token_saved_for_another_host");
                default:
                    token = null;
                    break;
            }
        }
        catch (SecretUnavailableException)
        {
            return new GitHubFailure.Unavailable("secret_unavailable");
        }

        var context = new GitHostContext(
            services.GetRequiredService<TimeProvider>(), destination.Url, workspace.Policy.AllowPrivateNetworkUrls, workspace.RequireHttps,
            token is null ? null : Credential(host, token));
        return Open(host, context);
    }

    /// <summary>
    /// How each host takes its token. GitLab reads <c>PRIVATE-TOKEN</c>. Bitbucket Cloud takes an
    /// access token as a bearer token, and an app password or API token as basic credentials
    /// written <c>user:token</c>. Azure DevOps takes a personal access token as basic
    /// credentials with an empty user name.
    /// </summary>
    internal static OutboundCredential Credential(GitHostInfo host, string token) => host.Kind switch
    {
        GitHostKind.Gitlab => new OutboundCredential("PRIVATE-TOKEN", null, token),
        GitHostKind.Bitbucket when token.Contains(':', StringComparison.Ordinal) =>
            new OutboundCredential("Authorization", "Basic", Convert.ToBase64String(System.Text.Encoding.ASCII.GetBytes(token))),
        GitHostKind.Bitbucket => new OutboundCredential("Authorization", "Bearer", token),
        _ => new OutboundCredential("Authorization", "Basic", Convert.ToBase64String(System.Text.Encoding.ASCII.GetBytes(":" + token))),
    };

    private GitHubResult<IGitHostConnection> Open(GitHostInfo host, GitHostContext context) => new(host.Kind switch
    {
        GitHostKind.Gitlab => (IGitHostConnection)new GitLabConnection(services.GetRequiredService<IGitLabApi>(), context),
        GitHostKind.Bitbucket => new BitbucketConnection(services.GetRequiredService<IBitbucketApi>(), context),
        _ => new AzureDevOpsConnection(services.GetRequiredService<IAzureDevOpsApi>(), context),
    }, null);
}
