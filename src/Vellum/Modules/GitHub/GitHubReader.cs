using System.Globalization;
using System.Text.RegularExpressions;
using Vellum.Modules.Settings.Document;
using Vellum.Modules.Settings.Secrets;
using Vellum.Shared.Outbound;

namespace Vellum.Modules.GitHub;

/// <summary>Why a GitHub read gave no value. Callers see these and no HTTP type.</summary>
public abstract record GitHubFailure
{
    private GitHubFailure() { }

    /// <summary>The repository, ref or object does not exist, or the token cannot see it.</summary>
    public sealed record NotFound : GitHubFailure;
    /// <summary>GitHub refused the token.</summary>
    public sealed record Unauthorized : GitHubFailure;
    /// <summary>The token is valid and may not do this.</summary>
    public sealed record Forbidden : GitHubFailure;
    public sealed record RateLimited(DateTimeOffset? ResetAt) : GitHubFailure;
    /// <summary>The object is larger than the cap the caller gave.</summary>
    public sealed record TooLarge : GitHubFailure;
    /// <summary>GitHub cut the recursive tree. The entries are not returned, because a partial tree reads like a whole one.</summary>
    public sealed record Truncated : GitHubFailure;
    /// <param name="Reason">
    /// One of <c>url_refused</c>, <c>timeout</c>, <c>network</c>, <c>server_error</c>,
    /// <c>redirect_refused</c>, <c>unexpected_response</c>, <c>secret_unavailable</c>.
    /// </param>
    public sealed record Unavailable(string Reason) : GitHubFailure;
}

public readonly record struct GitHubResult<T>(T? Value, GitHubFailure? Failure)
{
    public bool Ok => Failure is null;
    public static implicit operator GitHubResult<T>(T value) => new(value, null);
    public static implicit operator GitHubResult<T>(GitHubFailure failure) => new(default, failure);
}

public sealed record GitHubRepository(string FullName, string DefaultBranch, bool IsPrivate, bool Archived);
public sealed record GitHubTreeEntry(string Path, string Type, string Sha, long? Size, string? Mode);
public sealed record GitHubTree(string Sha, IReadOnlyList<GitHubTreeEntry> Entries);
public sealed record GitHubBlob(string Sha, byte[] Content);
public sealed record GitHubChangedFile(string Path, string Status, string? Sha, string? PreviousPath);

/// <param name="FilesIncomplete">
/// GitHub returns at most 300 changed files for a comparison. True when the list reached that
/// number, so more files can exist.
/// </param>
public sealed record GitHubComparison(
    string Status, int TotalCommits, string? BaseSha, string? MergeBaseSha,
    IReadOnlyList<GitHubChangedFile> Files, bool FilesIncomplete);

/// <param name="HeadRepository">Null when the head repository was deleted.</param>
public sealed record GitHubPullRequest(
    int Number, string State, bool Merged, string BaseRef, string BaseSha, string HeadRef, string HeadSha, string? HeadRepository);

public sealed record GitHubRateLimit(int Limit, int Remaining, DateTimeOffset ResetAt);

/// <param name="Login">Null when the workspace has no token; the calls are then anonymous.</param>
public sealed record GitHubIdentity(string? Login, int? RateLimitRemaining);

/// <summary>
/// Opens a read-only connection to GitHub for one workspace. The base URL comes from the
/// workspace's settings and the token from its secret <c>github:token</c>. The token is
/// optional: without it the calls are anonymous and reach public repositories only.
/// </summary>
public sealed class GitHubReader(IServiceProvider services, SettingsResolver settings, SecretReader secrets)
{
    public async Task<GitHubResult<GitHubConnection>> ConnectAsync(Guid workspaceId, CancellationToken ct = default)
    {
        var workspace = await settings.GetWorkspaceAsync(workspaceId, ct);
        var instance = await settings.GetInstanceAsync(ct);

        string? token;
        try
        {
            token = await secrets.GetAsync(workspaceId, SecretNames.GithubToken, ct);
        }
        catch (SecretUnavailableException)
        {
            return new GitHubFailure.Unavailable("secret_unavailable");
        }

        if (!Uri.TryCreate(workspace.Effective.Github.ApiBaseUrl, UriKind.Absolute, out var baseUrl))
            return new GitHubFailure.Unavailable("url_refused");

        return new GitHubConnection(
            services.GetRequiredService<IGitHubApi>(), baseUrl, instance.Policy.AllowPrivateNetworkUrls,
            token is null ? null : new OutboundCredential("Authorization", "Bearer", token));
    }
}

