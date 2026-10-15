using System.Text.RegularExpressions;
using Vellum.Modules.GitHub;
using Vellum.Shared.Outbound;
using static Vellum.Modules.GitHosts.GitHostText;

namespace Vellum.Modules.GitHosts.GitLab;

/// <summary>
/// The reads of a GitLab instance (API v4) for one workspace. A project is named by its full
/// path, <c>group/subgroup/name</c>. GitLab names the blob SHA of each file, so a file is read
/// by that SHA. A rate limit is a 429 with <c>Retry-After</c> and <c>RateLimit-Reset</c>.
/// </summary>
internal sealed partial class GitLabConnection(IGitLabApi api, GitHostContext context) : IGitHostConnection
{
    private const int MaxTreePages = MaxEntries / 100;

    private readonly IGitLabApi _api = api;
    private readonly GitHostSender _send = new(context, "gitlab", api.Follow, "RateLimit-Reset");

    public GitHostInfo Host => GitHostInfo.Gitlab;

    public bool HasToken => _send.HasToken;

    public Task<GitHubResult<GitHostRepository>> GetRepositoryAsync(string locator, CancellationToken ct) =>
        Project(locator) is not { } project
            ? NotFound<GitHostRepository>()
            : _send.SendAsync<GitHostRepository>("get-project", call => _api.GetProject(project, call, ct), async response =>
            {
                if (await OutboundBody.ReadJsonAsync<GitLabProjectDto>(response, ct) is not { } dto)
                    return Unexpected;
                // An empty project has no default branch, and so nothing to read.
                if (dto.DefaultBranch is null)
                    return new GitHubFailure.NotFound();
                return Repository(dto) is { } repository ? repository : Unexpected;
            }, ct);

    public Task<GitHubResult<string>> ResolveRefAsync(string locator, string reference, CancellationToken ct) =>
        Project(locator) is not { } project || !IsRef(reference)
            ? NotFound<string>()
            : _send.SendAsync<string>("resolve-ref", call => _api.GetCommit(project, reference, call, ct), async response =>
                await OutboundBody.ReadJsonAsync<GitLabCommitDto>(response, ct) is { Id: { } sha } && IsSha(sha) ? sha : Unexpected, ct);

    /// <summary>
    /// Every blob of the commit, page by page. Each next page is the address GitLab links to,
    /// followed only on the configured API origin. A list over <see cref="GitHostText.MaxEntries"/>
    /// entries is <see cref="GitHubFailure.Truncated"/>.
    /// </summary>
    public async Task<GitHubResult<IReadOnlyList<GitHubTreeEntry>>> ListFilesAsync(string locator, string commitSha, CancellationToken ct)
    {
        if (Project(locator) is not { } project || !IsSha(commitSha))
            return new GitHubFailure.NotFound();

        var entries = new List<GitHubTreeEntry>();
        Uri? next = null;
        for (var page = 0; ; page++)
        {
            if (page == MaxTreePages)
                return new GitHubFailure.Truncated();
            var address = next;
            var read = await _send.SendAsync<Uri?>("list-tree",
                call => address is null ? _api.ListTree(project, commitSha, call, ct) : _api.Follow(_send.At(call, address), ct),
                async response =>
                {
                    if (await OutboundBody.ReadJsonAsync<List<GitLabTreeEntryDto?>>(response, ct) is not { } listed)
                        return Unexpected;
                    foreach (var entry in listed)
                    {
                        if (entry is not { Type: { } type, Path: { } path, Id: { } sha } || !IsSha(sha))
                            return Unexpected;
                        if (type == "blob")
                            entries.Add(new GitHubTreeEntry(path, "blob", sha, null, Clean(entry.Mode, MaxWordChars)));
                    }
                    return NextLink(response) switch
                    {
                        null => new GitHubResult<Uri?>(null, null),
                        var link when _send.OnOrigin(link) => link,
                        _ => new GitHubFailure.Unavailable("redirect_refused"),
                    };
                }, ct, PageMaxBytes);

            if (read.Failure is GitHubFailure.TooLarge)
                return new GitHubFailure.Truncated();
            if (read.Failure is { } failure)
                return failure;
            if (entries.Count > MaxEntries)
                return new GitHubFailure.Truncated();
            if (read.Value is not { } link)
                return entries;
            next = link;
        }
    }

