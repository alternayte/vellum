using System.Text.Json.Serialization;
using Refit;
using Vellum.Shared.Outbound;

namespace Vellum.Modules.GitHosts.GitLab;

/// <summary>
/// The GitLab REST calls (API v4) that Vellum makes, on gitlab.com and on a self-managed
/// instance. All are reads. A project or a group is named by its full path as one encoded
/// path part: Refit encodes <c>group/sub/name</c> as <c>group%2Fsub%2Fname</c>, which is the
/// form GitLab documents. Every method returns the raw response; <see cref="GitLabConnection"/>
/// turns it into a typed result.
/// </summary>
[Headers("Accept: application/json", "User-Agent: Vellum")]
internal interface IGitLabApi
{
    [Get("/projects/{project}")]
    Task<HttpResponseMessage> GetProject(string project,
        [Property(OutboundCall.PropertyName)] OutboundCall call, CancellationToken ct);

    /// <summary>One commit by a branch, a tag or a SHA. The ref is one encoded path part, so a slash in it stays part of the ref.</summary>
    [Get("/projects/{project}/repository/commits/{reference}")]
    Task<HttpResponseMessage> GetCommit(string project, string reference,
        [Property(OutboundCall.PropertyName)] OutboundCall call, CancellationToken ct);

    /// <summary>
    /// The first page of the recursive tree of a commit, with keyset pagination: the next page
    /// is the <c>Link</c> header's <c>rel="next"</c> address.
    /// </summary>
    [Get("/projects/{project}/repository/tree?recursive=true&pagination=keyset&per_page=100")]
    Task<HttpResponseMessage> ListTree(string project, [AliasAs("ref")] string reference,
        [Property(OutboundCall.PropertyName)] OutboundCall call, CancellationToken ct);

    [Get("/projects/{project}/repository/blobs/{sha}/raw")]
    [Headers("Accept: */*")]
    Task<HttpResponseMessage> GetRawBlob(string project, string sha,
        [Property(OutboundCall.PropertyName)] OutboundCall call, CancellationToken ct);

    [Get("/user")]
    Task<HttpResponseMessage> GetCurrentUser([Property(OutboundCall.PropertyName)] OutboundCall call, CancellationToken ct);

    /// <summary>One page of the projects of a group and its subgroups, without archived ones.</summary>
    [Get("/groups/{group}/projects?include_subgroups=true&archived=false&order_by=path&sort=asc&per_page=100")]
    Task<HttpResponseMessage> ListGroupProjects(string group, int page,
        [Property(OutboundCall.PropertyName)] OutboundCall call, CancellationToken ct);

    /// <summary>One page of the projects of a user, for an owner that is no group.</summary>
    [Get("/users/{user}/projects?archived=false&order_by=path&sort=asc&per_page=100")]
    Task<HttpResponseMessage> ListUserProjects(string user, int page,
        [Property(OutboundCall.PropertyName)] OutboundCall call, CancellationToken ct);

    /// <summary>Repeats a read at the address in <see cref="OutboundCall.AbsoluteUri"/>.</summary>
    [Get("/")]
    Task<HttpResponseMessage> Follow([Property(OutboundCall.PropertyName)] OutboundCall call, CancellationToken ct);
}

internal sealed record GitLabProjectDto(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("path_with_namespace")] string? PathWithNamespace,
    [property: JsonPropertyName("default_branch")] string? DefaultBranch,
    [property: JsonPropertyName("visibility")] string? Visibility,
    [property: JsonPropertyName("archived")] bool Archived);

internal sealed record GitLabCommitDto([property: JsonPropertyName("id")] string? Id);

internal sealed record GitLabTreeEntryDto(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("path")] string? Path,
    [property: JsonPropertyName("mode")] string? Mode);

internal sealed record GitLabUserDto([property: JsonPropertyName("username")] string? Username);