/// <summary>The GitHub reads of steps 07 and later. Repository text in a result is untrusted data.</summary>
public sealed partial class GitHubConnection
{
    private const int MaxRedirects = 3;
    private const int CompareFileLimit = 300;
    private const long TreeMaxBytes = 16 * 1024 * 1024;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private readonly IGitHubApi _api;
    private readonly Uri _baseUrl;
    private readonly bool _allowPrivateNetwork;
    private readonly OutboundCredential? _credential;

    internal GitHubConnection(IGitHubApi api, Uri baseUrl, bool allowPrivateNetwork, OutboundCredential? credential)
    {
        _api = api;
        _baseUrl = baseUrl;
        _allowPrivateNetwork = allowPrivateNetwork;
        _credential = credential;
    }

    public bool HasToken => _credential is not null;

    public Task<GitHubResult<GitHubRepository>> GetRepositoryAsync(string owner, string repo, CancellationToken ct = default) =>
        !IsName(owner) || !IsName(repo)
            ? NotFound<GitHubRepository>()
            : SendAsync<GitHubRepository>("get-repository", call => _api.GetRepository(owner, repo, call, ct), async response =>
                await OutboundBody.ReadJsonAsync<RepositoryDto>(response, ct) is { FullName: { } name, DefaultBranch: { } branch } dto
                    ? new GitHubRepository(name, branch, dto.Private, dto.Archived)
                    : Unexpected, ct);

    /// <summary>Resolves a branch, a tag or a SHA to the immutable commit SHA.</summary>
    public Task<GitHubResult<string>> ResolveRefAsync(string owner, string repo, string reference, CancellationToken ct = default) =>
        !IsName(owner) || !IsName(repo) || !IsRef(reference)
            ? NotFound<string>()
            : SendAsync<string>("resolve-ref", call => _api.GetCommitSha(owner, repo, reference, call, ct), async response =>
                (await OutboundBody.ReadTextAsync(response, ct)).Trim() is var sha && IsSha(sha) ? sha : Unexpected,
                ct, maxBytes: 1024, sha: true);

    /// <summary>The whole tree of a commit. A tree that GitHub cut gives <see cref="GitHubFailure.Truncated"/>.</summary>
    public Task<GitHubResult<GitHubTree>> GetTreeAsync(string owner, string repo, string commitSha, CancellationToken ct = default) =>
        !IsName(owner) || !IsName(repo) || !IsSha(commitSha)
            ? NotFound<GitHubTree>()
            : SendAsync<GitHubTree>("get-tree", call => _api.GetTree(owner, repo, commitSha, call, ct), async response =>
            {
                if (await OutboundBody.ReadJsonAsync<TreeDto>(response, ct) is not { Sha: { } sha, Tree: { } tree } dto)
                    return Unexpected;
                if (dto.Truncated)
                    return new GitHubFailure.Truncated();
                return new GitHubTree(sha, tree
                    .Where(e => e is { Path: not null, Type: not null, Sha: not null })
                    .Select(e => new GitHubTreeEntry(e.Path!, e.Type!, e.Sha!, e.Size, e.Mode))
                    .ToList());
            }, ct, TreeMaxBytes);

    /// <summary>The bytes of a blob, or <see cref="GitHubFailure.TooLarge"/> when it is over <paramref name="maxBytes"/>.</summary>
    public Task<GitHubResult<GitHubBlob>> GetBlobAsync(string owner, string repo, string blobSha, long maxBytes, CancellationToken ct = default) =>
        !IsName(owner) || !IsName(repo) || !IsSha(blobSha)
            ? NotFound<GitHubBlob>()
            : SendAsync<GitHubBlob>("get-blob", call => _api.GetBlob(owner, repo, blobSha, call, ct), async response =>
            {
                if (await OutboundBody.ReadJsonAsync<BlobDto>(response, ct) is not { Sha: { } sha, Content: { } content } dto)
                    return Unexpected;
                if (dto.Size > maxBytes)
                    return new GitHubFailure.TooLarge();
                if (dto.Encoding != "base64")
                    return Unexpected;

                // GitHub breaks the base64 text into lines.
                var bytes = new byte[content.Length];
                if (!Convert.TryFromBase64String(WhiteSpace().Replace(content, ""), bytes, out var written))
                    return Unexpected;
                return written > maxBytes ? new GitHubFailure.TooLarge() : new GitHubBlob(sha, bytes[..written]);
            },
            // Base64 is four characters for three bytes, plus line breaks and the JSON around it.
            ct, maxBytes / 3 * 4 + maxBytes / 30 + 8192);

