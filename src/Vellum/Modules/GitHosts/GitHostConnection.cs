using System.Globalization;
using System.Text.RegularExpressions;
using Vellum.Modules.GitHub;
using Vellum.Modules.Settings;

namespace Vellum.Modules.GitHosts;

/// <summary>The git hosts other than GitHub that a Source reads through a REST adapter.</summary>
public enum GitHostKind
{
    Gitlab,
    Bitbucket,
    AzureDevops,
}

/// <param name="Locator">The repository in the form a Source keeps it: lower case, parts joined by <c>/</c>.</param>
/// <param name="DefaultBranch">The branch a scan without a ref reads. Untrusted text, cut and cleared.</param>
/// <param name="Name">The name as the host writes it, with its case. For display only.</param>
public sealed record GitHostRepository(string Locator, string Name, string DefaultBranch, bool IsPrivate);

/// <param name="HasMore">True when another page follows.</param>
public sealed record GitHostRepositoryPage(IReadOnlyList<GitHostRepository> Repositories, bool HasMore);

/// <param name="Login">The account the token belongs to, as the host names it. Null when the workspace has no token.</param>
public sealed record GitHostIdentity(string? Login);

/// <summary>
/// The reads of one git host for one workspace. Every method is a read; nothing is written to
/// the host. The failures are the ones a GitHub read gives (<see cref="GitHubFailure"/>), so a
/// scan reports a rate limit, an expired token or a missing repository the same way for every
/// host. Paths and file content are returned as they are: they are keys and evidence, and a
/// caller treats them as untrusted data.
/// </summary>
public interface IGitHostConnection
{
    GitHostInfo Host { get; }

    bool HasToken { get; }

    Task<GitHubResult<GitHostRepository>> GetRepositoryAsync(string locator, CancellationToken ct);

    /// <summary>A branch, a tag or a commit SHA as the immutable commit SHA it names now.</summary>
    Task<GitHubResult<string>> ResolveRefAsync(string locator, string reference, CancellationToken ct);

    /// <summary>
    /// Every file of the commit, as git tree entries of type <c>blob</c>. The SHA is the git blob
    /// SHA; it is empty when the host names none, and the scan then computes it from the bytes.
    /// The size is null when the host gives none. A list that the host cut is
    /// <see cref="GitHubFailure.Truncated"/>: a part of a tree reads like a whole one.
    /// </summary>
    Task<GitHubResult<IReadOnlyList<GitHubTreeEntry>>> ListFilesAsync(string locator, string commitSha, CancellationToken ct);

    /// <summary>
    /// The bytes of one file of the commit, or <see cref="GitHubFailure.TooLarge"/> when it is
    /// over <paramref name="maxBytes"/>. <paramref name="blobSha"/> is the SHA the list gave, or empty.
    /// </summary>
    Task<GitHubResult<byte[]>> GetFileAsync(string locator, string commitSha, string path, string blobSha, long maxBytes, CancellationToken ct);

    /// <summary>Who the token belongs to. It is the "Test connection" call.</summary>
    Task<GitHubResult<GitHostIdentity>> IdentifyAsync(CancellationToken ct);

    /// <summary>
    /// One page of the repositories of a group, a workspace or an organization (GitLab group,
    /// Bitbucket workspace, Azure DevOps organization or <c>organization/project</c>), for bulk add.
    /// <paramref name="page"/> counts from 1.
    /// </summary>
    Task<GitHubResult<GitHostRepositoryPage>> ListRepositoriesAsync(string owner, int page, CancellationToken ct);
}

/// <summary>What Vellum knows of one git host: its names, its setting, its secret and the form of its repository names.</summary>
public sealed partial class GitHostInfo
{
    public static readonly GitHostInfo Gitlab = new(
        GitHostKind.Gitlab, "gitlab", "GitLab", WorkspaceSettings.GitlabField, SecretNames.GitlabToken, 2, 20, "group/name or group/subgroup/name", "group");

    public static readonly GitHostInfo Bitbucket = new(
        GitHostKind.Bitbucket, "bitbucket", "Bitbucket", WorkspaceSettings.BitbucketField, SecretNames.BitbucketToken, 2, 2, "workspace/repository", "workspace");

    public static readonly GitHostInfo AzureDevops = new(
        GitHostKind.AzureDevops, "azure-devops", "Azure DevOps", WorkspaceSettings.AzureDevopsField, SecretNames.AzureDevopsToken, 3, 3,
        "organization/project/repository", "organization or organization/project");

    public static readonly IReadOnlyList<GitHostInfo> All = [Gitlab, Bitbucket, AzureDevops];

    private GitHostInfo(
        GitHostKind kind, string name, string display, string settingsField, string secretName, int minParts, int maxParts,
        string locatorForm, string ownerForm)
    {
        Kind = kind;
        Name = name;
        Display = display;
        SettingsField = settingsField;
        SecretName = secretName;
        MinParts = minParts;
        MaxParts = maxParts;
        LocatorForm = locatorForm;
        OwnerForm = ownerForm;
    }

