using Vellum.Modules.GitHub;
using Vellum.Shared.Outbound;
using static Vellum.Modules.GitHosts.GitHostText;

namespace Vellum.Modules.GitHosts.Bitbucket;

/// <summary>
/// The reads of Bitbucket Cloud (API 2.0) for one workspace. A repository is named
/// <c>workspace/slug</c>. Bitbucket names no blob SHA in a file list, so the entries have an
/// empty SHA and the scan computes it from the bytes; a file is read by its path in the
/// commit. A rate limit is a 429, with <c>Retry-After</c> when Bitbucket sends one.
/// </summary>
internal sealed class BitbucketConnection(IBitbucketApi api, GitHostContext context) : IGitHostConnection
{
    private const int MaxListPages = MaxEntries / 100;

    private readonly IBitbucketApi _api = api;
    private readonly GitHostSender _send = new(context, "bitbucket", api.Follow, resetHeader: null);

    public GitHostInfo Host => GitHostInfo.Bitbucket;

    public bool HasToken => _send.HasToken;

    public Task<GitHubResult<GitHostRepository>> GetRepositoryAsync(string locator, CancellationToken ct) =>
        Parts(Host, locator) is not [var workspace, var slug]
            ? NotFound<GitHostRepository>()
            : _send.SendAsync<GitHostRepository>("get-repository", call => _api.GetRepository(workspace, slug, call, ct), async response =>
            {
                if (await OutboundBody.ReadJsonAsync<BitbucketRepositoryDto>(response, ct) is not { } dto)
                    return Unexpected;
                // An empty repository has no main branch, and so nothing to read.
                if (dto.MainBranch?.Name is null)
                    return new GitHubFailure.NotFound();
                return Repository(dto) is { } repository ? repository : Unexpected;
            }, ct);

    /// <summary>
    /// A full SHA is read as a commit. A name is looked up as a branch and then as a tag, with a
    /// filter query for exactly that name, so a slash in it stays part of the name.
    /// </summary>
    public async Task<GitHubResult<string>> ResolveRefAsync(string locator, string reference, CancellationToken ct)
    {
        if (Parts(Host, locator) is not [var workspace, var slug] || !IsRef(reference))
            return new GitHubFailure.NotFound();

        if (IsSha(reference))
        {
            return await _send.SendAsync<string>("get-commit", call => _api.GetCommit(workspace, slug, reference, call, ct), async response =>
                await OutboundBody.ReadJsonAsync<BitbucketCommitDto>(response, ct) is { Hash: { } hash } && IsSha(hash) ? hash : Unexpected, ct);
        }

        var query = $"name=\"{reference}\"";
        async Task<GitHubResult<string>> ReadAsync(HttpResponseMessage response)
        {
            if (await OutboundBody.ReadJsonAsync<BitbucketPageDto<BitbucketRefDto>>(response, ct) is not { Values: { } refs })
                return Unexpected;
            return refs.FirstOrDefault(r => r?.Name == reference) switch
            {
                null => new GitHubFailure.NotFound(),
                { Target.Hash: { } hash } when IsSha(hash) => hash,
                _ => Unexpected,
            };
        }

        var branch = await _send.SendAsync("find-branch", call => _api.FindBranches(workspace, slug, query, call, ct), ReadAsync, ct);
        if (branch.Failure is not GitHubFailure.NotFound)
            return branch;
        return await _send.SendAsync("find-tag", call => _api.FindTags(workspace, slug, query, call, ct), ReadAsync, ct);
    }

