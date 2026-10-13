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
    /// <param name="ResetAt">
    /// When to try again: the time GitHub gave, or one minute from now for a secondary rate
    /// limit that gave none. Null only when GitHub's reset time is not a date.
    /// </param>
    public sealed record RateLimited(DateTimeOffset? ResetAt) : GitHubFailure;
    /// <summary>The object is larger than the cap the caller gave.</summary>
    public sealed record TooLarge : GitHubFailure;
    /// <summary>GitHub cut the recursive tree. The entries are not returned, because a partial tree reads like a whole one.</summary>
    public sealed record Truncated : GitHubFailure;
    /// <param name="Reason">
    /// One of <c>url_refused</c>, <c>invalid_setting</c>, <c>timeout</c>, <c>network</c>,
    /// <c>server_error</c>, <c>redirect_refused</c>, <c>unexpected_response</c>,
    /// <c>token_saved_for_another_host</c>, <c>secret_unavailable</c>.
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
/// <param name="Path">The path as the repository holds it. It is not changed: it is the key of the entry, and it is untrusted text.</param>
public sealed record GitHubTreeEntry(string Path, string Type, string Sha, long? Size, string? Mode);
public sealed record GitHubTree(string Sha, IReadOnlyList<GitHubTreeEntry> Entries);
public sealed record GitHubBlob(string Sha, byte[] Content);
/// <param name="Path">The path as the repository holds it, unchanged and untrusted.</param>
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

        // The settings module applies the instance policy and names the origin the token
        // is bound to. A token saved for another host is never sent.
        if (workspace.GithubApiBaseUrl is not SettingsUrl.Usable destination)
            return new GitHubFailure.Unavailable("url_refused");
        var baseUrl = destination.Url;

        string? token;
        try
        {
            switch (await secrets.ReadAsync(workspaceId, SecretNames.GithubToken, baseUrl, ct))
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

        // The policy and the https rule come from the same settings read as the URL. The
        // pipeline applies both to every attempt and to every followed redirect.
        return new GitHubConnection(
            services.GetRequiredService<IGitHubApi>(), baseUrl, workspace.Policy.AllowPrivateNetworkUrls, workspace.RequireHttps,
            token is null ? null : new OutboundCredential("Authorization", "Bearer", token));
    }
}

/// <summary>
/// The GitHub reads of steps 07 and later. Everything GitHub returns is untrusted data. The
/// short texts (names, branch names, states, the login) are cut to a fixed length and
/// cleared of control characters before they leave this class. File paths and file content
/// are returned as they are, because they are keys and evidence; a caller treats them as data.
/// </summary>
public sealed partial class GitHubConnection
{
    private const int MaxRedirects = 3;
    private const int CompareFileLimit = 300;
    private const long TreeMaxBytes = 16 * 1024 * 1024;
    private const int MaxNameChars = 300;
    private const int MaxRefChars = 255;
    private const int MaxWordChars = 40;
    private const int MaxLoginChars = 100;
    private const long MaxEpochSeconds = 253402300799;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan SecondaryLimitWait = TimeSpan.FromMinutes(1);

    private readonly IGitHubApi _api;
    private readonly Uri _baseUrl;
    private readonly bool _allowPrivateNetwork;
    private readonly bool _requireHttps;
    private readonly OutboundCredential? _credential;

    internal GitHubConnection(IGitHubApi api, Uri baseUrl, bool allowPrivateNetwork, bool requireHttps, OutboundCredential? credential)
    {
        _api = api;
        _baseUrl = baseUrl;
        _allowPrivateNetwork = allowPrivateNetwork;
        _requireHttps = requireHttps;
        _credential = credential;
    }

    public bool HasToken => _credential is not null;

    public Task<GitHubResult<GitHubRepository>> GetRepositoryAsync(string owner, string repo, CancellationToken ct = default) =>
        !IsName(owner) || !IsName(repo)
            ? NotFound<GitHubRepository>()
            : SendAsync<GitHubRepository>("get-repository", call => _api.GetRepository(owner, repo, call, ct), async response =>
                await OutboundBody.ReadJsonAsync<RepositoryDto>(response, ct) is { } dto
                && Clean(dto.FullName, MaxNameChars) is { } name && Clean(dto.DefaultBranch, MaxRefChars) is { } branch
                    ? new GitHubRepository(name, branch, dto.Private, dto.Archived)
                    : Unexpected, ct);

