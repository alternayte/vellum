using System.Text.Json.Serialization;
using Refit;
using Vellum.Shared.Outbound;

namespace Vellum.Modules.GitHosts.AzureDevOps;

/// <summary>
/// The Azure DevOps Repos REST calls (api-version 7.1) that Vellum makes, on dev.azure.com and
/// on Azure DevOps Server, where the organization is the collection. All are reads. Each name
/// is one encoded path part, so a project or repository name with a space is sent as
/// <c>%20</c>. Every method returns the raw response; <see cref="AzureDevOpsConnection"/>
/// turns it into a typed result.
/// </summary>
[Headers("Accept: application/json", "User-Agent: Vellum")]
internal interface IAzureDevOpsApi
{
    [Get("/{organization}/{project}/_apis/git/repositories/{repository}?api-version=7.1")]
    Task<HttpResponseMessage> GetRepository(string organization, string project, string repository,
        [Property(OutboundCall.PropertyName)] OutboundCall call, CancellationToken ct);

    [Get("/{organization}/{project}/_apis/git/repositories/{repository}/commits/{commit}?api-version=7.1")]
    Task<HttpResponseMessage> GetCommit(string organization, string project, string repository, string commit,
        [Property(OutboundCall.PropertyName)] OutboundCall call, CancellationToken ct);

    /// <summary>
    /// The refs whose names start with <c>refs/</c> and <paramref name="filter"/>, such as
    /// <c>heads/main</c>. <c>peelTags</c> adds the commit an annotated tag points at.
    /// </summary>
    [Get("/{organization}/{project}/_apis/git/repositories/{repository}/refs?peelTags=true&api-version=7.1")]
    Task<HttpResponseMessage> ListRefs(string organization, string project, string repository, string filter,
        [Property(OutboundCall.PropertyName)] OutboundCall call, CancellationToken ct);

    [Get("/{organization}/{project}/_apis/git/repositories/{repository}/trees/{tree}?recursive=true&api-version=7.1")]
    Task<HttpResponseMessage> GetTree(string organization, string project, string repository, string tree,
        [Property(OutboundCall.PropertyName)] OutboundCall call, CancellationToken ct);

    [Get("/{organization}/{project}/_apis/git/repositories/{repository}/blobs/{sha}?$format=octetstream&api-version=7.1")]
    [Headers("Accept: application/octet-stream")]
    Task<HttpResponseMessage> GetBlob(string organization, string project, string repository, string sha,
        [Property(OutboundCall.PropertyName)] OutboundCall call, CancellationToken ct);

    /// <summary>
    /// The identity that the request authenticated as, at the configured address itself: the
    /// deployment of dev.azure.com, or the collection of an Azure DevOps Server.
    /// </summary>
    [Get("/_apis/connectionData")]
    Task<HttpResponseMessage> GetConnectionData([Property(OutboundCall.PropertyName)] OutboundCall call, CancellationToken ct);

    [Get("/{organization}/_apis/git/repositories?api-version=7.1")]
    Task<HttpResponseMessage> ListOrganizationRepositories(string organization,
        [Property(OutboundCall.PropertyName)] OutboundCall call, CancellationToken ct);

    [Get("/{organization}/{project}/_apis/git/repositories?api-version=7.1")]
    Task<HttpResponseMessage> ListProjectRepositories(string organization, string project,
        [Property(OutboundCall.PropertyName)] OutboundCall call, CancellationToken ct);

    /// <summary>Repeats a read at the address in <see cref="OutboundCall.AbsoluteUri"/>.</summary>
    [Get("/")]
    Task<HttpResponseMessage> Follow([Property(OutboundCall.PropertyName)] OutboundCall call, CancellationToken ct);
}

/// <summary>A list answer of Azure DevOps: <c>{"value": [...], "count": n}</c>.</summary>
internal sealed record AzureListDto<T>([property: JsonPropertyName("value")] List<T?>? Value);

internal sealed record AzureRepositoryDto(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("defaultBranch")] string? DefaultBranch,
    [property: JsonPropertyName("isDisabled")] bool IsDisabled,
    [property: JsonPropertyName("project")] AzureProjectDto? Project);

/// <param name="Visibility"><c>private</c> or <c>public</c>.</param>
internal sealed record AzureProjectDto(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("visibility")] string? Visibility);

internal sealed record AzureCommitDto(
    [property: JsonPropertyName("commitId")] string? CommitId,
    [property: JsonPropertyName("treeId")] string? TreeId);

/// <param name="PeeledObjectId">For an annotated tag: the commit it points at. <c>objectId</c> is then the tag object.</param>
internal sealed record AzureRefDto(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("objectId")] string? ObjectId,
    [property: JsonPropertyName("peeledObjectId")] string? PeeledObjectId);

internal sealed record AzureTreeDto([property: JsonPropertyName("treeEntries")] List<AzureTreeEntryDto?>? TreeEntries);

/// <param name="GitObjectType"><c>blob</c>, <c>tree</c> or <c>commit</c> (a submodule).</param>
internal sealed record AzureTreeEntryDto(
    [property: JsonPropertyName("relativePath")] string? RelativePath,
    [property: JsonPropertyName("objectId")] string? ObjectId,
    [property: JsonPropertyName("gitObjectType")] string? GitObjectType,
    [property: JsonPropertyName("size")] long? Size,
    [property: JsonPropertyName("mode")] string? Mode);

internal sealed record AzureConnectionDataDto([property: JsonPropertyName("authenticatedUser")] AzureIdentityDto? AuthenticatedUser);

internal sealed record AzureIdentityDto([property: JsonPropertyName("providerDisplayName")] string? ProviderDisplayName);
