using System.Text.Json.Serialization;
using Refit;
using Vellum.Shared.Outbound;

namespace Vellum.Modules.GitHosts.Bitbucket;

/// <summary>
/// The Bitbucket Cloud REST calls (API 2.0) that Vellum makes. All are reads. A file path is
/// sent as path parts, each one encoded (<c>{**path}</c>). Every method returns the raw
/// response; <see cref="BitbucketConnection"/> turns it into a typed result.
/// </summary>
[Headers("Accept: application/json", "User-Agent: Vellum")]
internal interface IBitbucketApi
{
    [Get("/repositories/{workspace}/{slug}")]
    Task<HttpResponseMessage> GetRepository(string workspace, string slug,
        [Property(OutboundCall.PropertyName)] OutboundCall call, CancellationToken ct);

    [Get("/repositories/{workspace}/{slug}/commit/{commit}")]
    Task<HttpResponseMessage> GetCommit(string workspace, string slug, string commit,
        [Property(OutboundCall.PropertyName)] OutboundCall call, CancellationToken ct);

    /// <summary>The branches that a filter query (<c>name="main"</c>) selects.</summary>
    [Get("/repositories/{workspace}/{slug}/refs/branches")]
    Task<HttpResponseMessage> FindBranches(string workspace, string slug, [AliasAs("q")] string query,
        [Property(OutboundCall.PropertyName)] OutboundCall call, CancellationToken ct);

    /// <summary>The tags that a filter query (<c>name="v1.0"</c>) selects.</summary>
    [Get("/repositories/{workspace}/{slug}/refs/tags")]
    Task<HttpResponseMessage> FindTags(string workspace, string slug, [AliasAs("q")] string query,
        [Property(OutboundCall.PropertyName)] OutboundCall call, CancellationToken ct);

    /// <summary>The top directory of a commit and the directories under it down to <c>max_depth</c>, one page.</summary>
    [Get("/repositories/{workspace}/{slug}/src/{commit}/?max_depth=10&pagelen=100")]
    Task<HttpResponseMessage> ListRoot(string workspace, string slug, string commit,
        [Property(OutboundCall.PropertyName)] OutboundCall call, CancellationToken ct);

    /// <summary>One directory of a commit and the directories under it down to <c>max_depth</c>, one page.</summary>
    [Get("/repositories/{workspace}/{slug}/src/{commit}/{**path}/?max_depth=10&pagelen=100")]
    Task<HttpResponseMessage> ListDirectory(string workspace, string slug, string commit, string path,
        [Property(OutboundCall.PropertyName)] OutboundCall call, CancellationToken ct);

    /// <summary>The raw bytes of one file of a commit.</summary>
    [Get("/repositories/{workspace}/{slug}/src/{commit}/{**path}")]
    [Headers("Accept: */*")]
    Task<HttpResponseMessage> GetFile(string workspace, string slug, string commit, string path,
        [Property(OutboundCall.PropertyName)] OutboundCall call, CancellationToken ct);

    [Get("/user")]
    Task<HttpResponseMessage> GetCurrentUser([Property(OutboundCall.PropertyName)] OutboundCall call, CancellationToken ct);

    [Get("/repositories/{workspace}?pagelen=100&sort=slug")]
    Task<HttpResponseMessage> ListRepositories(string workspace, int page,
        [Property(OutboundCall.PropertyName)] OutboundCall call, CancellationToken ct);

    /// <summary>Repeats a read at the address in <see cref="OutboundCall.AbsoluteUri"/>.</summary>
    [Get("/")]
    Task<HttpResponseMessage> Follow([Property(OutboundCall.PropertyName)] OutboundCall call, CancellationToken ct);
}

/// <summary>One page of a Bitbucket list. <c>next</c> is the full address of the next page, or null on the last one.</summary>
internal sealed record BitbucketPageDto<T>(
    [property: JsonPropertyName("values")] List<T?>? Values,
    [property: JsonPropertyName("next")] string? Next);

internal sealed record BitbucketRepositoryDto(
    [property: JsonPropertyName("full_name")] string? FullName,
    [property: JsonPropertyName("is_private")] bool IsPrivate,
    [property: JsonPropertyName("mainbranch")] BitbucketBranchDto? MainBranch);

internal sealed record BitbucketBranchDto([property: JsonPropertyName("name")] string? Name);

internal sealed record BitbucketCommitDto([property: JsonPropertyName("hash")] string? Hash);

internal sealed record BitbucketRefDto(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("target")] BitbucketCommitDto? Target);

/// <param name="Type"><c>commit_file</c> or <c>commit_directory</c>.</param>
/// <param name="Attributes">For a file: <c>link</c> (a symbolic link), <c>executable</c>, <c>binary</c>, <c>lfs</c>.</param>
internal sealed record BitbucketTreeEntryDto(
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("path")] string? Path,
    [property: JsonPropertyName("size")] long? Size,
    [property: JsonPropertyName("attributes")] List<string?>? Attributes);

internal sealed record BitbucketUserDto(
    [property: JsonPropertyName("username")] string? Username,
    [property: JsonPropertyName("nickname")] string? Nickname,
    [property: JsonPropertyName("display_name")] string? DisplayName);
