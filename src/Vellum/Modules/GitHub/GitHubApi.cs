using System.Text.Json.Serialization;
using Refit;
using Vellum.Shared.Outbound;

namespace Vellum.Modules.GitHub;

/// <summary>
/// The GitHub REST calls that Vellum makes. All are reads. Version <c>2022-11-28</c> is the
/// one that both github.com and GitHub Enterprise Server accept. Every method returns the raw
/// response; <see cref="GitHubConnection"/> turns it into a typed result.
/// </summary>
[Headers("Accept: application/vnd.github+json", "X-GitHub-Api-Version: 2022-11-28", "User-Agent: Vellum")]
internal interface IGitHubApi
{
    [Get("/repos/{owner}/{repo}")]
    Task<HttpResponseMessage> GetRepository(string owner, string repo,
        [Property(OutboundCall.PropertyName)] OutboundCall call, CancellationToken ct);

    /// <summary>
    /// "List commits" with one result: the newest commit that <paramref name="reference"/>
    /// (a SHA, a branch or a tag) points at. The ref is a query value, so Refit encodes it as
    /// a whole and a slash in it stays part of the ref. "Get a commit" takes the ref as path
    /// parts, where <c>main/status</c> is another endpoint.
    /// </summary>
    [Get("/repos/{owner}/{repo}/commits")]
    Task<HttpResponseMessage> ListCommits(string owner, string repo,
        [AliasAs("sha")] string reference, [AliasAs("per_page")] int perPage,
        [Property(OutboundCall.PropertyName)] OutboundCall call, CancellationToken ct);

    [Get("/repos/{owner}/{repo}/git/trees/{sha}?recursive=1")]
    Task<HttpResponseMessage> GetTree(string owner, string repo, string sha,
        [Property(OutboundCall.PropertyName)] OutboundCall call, CancellationToken ct);

    [Get("/repos/{owner}/{repo}/git/blobs/{sha}")]
    Task<HttpResponseMessage> GetBlob(string owner, string repo, string sha,
        [Property(OutboundCall.PropertyName)] OutboundCall call, CancellationToken ct);

    /// <summary>The changed files are on the first page only, so the commit list is kept to one entry.</summary>
    [Get("/repos/{owner}/{repo}/compare/{basehead}?per_page=1&page=1")]
    Task<HttpResponseMessage> Compare(string owner, string repo, string basehead,
        [Property(OutboundCall.PropertyName)] OutboundCall call, CancellationToken ct);

    [Get("/repos/{owner}/{repo}/pulls/{number}")]
    Task<HttpResponseMessage> GetPullRequest(string owner, string repo, int number,
        [Property(OutboundCall.PropertyName)] OutboundCall call, CancellationToken ct);

    [Get("/rate_limit")]
    Task<HttpResponseMessage> GetRateLimit([Property(OutboundCall.PropertyName)] OutboundCall call, CancellationToken ct);

    [Get("/user")]
    Task<HttpResponseMessage> GetAuthenticatedUser([Property(OutboundCall.PropertyName)] OutboundCall call, CancellationToken ct);

    /// <summary>Repeats a read at the address in <see cref="OutboundCall.AbsoluteUri"/>.</summary>
    [Get("/")]
    Task<HttpResponseMessage> Follow([Property(OutboundCall.PropertyName)] OutboundCall call, CancellationToken ct);
}

internal sealed record ErrorDto(
    [property: JsonPropertyName("message")] string? Message,
    [property: JsonPropertyName("documentation_url")] string? DocumentationUrl);

internal sealed record RepositoryDto(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("full_name")] string? FullName,
    [property: JsonPropertyName("private")] bool Private,
    [property: JsonPropertyName("default_branch")] string? DefaultBranch,
    [property: JsonPropertyName("archived")] bool Archived,
    [property: JsonPropertyName("size")] long Size);

internal sealed record TreeDto(
    [property: JsonPropertyName("sha")] string? Sha,
    [property: JsonPropertyName("truncated")] bool Truncated,
    [property: JsonPropertyName("tree")] List<TreeEntryDto>? Tree);

internal sealed record TreeEntryDto(
    [property: JsonPropertyName("path")] string? Path,
    [property: JsonPropertyName("mode")] string? Mode,
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("sha")] string? Sha,
    [property: JsonPropertyName("size")] long? Size);

internal sealed record BlobDto(
    [property: JsonPropertyName("sha")] string? Sha,
    [property: JsonPropertyName("size")] long? Size,
    [property: JsonPropertyName("encoding")] string? Encoding,
    [property: JsonPropertyName("content")] string? Content);

internal sealed record CompareDto(
    [property: JsonPropertyName("status")] string? Status,
    [property: JsonPropertyName("total_commits")] int TotalCommits,
    [property: JsonPropertyName("base_commit")] CommitRefDto? BaseCommit,
    [property: JsonPropertyName("merge_base_commit")] CommitRefDto? MergeBaseCommit,
    [property: JsonPropertyName("files")] List<ChangedFileDto>? Files);

internal sealed record CommitRefDto([property: JsonPropertyName("sha")] string? Sha);

internal sealed record ChangedFileDto(
    [property: JsonPropertyName("filename")] string? Filename,
    [property: JsonPropertyName("status")] string? Status,
    [property: JsonPropertyName("sha")] string? Sha,
    [property: JsonPropertyName("previous_filename")] string? PreviousFilename);

internal sealed record PullRequestDto(
    [property: JsonPropertyName("number")] int Number,
    [property: JsonPropertyName("state")] string? State,
    [property: JsonPropertyName("merged")] bool Merged,
    [property: JsonPropertyName("base")] PullSideDto? Base,
    [property: JsonPropertyName("head")] PullSideDto? Head);

internal sealed record PullSideDto(
    [property: JsonPropertyName("ref")] string? Ref,
    [property: JsonPropertyName("sha")] string? Sha,
    [property: JsonPropertyName("repo")] PullRepoDto? Repo);

internal sealed record PullRepoDto([property: JsonPropertyName("full_name")] string? FullName);

internal sealed record RateLimitDto([property: JsonPropertyName("resources")] RateResourcesDto? Resources);

internal sealed record RateResourcesDto([property: JsonPropertyName("core")] RateDto? Core);

internal sealed record RateDto(
    [property: JsonPropertyName("limit")] int Limit,
    [property: JsonPropertyName("remaining")] int Remaining,
    [property: JsonPropertyName("reset")] long Reset);

internal sealed record UserDto([property: JsonPropertyName("login")] string? Login);