    public GitHostKind Kind { get; }
    /// <summary>The Source kind and the route word: <c>gitlab</c>, <c>bitbucket</c>, <c>azure-devops</c>.</summary>
    public string Name { get; }
    public string Display { get; }
    public string SettingsField { get; }
    public string SecretName { get; }
    public int MinParts { get; }
    public int MaxParts { get; }
    public string LocatorForm { get; }
    public string OwnerForm { get; }

    public static GitHostInfo? Of(string? name) => All.FirstOrDefault(h => h.Name == name);

    public SettingsUrl ApiBaseUrl(WorkspaceSettings settings) => Kind switch
    {
        GitHostKind.Gitlab => settings.GitlabApiBaseUrl,
        GitHostKind.Bitbucket => settings.BitbucketApiBaseUrl,
        _ => settings.AzureDevopsApiBaseUrl,
    };

    /// <summary>
    /// The host of the repository pages for an API address, which the Source and its citations
    /// name: <c>gitlab.com</c> for <c>gitlab.com/api/v4</c>, <c>bitbucket.org</c> for
    /// <c>api.bitbucket.org</c>, and the API host itself otherwise.
    /// </summary>
    public string WebHost(Uri apiBaseUrl)
    {
        var host = apiBaseUrl.IdnHost.ToLowerInvariant();
        if (Kind == GitHostKind.Bitbucket && host == "api.bitbucket.org")
            return "bitbucket.org";
        return apiBaseUrl.IsDefaultPort ? host : string.Create(CultureInfo.InvariantCulture, $"{host}:{apiBaseUrl.Port}");
    }

    /// <summary>
    /// Reads a repository name, or the address of its page on <paramref name="host"/>, as the
    /// locator a Source keeps: the parts in lower case joined by <c>/</c>. Azure DevOps
    /// addresses name the repository after <c>_git</c>.
    /// </summary>
    public bool TryReadLocator(string? value, string host, out string locator, out string error)
    {
        locator = "";
        error = $"Give the repository as {LocatorForm}, or as the address of its page on {host}.";
        if (string.IsNullOrWhiteSpace(value) || value.Length > 400 || value.Any(char.IsControl))
            return false;

        var text = value.Trim();
        if (text.Contains("://", StringComparison.Ordinal))
        {
            if (!Uri.TryCreate(text, UriKind.Absolute, out var url) || url.Scheme is not ("https" or "http")
                || url.Query.Length > 0 || url.Fragment.Length > 0)
                return false;
            var urlHost = url.IsDefaultPort ? url.IdnHost : string.Create(CultureInfo.InvariantCulture, $"{url.IdnHost}:{url.Port}");
            if (!string.Equals(urlHost, host, StringComparison.OrdinalIgnoreCase))
            {
                error = $"The address is not on this workspace's {Display} host ({host}).";
                return false;
            }
            // An Azure DevOps clone address carries the user name in front of the host.
            if (url.UserInfo.Length > 0 && Kind != GitHostKind.AzureDevops)
                return false;
            text = Uri.UnescapeDataString(url.AbsolutePath.Trim('/'));
            if (Kind == GitHostKind.AzureDevops)
                text = text.Replace("/_git/", "/", StringComparison.Ordinal);
            if (Kind == GitHostKind.Gitlab && text.IndexOf("/-/", StringComparison.Ordinal) is > 0 and var tail)
                text = text[..tail];
        }

        var parts = text.Split('/');
        if (parts.Length < MinParts || parts.Length > MaxParts)
            return false;
        var last = parts[^1];
        if (last.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            parts[^1] = last[..^4];
        if (!parts.All(IsPart))
            return false;

        locator = string.Join('/', parts).ToLowerInvariant();
        return true;
    }

    /// <summary>Reads the owner of a listing: a group path, a workspace, or an organization with an optional project.</summary>
    public bool TryReadOwner(string? value, out string owner)
    {
        owner = "";
        if (string.IsNullOrWhiteSpace(value) || value.Length > 300 || value.Any(char.IsControl))
            return false;
        var parts = value.Trim().Trim('/').Split('/');
        var max = Kind switch { GitHostKind.Gitlab => 19, GitHostKind.Bitbucket => 1, _ => 2 };
        if (parts.Length > max || !parts.All(IsPart))
            return false;
        owner = string.Join('/', parts);
        return true;
    }

    private bool IsPart(string part) => part is not ("" or "." or "..")
        && (Kind == GitHostKind.AzureDevops ? AzurePart().IsMatch(part) : Part().IsMatch(part));

    [GeneratedRegex(@"\A[A-Za-z0-9_.-]{1,100}\z")]
    private static partial Regex Part();

    // Azure DevOps project and repository names may hold spaces, not at either end.
    [GeneratedRegex(@"\A[A-Za-z0-9_.-](?:[A-Za-z0-9 _.-]{0,98}[A-Za-z0-9_.-])?\z")]
    private static partial Regex AzurePart();
}