    /// <summary>
    /// Resolves a branch, a tag or a SHA to the immutable commit SHA. The ref travels as one
    /// encoded query value of "list commits", so a ref with a slash, such as
    /// <c>main/status</c>, is a ref and cannot select another endpoint.
    /// </summary>
    public Task<GitHubResult<string>> ResolveRefAsync(string owner, string repo, string reference, CancellationToken ct = default) =>
        !IsName(owner) || !IsName(repo) || !IsRef(reference)
            ? NotFound<string>()
            : SendAsync<string>("resolve-ref", call => _api.ListCommits(owner, repo, reference, 1, call, ct), async response =>
                await OutboundBody.ReadJsonAsync<List<CommitRefDto?>>(response, ct) switch
                {
                    null => Unexpected,
                    [] => new GitHubFailure.NotFound(),
                    [{ Sha: { } sha }, ..] when IsSha(sha) => sha,
                    _ => Unexpected,
                }, ct);

    /// <summary>The whole tree of a commit. A tree that GitHub cut gives <see cref="GitHubFailure.Truncated"/>.</summary>
    public Task<GitHubResult<GitHubTree>> GetTreeAsync(string owner, string repo, string commitSha, CancellationToken ct = default) =>
        !IsName(owner) || !IsName(repo) || !IsSha(commitSha)
            ? NotFound<GitHubTree>()
            : SendAsync<GitHubTree>("get-tree", call => _api.GetTree(owner, repo, commitSha, call, ct), async response =>
            {
                if (await OutboundBody.ReadJsonAsync<TreeDto>(response, ct) is not { Sha: { } sha, Tree: { } tree } dto || !IsSha(sha))
                    return Unexpected;
                if (dto.Truncated)
                    return new GitHubFailure.Truncated();
                return new GitHubTree(sha, tree
                    .Where(e => e is { Path: not null, Type: not null, Sha: not null })
                    .Select(e => new GitHubTreeEntry(e.Path!, OutboundText.Clean(e.Type!, MaxWordChars), e.Sha!, e.Size, Clean(e.Mode, MaxWordChars)))
                    .ToList());
            }, ct, TreeMaxBytes);