    /// <summary>The files that differ between two commits, as GitHub's three-dot comparison gives them.</summary>
    public Task<GitHubResult<GitHubComparison>> CompareAsync(string owner, string repo, string baseSha, string headSha, CancellationToken ct = default) =>
        !IsName(owner) || !IsName(repo) || !IsSha(baseSha) || !IsSha(headSha)
            ? NotFound<GitHubComparison>()
            : SendAsync<GitHubComparison>("compare", call => _api.Compare(owner, repo, $"{baseSha}...{headSha}", call, ct), async response =>
            {
                if (await OutboundBody.ReadJsonAsync<CompareDto>(response, ct) is not { Status: { } status } dto)
                    return Unexpected;
                var files = (dto.Files ?? [])
                    .Where(f => f is { Filename: not null, Status: not null })
                    .Select(f => new GitHubChangedFile(f.Filename!, f.Status!, f.Sha, f.PreviousFilename))
                    .ToList();
                return new GitHubComparison(status, dto.TotalCommits, dto.BaseCommit?.Sha, dto.MergeBaseCommit?.Sha,
                    files, (dto.Files?.Count ?? 0) >= CompareFileLimit);
            }, ct, TreeMaxBytes);

    public Task<GitHubResult<GitHubPullRequest>> GetPullRequestAsync(string owner, string repo, int number, CancellationToken ct = default) =>
        !IsName(owner) || !IsName(repo) || number < 1
            ? NotFound<GitHubPullRequest>()
            : SendAsync<GitHubPullRequest>("get-pull-request", call => _api.GetPullRequest(owner, repo, number, call, ct), async response =>
                await OutboundBody.ReadJsonAsync<PullRequestDto>(response, ct) is
                {
                    State: { } state,
                    Base: { Ref: { } baseRef, Sha: { } baseSha },
                    Head: { Ref: { } headRef, Sha: { } headSha } head,
                } dto && IsSha(baseSha) && IsSha(headSha)
                    ? new GitHubPullRequest(dto.Number, state, dto.Merged, baseRef, baseSha, headRef, headSha, head.Repo?.FullName)
                    : Unexpected, ct);

    public Task<GitHubResult<GitHubRateLimit>> GetRateLimitAsync(CancellationToken ct = default) =>
        SendAsync<GitHubRateLimit>("rate-limit", call => _api.GetRateLimit(call, ct), async response =>
            await OutboundBody.ReadJsonAsync<RateLimitDto>(response, ct) is { Resources.Core: { } core }
                ? new GitHubRateLimit(core.Limit, core.Remaining, DateTimeOffset.FromUnixTimeSeconds(core.Reset))
                : Unexpected, ct);

    /// <summary>Who the token belongs to. Without a token, the anonymous rate limit that is left.</summary>
    public async Task<GitHubResult<GitHubIdentity>> IdentifyAsync(CancellationToken ct = default)
    {
        if (!HasToken)
        {
            var limit = await GetRateLimitAsync(ct);
            return limit.Failure is { } failure ? failure : new GitHubIdentity(null, limit.Value!.Remaining);
        }

        return await SendAsync<GitHubIdentity>("get-user", call => _api.GetAuthenticatedUser(call, ct), async response =>
            await OutboundBody.ReadJsonAsync<UserDto>(response, ct) is { Login: { } login }
                ? new GitHubIdentity(login, HeaderInt(response, "x-ratelimit-remaining"))
                : Unexpected, ct);
    }

    private static GitHubFailure Unexpected => new GitHubFailure.Unavailable("unexpected_response");

    private static Task<GitHubResult<T>> NotFound<T>() => Task.FromResult<GitHubResult<T>>(new GitHubFailure.NotFound());