    /// <summary>
    /// Every file of the commit. Each listing returns a directory and the directories under it
    /// down to a fixed depth. A directory that it names without any entry under it lies below
    /// that depth, and is listed on its own. Each next page is the address Bitbucket gives,
    /// followed only on the configured API origin.
    /// </summary>
    public async Task<GitHubResult<IReadOnlyList<GitHubTreeEntry>>> ListFilesAsync(string locator, string commitSha, CancellationToken ct)
    {
        if (Parts(Host, locator) is not [var workspace, var slug] || !IsSha(commitSha))
            return new GitHubFailure.NotFound();

        var entries = new List<GitHubTreeEntry>();
        var parents = new HashSet<string>(StringComparer.Ordinal);
        var walked = new HashSet<string>(StringComparer.Ordinal) { "" };
        var directories = new Queue<string>([""]);
        var pages = 0;
        while (directories.TryDequeue(out var directory))
        {
            var found = new List<string>();
            Uri? next = null;
            do
            {
                if (++pages > MaxListPages)
                    return new GitHubFailure.Truncated();
                var address = next;
                var read = await _send.SendAsync<Uri?>("list-tree", call =>
                    address is not null ? _api.Follow(_send.At(call, address), ct)
                    : directory.Length == 0 ? _api.ListRoot(workspace, slug, commitSha, call, ct)
                    : _api.ListDirectory(workspace, slug, commitSha, directory, call, ct), async response =>
                {
                    if (await OutboundBody.ReadJsonAsync<BitbucketPageDto<BitbucketTreeEntryDto>>(response, ct) is not { Values: { } values } page)
                        return Unexpected;
                    foreach (var value in values)
                    {
                        if (value is not { Type: { } type, Path: { } path } || !IsPath(path))
                            return Unexpected;
                        parents.Add(path.LastIndexOf('/') is var slash and >= 0 ? path[..slash] : "");
                        if (type == "commit_directory")
                            found.Add(path);
                        else if (type == "commit_file")
                            entries.Add(new GitHubTreeEntry(path, "blob", "", value.Size, Mode(value.Attributes)));
                    }
                    return page.Next switch
                    {
                        null => new GitHubResult<Uri?>(null, null),
                        var link when Uri.TryCreate(link, UriKind.Absolute, out var url) && _send.OnOrigin(url) => url,
                        _ => new GitHubFailure.Unavailable("redirect_refused"),
                    };
                }, ct, PageMaxBytes);

                if (read.Failure is GitHubFailure.TooLarge)
                    return new GitHubFailure.Truncated();
                if (read.Failure is { } failure)
                    return failure;
                if (entries.Count > MaxEntries)
                    return new GitHubFailure.Truncated();
                next = read.Value;
            }
            while (next is not null);

            // Git keeps no empty directory: one with no entry listed was not listed.
            foreach (var path in found)
            {
                if (!parents.Contains(path) && walked.Add(path))
                    directories.Enqueue(path);
            }
        }
        return entries;
    }

    /// <summary>The raw bytes of the file at <paramref name="path"/> in the commit. The pipeline cuts a body over <paramref name="maxBytes"/>.</summary>
    public Task<GitHubResult<byte[]>> GetFileAsync(
        string locator, string commitSha, string path, string blobSha, long maxBytes, CancellationToken ct) =>
        Parts(Host, locator) is not [var workspace, var slug] || !IsSha(commitSha) || !IsPath(path) || maxBytes < 0
            ? NotFound<byte[]>()
            : _send.SendAsync<byte[]>("get-file", call => _api.GetFile(workspace, slug, commitSha, path, call, ct),
                async response => await GitHostSender.ReadBytesAsync(response, ct), ct, maxBytes);

    /// <summary>The account of the token. Without a token nothing is sent: Bitbucket has no user for an anonymous read.</summary>
    public async Task<GitHubResult<GitHostIdentity>> IdentifyAsync(CancellationToken ct)
    {
        if (!HasToken)
            return new GitHostIdentity(null);
        return await _send.SendAsync<GitHostIdentity>("get-user", call => _api.GetCurrentUser(call, ct), async response =>
            await OutboundBody.ReadJsonAsync<BitbucketUserDto>(response, ct) is { } user
            && Clean(user.Username ?? user.Nickname ?? user.DisplayName, MaxLoginChars) is { } login
                ? new GitHostIdentity(login)
                : Unexpected, ct);
    }

    /// <summary>One page of the repositories of a workspace. Empty repositories are left out.</summary>
    public async Task<GitHubResult<GitHostRepositoryPage>> ListRepositoriesAsync(string owner, int page, CancellationToken ct)
    {
        if (!Host.TryReadOwner(owner, out var workspace) || page is < 1 or > MaxRepositoryPages)
            return new GitHubFailure.NotFound();

        return await _send.SendAsync<GitHostRepositoryPage>("list-repositories", call => _api.ListRepositories(workspace, page, call, ct), async response =>
            await OutboundBody.ReadJsonAsync<BitbucketPageDto<BitbucketRepositoryDto>>(response, ct) is { Values: { } listed } dto
                ? new GitHostRepositoryPage([.. listed.Where(r => r is not null).Select(r => Repository(r!)).OfType<GitHostRepository>()], dto.Next is not null)
                : Unexpected, ct, PageMaxBytes);
    }

    private static GitHostRepository? Repository(BitbucketRepositoryDto dto) =>
        Clean(dto.FullName, MaxNameChars) is { } name && Locator(GitHostInfo.Bitbucket, name) is { } locator
        && Clean(dto.MainBranch?.Name, MaxRefChars) is { } branch
            ? new GitHostRepository(locator, name, branch, dto.IsPrivate)
            : null;

    /// <summary>The git file mode that Bitbucket's attributes stand for.</summary>
    private static string Mode(List<string?>? attributes) =>
        attributes is null ? "100644"
        : attributes.Contains("link") ? "120000"
        : attributes.Contains("executable") ? "100755"
        : "100644";
}