    /// <summary>The raw bytes of the blob, read by the SHA the list gave. The pipeline cuts a body over <paramref name="maxBytes"/>.</summary>
    public Task<GitHubResult<byte[]>> GetFileAsync(
        string locator, string commitSha, string path, string blobSha, long maxBytes, CancellationToken ct) =>
        Project(locator) is not { } project || !IsSha(blobSha) || maxBytes < 0
            ? NotFound<byte[]>()
            : _send.SendAsync<byte[]>("get-blob", call => _api.GetRawBlob(project, blobSha, call, ct),
                async response => await GitHostSender.ReadBytesAsync(response, ct), ct, maxBytes);

    /// <summary>The user name of the token. Without a token nothing is sent: GitLab has no user for an anonymous read.</summary>
    public async Task<GitHubResult<GitHostIdentity>> IdentifyAsync(CancellationToken ct)
    {
        if (!HasToken)
            return new GitHostIdentity(null);
        return await _send.SendAsync<GitHostIdentity>("get-user", call => _api.GetCurrentUser(call, ct), async response =>
            await OutboundBody.ReadJsonAsync<GitLabUserDto>(response, ct) is { } user && Clean(user.Username, MaxLoginChars) is { } login
                ? new GitHostIdentity(login)
                : Unexpected, ct);
    }

    /// <summary>
    /// One page of the projects of a group and its subgroups, or of a user when no group has
    /// that path. Archived projects and empty projects are left out.
    /// </summary>
    public async Task<GitHubResult<GitHostRepositoryPage>> ListRepositoriesAsync(string owner, int page, CancellationToken ct)
    {
        if (!Host.TryReadOwner(owner, out var group) || page is < 1 or > MaxRepositoryPages)
            return new GitHubFailure.NotFound();

        async Task<GitHubResult<GitHostRepositoryPage>> ReadAsync(HttpResponseMessage response)
        {
            if (await OutboundBody.ReadJsonAsync<List<GitLabProjectDto?>>(response, ct) is not { } listed)
                return Unexpected;
            var repositories = listed.Where(p => p is { Archived: false }).Select(p => Repository(p!)).OfType<GitHostRepository>().ToList();
            // X-Next-Page is empty on the last page. GitLab leaves it out only when it counts no pages.
            var hasMore = response.Headers.TryGetValues("X-Next-Page", out var values)
                ? !string.IsNullOrWhiteSpace(values.FirstOrDefault())
                : listed.Count >= RepositoriesPerPage;
            return new GitHostRepositoryPage(repositories, hasMore);
        }

        var groupPage = await _send.SendAsync("list-group-projects",
            call => _api.ListGroupProjects(group, page, call, ct), ReadAsync, ct, PageMaxBytes);
        if (groupPage.Failure is not GitHubFailure.NotFound || group.Contains('/', StringComparison.Ordinal))
            return groupPage;
        return await _send.SendAsync("list-user-projects",
            call => _api.ListUserProjects(group, page, call, ct), ReadAsync, ct, PageMaxBytes);
    }

    private static string? Project(string locator) =>
        Parts(GitHostInfo.Gitlab, locator) is { } parts ? string.Join('/', parts) : null;

    private static GitHostRepository? Repository(GitLabProjectDto dto) =>
        Clean(dto.PathWithNamespace, MaxNameChars) is { } name && Locator(GitHostInfo.Gitlab, name) is { } locator
        && Clean(dto.DefaultBranch, MaxRefChars) is { } branch
            ? new GitHostRepository(locator, name, branch, dto.Visibility != "public")
            : null;

    /// <summary>The address of the <c>rel="next"</c> link of the <c>Link</c> header, or null when there is none.</summary>
    private static Uri? NextLink(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Link", out var values))
            return null;
        foreach (Match match in LinkPart().Matches(string.Join(',', values)))
        {
            if (match.Groups["rel"].Value.Split(' ').Contains("next", StringComparer.OrdinalIgnoreCase)
                && Uri.TryCreate(match.Groups["url"].Value, UriKind.Absolute, out var url))
                return url;
        }
        return null;
    }

    [GeneratedRegex("""<(?<url>[^>]*)>[^,]*?;\s*rel="?(?<rel>[^",;]*)"?""")]
    private static partial Regex LinkPart();
}