    private async Task<GitHubResult<T>> SendAsync<T>(
        string operation,
        Func<OutboundCall, Task<HttpResponseMessage>> send,
        Func<HttpResponseMessage, Task<GitHubResult<T>>> read,
        CancellationToken ct,
        long maxBytes = OutboundCall.DefaultMaxResponseBytes,
        bool sha = false)
    {
        var call = new OutboundCall("github", operation, _baseUrl, _allowPrivateNetwork, Timeout)
        {
            Credential = _credential,
            MaxResponseBytes = maxBytes,
        };

        try
        {
            var response = await OutboundSend.RunAsync(() => send(call));
            for (var hop = 0; (int)response.StatusCode is 301 or 302 or 307 or 308; hop++)
            {
                // GitHub redirects a renamed or moved repository. The pipeline follows nothing
                // by itself. The target is followed here when it is on the configured API
                // origin, so the token goes nowhere else; the pipeline then checks the address
                // again like any other.
                var from = response.RequestMessage?.RequestUri;
                var location = response.Headers.Location;
                response.Dispose();
                if (hop == MaxRedirects || from is null || location is null)
                    return new GitHubFailure.Unavailable("redirect_refused");

                var target = location.IsAbsoluteUri ? location : new Uri(from, location);
                if (target.Scheme != _baseUrl.Scheme || target.Port != _baseUrl.Port
                    || !string.Equals(target.IdnHost, _baseUrl.IdnHost, StringComparison.OrdinalIgnoreCase))
                    return new GitHubFailure.Unavailable("redirect_refused");

                var next = call with { AbsoluteUri = target };
                response = await OutboundSend.RunAsync(() => sha ? _api.FollowSha(next, ct) : _api.Follow(next, ct));
            }

            using (response)
                return response.IsSuccessStatusCode ? await read(response) : Failure(response);
        }
        catch (OutboundException ex)
        {
            return ex.Failure switch
            {
                OutboundFailure.ResponseTooLarge => new GitHubFailure.TooLarge(),
                OutboundFailure.UrlRefused => new GitHubFailure.Unavailable("url_refused"),
                OutboundFailure.Timeout => new GitHubFailure.Unavailable("timeout"),
                _ => new GitHubFailure.Unavailable("network"),
            };
        }
    }

    private static GitHubFailure Failure(HttpResponseMessage response)
    {
        var status = (int)response.StatusCode;
        if (status == 401)
            return new GitHubFailure.Unauthorized();

        // A rate limit is a 403 or a 429. It is told from a permission 403 by the headers.
        var retryAfter = response.Headers.RetryAfter?.Delta;
        if (status == 429 || (status == 403 && (retryAfter is not null || HeaderInt(response, "x-ratelimit-remaining") == 0)))
        {
            DateTimeOffset? resetAt = retryAfter is { } wait
                ? DateTimeOffset.UtcNow + wait
                : HeaderInt(response, "x-ratelimit-reset") is { } epoch ? DateTimeOffset.FromUnixTimeSeconds(epoch) : null;
            return new GitHubFailure.RateLimited(resetAt);
        }

        return status switch
        {
            403 or 451 => new GitHubFailure.Forbidden(),
            // 409 is an empty repository: the ref is not there.
            404 or 409 or 410 => new GitHubFailure.NotFound(),
            >= 500 => new GitHubFailure.Unavailable("server_error"),
            _ => Unexpected,
        };
    }

    private static int? HeaderInt(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values)
        && int.TryParse(values.FirstOrDefault(), NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;

    private static bool IsName(string? value) => value is not (null or "." or "..") && Name().IsMatch(value);

    private static bool IsSha(string? value) => value is not null && Sha().IsMatch(value);

    private static bool IsRef(string? value) =>
        value is { Length: >= 1 and <= 255 } && !value.StartsWith('/') && !value.EndsWith('/')
        && !value.Contains("..", StringComparison.Ordinal) && !value.Contains("//", StringComparison.Ordinal)
        && !value.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c is '?' or '#' or '\\');

    [GeneratedRegex(@"^[A-Za-z0-9_.-]{1,100}$")]
    private static partial Regex Name();

    [GeneratedRegex(@"^(?:[0-9a-f]{40}|[0-9a-f]{64})$")]
    private static partial Regex Sha();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhiteSpace();
}
