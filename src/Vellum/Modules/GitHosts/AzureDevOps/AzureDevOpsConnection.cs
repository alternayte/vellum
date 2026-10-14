using Vellum.Modules.GitHub;
using Vellum.Shared.Outbound;
using static Vellum.Modules.GitHosts.GitHostText;

namespace Vellum.Modules.GitHosts.AzureDevOps;

/// <summary>
/// The reads of Azure DevOps Repos for one workspace. A repository is named
/// <c>organization/project/repository</c>; project and repository names may hold spaces.
/// Azure DevOps answers a token it does not accept with 401, or with 203 and an HTML sign-in
/// page; both are <see cref="GitHubFailure.Unauthorized"/>. A rate limit is a 429 with
/// <c>Retry-After</c> or <c>X-RateLimit-Reset</c>.
/// </summary>
internal sealed class AzureDevOpsConnection(IAzureDevOpsApi api, GitHostContext context) : IGitHostConnection
{
    private const long TreeMaxBytes = 32 * 1024 * 1024;
    private const string Heads = "refs/heads/";
    private const string Tags = "refs/tags/";

    private readonly IAzureDevOpsApi _api = api;
    private readonly GitHostSender _send = new(context, "azure-devops", api.Follow, "X-RateLimit-Reset", nonAuthoritativeIsSignIn: true);

    public GitHostInfo Host => GitHostInfo.AzureDevops;

    public bool HasToken => _send.HasToken;

    public Task<GitHubResult<GitHostRepository>> GetRepositoryAsync(string locator, CancellationToken ct) =>
        Parts(Host, locator) is not [var organization, var project, var repository]
            ? NotFound<GitHostRepository>()
            : _send.SendAsync<GitHostRepository>("get-repository", call => _api.GetRepository(organization, project, repository, call, ct), async response =>
            {
                if (await OutboundBody.ReadJsonAsync<AzureRepositoryDto>(response, ct) is not { } dto)
                    return Unexpected;
                // An empty repository has no default branch, and so nothing to read.
                if (dto.DefaultBranch is null)
                    return new GitHubFailure.NotFound();
                return Repository(organization, dto) is { } found ? found : Unexpected;
            }, ct);

    /// <summary>
    /// A full SHA is read as a commit. A name is looked up as a branch and then as a tag; a
    /// name that starts with <c>refs/heads/</c> or <c>refs/tags/</c> is looked up as that kind
    /// only. The ref filter matches by prefix, so only the ref with exactly that name counts.
    /// An annotated tag gives the commit it points at.
    /// </summary>
    public async Task<GitHubResult<string>> ResolveRefAsync(string locator, string reference, CancellationToken ct)
    {
        if (Parts(Host, locator) is not [var organization, var project, var repository] || !IsRef(reference))
            return new GitHubFailure.NotFound();

        if (IsSha(reference))
        {
            return await _send.SendAsync<string>("get-commit", call => _api.GetCommit(organization, project, repository, reference, call, ct),
                async response => await OutboundBody.ReadJsonAsync<AzureCommitDto>(response, ct) is { CommitId: { } sha } && IsSha(sha) ? sha : Unexpected, ct);
        }

        string[] kinds = reference.StartsWith(Heads, StringComparison.Ordinal) ? [Heads]
            : reference.StartsWith(Tags, StringComparison.Ordinal) ? [Tags]
            : new[] { Heads, Tags };
        var name = reference.StartsWith(Heads, StringComparison.Ordinal) ? reference[Heads.Length..]
            : reference.StartsWith(Tags, StringComparison.Ordinal) ? reference[Tags.Length..]
            : reference;
        if (name.Length == 0)
            return new GitHubFailure.NotFound();

        GitHubResult<string> found = new GitHubFailure.NotFound();
        foreach (var kind in kinds)
        {
            var full = kind + name;
            found = await _send.SendAsync<string>("list-refs", call => _api.ListRefs(organization, project, repository, full["refs/".Length..], call, ct), async response =>
            {
                if (await OutboundBody.ReadJsonAsync<AzureListDto<AzureRefDto>>(response, ct) is not { Value: { } refs })
                    return Unexpected;
                return refs.FirstOrDefault(r => r?.Name == full) switch
                {
                    null => new GitHubFailure.NotFound(),
                    { PeeledObjectId: { } peeled } when IsSha(peeled) => peeled,
                    { PeeledObjectId: null, ObjectId: { } sha } when IsSha(sha) => sha,
                    _ => Unexpected,
                };
            }, ct, PageMaxBytes);
            if (found.Failure is not GitHubFailure.NotFound)
                return found;
        }
        return found;
    }