    /// <summary>The bytes of a blob, or <see cref="GitHubFailure.TooLarge"/> when it is over <paramref name="maxBytes"/>.</summary>
    public Task<GitHubResult<GitHubBlob>> GetBlobAsync(string owner, string repo, string blobSha, long maxBytes, CancellationToken ct = default) =>
        !IsName(owner) || !IsName(repo) || !IsSha(blobSha) || maxBytes < 0
            ? NotFound<GitHubBlob>()
            : SendAsync<GitHubBlob>("get-blob", call => _api.GetBlob(owner, repo, blobSha, call, ct), async response =>
            {
                if (await OutboundBody.ReadJsonAsync<BlobDto>(response, ct) is not { Sha: { } sha, Content: { } content } dto || !IsSha(sha))
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
            ct, BlobResponseCap(maxBytes));

    /// <summary>
    /// The largest response that can hold a blob of <paramref name="maxBytes"/>. Base64 is
    /// four characters for three bytes, rounded up. GitHub ends each line of 60 characters
    /// with a line break, which is two bytes (<c>\n</c>) inside a JSON string. The rest of
    /// the JSON is small; 8 KiB covers it.
    /// </summary>
    internal static long BlobResponseCap(long maxBytes)
    {
        var base64 = (maxBytes + 2) / 3 * 4;
        return base64 + (base64 / 60 + 1) * 2 + 8192;
    }

    /// <summary>The files that differ between two commits, as GitHub's three-dot comparison gives them.</summary>
    public Task<GitHubResult<GitHubComparison>> CompareAsync(string owner, string repo, string baseSha, string headSha, CancellationToken ct = default) =>
        !IsName(owner) || !IsName(repo) || !IsSha(baseSha) || !IsSha(headSha)
            ? NotFound<GitHubComparison>()
            : SendAsync<GitHubComparison>("compare", call => _api.Compare(owner, repo, $"{baseSha}...{headSha}", call, ct), async response =>
            {
                if (await OutboundBody.ReadJsonAsync<CompareDto>(response, ct) is not { } dto || Clean(dto.Status, MaxWordChars) is not { } status)
                    return Unexpected;
                var files = (dto.Files ?? [])
                    .Where(f => f is { Filename: not null, Status: not null })
                    .Select(f => new GitHubChangedFile(f.Filename!, OutboundText.Clean(f.Status!, MaxWordChars), ShaOrNull(f.Sha), f.PreviousFilename))
                    .ToList();
                return new GitHubComparison(status, dto.TotalCommits, ShaOrNull(dto.BaseCommit?.Sha), ShaOrNull(dto.MergeBaseCommit?.Sha),
                    files, (dto.Files?.Count ?? 0) >= CompareFileLimit);
            }, ct, TreeMaxBytes);

    public Task<GitHubResult<GitHubPullRequest>> GetPullRequestAsync(string owner, string repo, int number, CancellationToken ct = default) =>
        !IsName(owner) || !IsName(repo) || number < 1
            ? NotFound<GitHubPullRequest>()
            : SendAsync<GitHubPullRequest>("get-pull-request", call => _api.GetPullRequest(owner, repo, number, call, ct), async response =>
                await OutboundBody.ReadJsonAsync<PullRequestDto>(response, ct) is
                {
                    Base: { Sha: { } baseSha } @base,
                    Head: { Sha: { } headSha } head,
                } dto && IsSha(baseSha) && IsSha(headSha)
                && Clean(dto.State, MaxWordChars) is { } state && Clean(@base.Ref, MaxRefChars) is { } baseRef && Clean(head.Ref, MaxRefChars) is { } headRef
                    ? new GitHubPullRequest(dto.Number, state, dto.Merged, baseRef, baseSha, headRef, headSha, Clean(head.Repo?.FullName, MaxNameChars))
                    : Unexpected, ct);

    public Task<GitHubResult<GitHubRateLimit>> GetRateLimitAsync(CancellationToken ct = default) =>
        SendAsync<GitHubRateLimit>("rate-limit", call => _api.GetRateLimit(call, ct), async response =>
            await OutboundBody.ReadJsonAsync<RateLimitDto>(response, ct) is { Resources.Core: { } core } && FromEpoch(core.Reset) is { } resetAt
                ? new GitHubRateLimit(core.Limit, core.Remaining, resetAt)
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
            await OutboundBody.ReadJsonAsync<UserDto>(response, ct) is { } user && Clean(user.Login, MaxLoginChars) is { } login
                ? new GitHubIdentity(login, HeaderInt(response, "x-ratelimit-remaining"))
                : Unexpected, ct);
    }

    private static GitHubFailure Unexpected => new GitHubFailure.Unavailable("unexpected_response");

    private static Task<GitHubResult<T>> NotFound<T>() => Task.FromResult<GitHubResult<T>>(new GitHubFailure.NotFound());

    /// <summary>Remote text for a client: cut, without control characters, and null when nothing is left.</summary>
    private static string? Clean(string? text, int maxLength) =>
        text is null ? null : OutboundText.Clean(text, maxLength) is { Length: > 0 } clean ? clean : null;

    private static string? ShaOrNull(string? value) => IsSha(value) ? value : null;

    /// <summary>A Unix time in seconds as a date, or null when no date has that number.</summary>
    private static DateTimeOffset? FromEpoch(long seconds) =>
        seconds is >= 0 and <= MaxEpochSeconds ? DateTimeOffset.FromUnixTimeSeconds(seconds) : null;

    private async Task<GitHubResult<T>> SendAsync<T>(
        string operation,
        Func<OutboundCall, Task<HttpResponseMessage>> send,
        Func<HttpResponseMessage, Task<GitHubResult<T>>> read,
        CancellationToken ct,
        long maxBytes = OutboundCall.DefaultMaxResponseBytes)
    {
        var call = new OutboundCall("github", operation, _baseUrl, _allowPrivateNetwork, _requireHttps, Timeout)
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
                // again like any other, with the same private-network and https rules.
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
                response = await OutboundSend.RunAsync(() => _api.Follow(next, ct));
            }

            using (response)
                return response.IsSuccessStatusCode ? await read(response) : await FailureAsync(response, ct);
        }
        catch (OutboundException ex)
        {
            return ex.Failure switch
            {
                OutboundFailure.ResponseTooLarge => new GitHubFailure.TooLarge(),
                OutboundFailure.UrlRefused => new GitHubFailure.Unavailable("url_refused"),
                OutboundFailure.InvalidRequest => new GitHubFailure.Unavailable("invalid_setting"),
                OutboundFailure.Timeout => new GitHubFailure.Unavailable("timeout"),
                _ => new GitHubFailure.Unavailable("network"),
            };
        }
    }

    private static async Task<GitHubFailure> FailureAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var status = (int)response.StatusCode;
        if (status == 401)
            return new GitHubFailure.Unauthorized();

        // A rate limit is a 403 or a 429. A primary limit has no requests left. A secondary
        // limit gives Retry-After, or only says so in its message; GitHub then asks for a
        // wait of at least one minute.
        var retryAfter = response.Headers.RetryAfter?.Delta;
        var exhausted = HeaderInt(response, "x-ratelimit-remaining") == 0;
        if (status == 429 || (status == 403 && (retryAfter is not null || exhausted || await SaysSecondaryLimitAsync(response, ct))))
        {
            return new GitHubFailure.RateLimited(
                retryAfter is { } wait ? DateTimeOffset.UtcNow + wait
                : exhausted ? HeaderLong(response, "x-ratelimit-reset") is { } epoch ? FromEpoch(epoch) : null
                : DateTimeOffset.UtcNow + SecondaryLimitWait);
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

    private static async Task<bool> SaysSecondaryLimitAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            return await OutboundBody.ReadJsonAsync<ErrorDto>(response, ct) is { } error
                   && ((error.Message?.Contains("secondary rate limit", StringComparison.OrdinalIgnoreCase) ?? false)
                       || (error.DocumentationUrl?.Contains("secondary-rate-limits", StringComparison.OrdinalIgnoreCase) ?? false));
        }
        catch (OutboundException)
        {
            // The body could not be read. The status alone is a permission failure.
            return false;
        }
    }

    private static long? HeaderLong(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values)
        && long.TryParse(values.FirstOrDefault(), NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;

    private static int? HeaderInt(HttpResponseMessage response, string name) =>
        HeaderLong(response, name) is >= 0 and <= int.MaxValue and var value ? (int)value : null;

    private static bool IsName(string? value) => value is not (null or "." or "..") && Name().IsMatch(value);

    private static bool IsSha(string? value) => value is not null && Sha().IsMatch(value);

    /// <summary>
    /// The rules of <c>git check-ref-format</c> that matter for one ref name: no control
    /// character, no space, none of <c>~ ^ : ? * [ \</c>, no <c>..</c>, no <c>@{</c>, no empty
    /// part between slashes, no part that starts with a dot or ends with <c>.lock</c>, and no
    /// dot at the end. A name that passes is one ref and is sent as one encoded value.
    /// </summary>
    private static bool IsRef(string? value) =>
        value is { Length: >= 1 and <= MaxRefChars } and not "@"
        && !OutboundText.HasControlCharacter(value)
        && !value.Any(c => char.IsWhiteSpace(c) || c is '~' or '^' or ':' or '?' or '*' or '[' or '\\')
        && !value.Contains("..", StringComparison.Ordinal) && !value.Contains("@{", StringComparison.Ordinal)
        && !value.EndsWith('.')
        && value.Split('/').All(part => part.Length > 0 && part[0] != '.' && !part.EndsWith(".lock", StringComparison.Ordinal));

    // \z is the end of the text. $ also matches before a final line break.
    [GeneratedRegex(@"\A[A-Za-z0-9_.-]{1,100}\z")]
    private static partial Regex Name();

    [GeneratedRegex(@"\A(?:[0-9a-f]{40}|[0-9a-f]{64})\z")]
    private static partial Regex Sha();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhiteSpace();
}