    /// <summary>
    /// Every blob of the commit: the commit gives its tree, and the tree is read recursively in
    /// one answer. A tree over the response cap or over <see cref="GitHostText.MaxEntries"/>
    /// entries is <see cref="GitHubFailure.Truncated"/>.
    /// </summary>
    public async Task<GitHubResult<IReadOnlyList<GitHubTreeEntry>>> ListFilesAsync(string locator, string commitSha, CancellationToken ct)
    {
        if (Parts(Host, locator) is not [var organization, var project, var repository] || !IsSha(commitSha))
            return new GitHubFailure.NotFound();

        var commit = await _send.SendAsync<string>("get-commit", call => _api.GetCommit(organization, project, repository, commitSha, call, ct),
            async response => await OutboundBody.ReadJsonAsync<AzureCommitDto>(response, ct) is { TreeId: { } tree } && IsSha(tree) ? tree : Unexpected, ct);
        if (commit.Failure is { } failure)
            return failure;

        var tree = await _send.SendAsync<IReadOnlyList<GitHubTreeEntry>>("get-tree",
            call => _api.GetTree(organization, project, repository, commit.Value!, call, ct), async response =>
            {
                if (await OutboundBody.ReadJsonAsync<AzureTreeDto>(response, ct) is not { TreeEntries: { } listed })
                    return Unexpected;
                if (listed.Count > MaxEntries)
                    return new GitHubFailure.Truncated();
                var entries = new List<GitHubTreeEntry>(listed.Count);
                foreach (var entry in listed)
                {
                    if (entry is not { RelativePath: { } path, GitObjectType: { } type, ObjectId: { } sha } || !IsSha(sha))
                        return Unexpected;
                    if (type == "blob")
                        entries.Add(new GitHubTreeEntry(path, "blob", sha, entry.Size, Clean(entry.Mode, MaxWordChars)));
                }
                return entries;
            }, ct, TreeMaxBytes);
        return tree.Failure is GitHubFailure.TooLarge ? new GitHubFailure.Truncated() : tree;
    }

    /// <summary>The bytes of the blob, read by the SHA the list gave. The pipeline cuts a body over <paramref name="maxBytes"/>.</summary>
    public Task<GitHubResult<byte[]>> GetFileAsync(
        string locator, string commitSha, string path, string blobSha, long maxBytes, CancellationToken ct) =>
        Parts(Host, locator) is not [var organization, var project, var repository] || !IsSha(blobSha) || maxBytes < 0
            ? NotFound<byte[]>()
            : _send.SendAsync<byte[]>("get-blob", call => _api.GetBlob(organization, project, repository, blobSha, call, ct),
                async response => await GitHostSender.ReadBytesAsync(response, ct), ct, maxBytes);

    /// <summary>
    /// The display name of the token's user, from the connection data of the configured address.
    /// dev.azure.com has no user call outside an organization other than this one; the profile
    /// API is on another host, where the token is not sent. Without a token nothing is sent.
    /// </summary>
    public async Task<GitHubResult<GitHostIdentity>> IdentifyAsync(CancellationToken ct)
    {
        if (!HasToken)
            return new GitHostIdentity(null);
        return await _send.SendAsync<GitHostIdentity>("connection-data", call => _api.GetConnectionData(call, ct), async response =>
            await OutboundBody.ReadJsonAsync<AzureConnectionDataDto>(response, ct) is { AuthenticatedUser: { } user }
            && Clean(user.ProviderDisplayName, MaxLoginChars) is { } login
                ? new GitHostIdentity(login)
                : Unexpected, ct);
    }

    /// <summary>
    /// One page of the repositories of an organization, or of one project of it. Azure DevOps
    /// returns them all in one answer; the pages are cut from it in name order. Disabled and
    /// empty repositories are left out.
    /// </summary>
    public async Task<GitHubResult<GitHostRepositoryPage>> ListRepositoriesAsync(string owner, int page, CancellationToken ct)
    {
        if (!Host.TryReadOwner(owner, out var checkedOwner) || page is < 1 or > MaxRepositoryPages)
            return new GitHubFailure.NotFound();
        var parts = checkedOwner.Split('/');
        var organization = parts[0].ToLowerInvariant();

        return await _send.SendAsync<GitHostRepositoryPage>("list-repositories",
            call => parts.Length == 1
                ? _api.ListOrganizationRepositories(parts[0], call, ct)
                : _api.ListProjectRepositories(parts[0], parts[1], call, ct),
            async response =>
            {
                if (await OutboundBody.ReadJsonAsync<AzureListDto<AzureRepositoryDto>>(response, ct) is not { Value: { } listed })
                    return Unexpected;
                var repositories = listed
                    .Where(r => r is { IsDisabled: false })
                    .Select(r => Repository(organization, r!))
                    .OfType<GitHostRepository>()
                    .OrderBy(r => r.Locator, StringComparer.Ordinal)
                    .ToList();
                var start = (page - 1) * RepositoriesPerPage;
                return new GitHostRepositoryPage([.. repositories.Skip(start).Take(RepositoriesPerPage)], repositories.Count > start + RepositoriesPerPage);
            }, ct, PageMaxBytes);
    }

    private static GitHostRepository? Repository(string organization, AzureRepositoryDto dto)
    {
        if (Clean(dto.Name, MaxNameChars) is not { } name || Clean(dto.Project?.Name, MaxNameChars) is not { } project
            || Clean(dto.DefaultBranch, MaxRefChars) is not { } branch)
            return null;
        var display = $"{organization}/{project}/{name}";
        return Locator(GitHostInfo.AzureDevops, display) is { } locator
            ? new GitHostRepository(locator, display, branch.StartsWith(Heads, StringComparison.Ordinal) ? branch[Heads.Length..] : branch,
                dto.Project?.Visibility != "public")
            : null;
    }
}
