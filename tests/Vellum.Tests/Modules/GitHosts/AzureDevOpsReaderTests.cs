using System.Net;
using System.Text;
using Vellum.Modules.GitHosts;
using Vellum.Modules.GitHub;
using Vellum.Tests.Shared.Outbound;

namespace Vellum.Tests.Modules.GitHosts;

/// <summary>
/// The Azure DevOps reads through the real Refit client and handler pipeline. The fixtures
/// under <c>Fixtures/AzureDevOps</c> follow the Azure DevOps Services REST API 7.1 reference
/// (Git repositories, refs, commits, trees, blobs, and the connection data of the location service).
/// </summary>
public class AzureDevOpsReaderTests(SharedOutboundHost shared) : IClassFixture<SharedOutboundHost>
{
    private const string Token = "azdopat0fixture0token0123456789abcdefghijklmnopqrst";
    private const string Repository = "/acme/orders%20platform/_apis/git/repositories/orders-api";
    private static readonly string CommitSha = new('a', 40);

    private readonly OutboundHost _host = shared.Host;

    private Task<GitHostScenario> CreateAsync(string? token = Token) =>
        GitHostScenario.CreateAsync(_host, GitHostInfo.AzureDevops, "azureDevops", "", token);

    private static string Fixture(string name) => Fixtures.Read($"AzureDevOps/{name}");

    [Fact]
    public async Task Each_read_sends_the_documented_request_and_returns_a_typed_value()
    {
        using var azure = await CreateAsync();
        azure.Stub
            .Json(Fixture("repository.json"))
            .Json(Fixture("refs-heads.json"))
            .Json("""{"value":[],"count":0}""")
            .Json(Fixture("refs-tags.json"))
            .Json(Fixture("commit.json"))
            .Json(Fixture("commit.json"))
            .Json(Fixture("tree.json"))
            .Json("class OrderService { }\n")
            .Json(Fixture("connection-data.json"))
            .Json(Fixture("repositories.json"));
        var connection = await azure.ConnectAsync();
        const string locator = "acme/orders platform/orders-api";

        var repository = await connection.GetRepositoryAsync(locator, CancellationToken.None);
        var branch = await connection.ResolveRefAsync(locator, "main", CancellationToken.None);
        var tag = await connection.ResolveRefAsync(locator, "v1.0", CancellationToken.None);
        var commit = await connection.ResolveRefAsync(locator, CommitSha, CancellationToken.None);
        var files = await connection.ListFilesAsync(locator, CommitSha, CancellationToken.None);
        var file = await connection.GetFileAsync(locator, CommitSha, "src/Order Service.cs", new string('e', 40), 1024, CancellationToken.None);
        var identity = await connection.IdentifyAsync(CancellationToken.None);
        var listed = await connection.ListRepositoriesAsync("acme/Orders Platform", 1, CancellationToken.None);

        Assert.Equal(new GitHostRepository(locator, "acme/Orders Platform/Orders-Api", "main", true), repository.Value);
        Assert.Equal(CommitSha, branch.Value);
        // An annotated tag gives the commit it points at, not the tag object.
        Assert.Equal(new string('b', 40), tag.Value);
        Assert.Equal(CommitSha, commit.Value);
        Assert.Equal(
        [
            new GitHubTreeEntry("README.md", "blob", new string('b', 40), 9, "100644"),
            new GitHubTreeEntry("src/Order Service.cs", "blob", new string('e', 40), 120, "100644"),
        ], files.Value);
        Assert.Equal("class OrderService { }\n", Encoding.UTF8.GetString(file.Value!));
        Assert.Equal("Orders Bot", identity.Value!.Login);
        Assert.Equal(
        [
            new GitHostRepository("acme/orders platform/billing", "acme/Orders Platform/billing", "trunk", false),
            new GitHostRepository(locator, "acme/Orders Platform/Orders-Api", "main", true),
        ], listed.Value!.Repositories);
        Assert.False(listed.Value.HasMore);

        var sent = azure.Stub.Requests;
        Assert.Equal(
        [
            $"{Repository}?api-version=7.1",
            $"{Repository}/refs?peelTags=true&api-version=7.1&filter=heads%2Fmain",
            $"{Repository}/refs?peelTags=true&api-version=7.1&filter=heads%2Fv1.0",
            $"{Repository}/refs?peelTags=true&api-version=7.1&filter=tags%2Fv1.0",
            $"{Repository}/commits/{CommitSha}?api-version=7.1",
            $"{Repository}/commits/{CommitSha}?api-version=7.1",
            $"{Repository}/trees/{new string('d', 40)}?recursive=true&api-version=7.1",
            $"{Repository}/blobs/{new string('e', 40)}?$format=octetstream&api-version=7.1",
            "/_apis/connectionData",
            "/acme/Orders%20Platform/_apis/git/repositories?api-version=7.1",
        ], sent.Select(s => s.Uri.PathAndQuery));
        var basic = $"Basic {Convert.ToBase64String(Encoding.ASCII.GetBytes(":" + Token))}";
        Assert.All(sent, s =>
        {
            Assert.Equal(HttpMethod.Get, s.Method);
            Assert.Equal(basic, s.Header("Authorization"));
        });
        Assert.Equal("application/octet-stream", sent[7].Header("Accept"));
    }

    [Fact]
    public async Task A_sign_in_page_and_other_http_failures_become_typed_failures()
    {
        using var azure = await CreateAsync();
        var reset = DateTimeOffset.FromUnixTimeSeconds(1767225600);
        azure.Stub
            .Then(() => new HttpResponseMessage(HttpStatusCode.NonAuthoritativeInformation)
            {
                Content = new StringContent(Fixture("sign-in.html"), Encoding.UTF8, "text/html"),
            })
            .Json(HttpStatusCode.Unauthorized, "")
            .Json(HttpStatusCode.NotFound, Fixture("error-404.json"))
            .Json(HttpStatusCode.TooManyRequests, "{}", ("X-RateLimit-Reset", "1767225600"));
        var connection = await azure.ConnectAsync();
        const string locator = "acme/orders/orders";

        Assert.IsType<GitHubFailure.Unauthorized>((await connection.GetRepositoryAsync(locator, CancellationToken.None)).Failure);
        Assert.IsType<GitHubFailure.Unauthorized>((await connection.IdentifyAsync(CancellationToken.None)).Failure);
        Assert.IsType<GitHubFailure.NotFound>((await connection.GetRepositoryAsync("acme/orders/missing", CancellationToken.None)).Failure);
        Assert.Equal(reset, Assert.IsType<GitHubFailure.RateLimited>((await connection.ListFilesAsync(locator, CommitSha, CancellationToken.None)).Failure).ResetAt);
        Assert.Equal(6, azure.Stub.Requests.Count);
    }

    [Fact]
    public async Task A_tree_over_the_entry_cap_is_truncated_and_not_partial()
    {
        using var azure = await CreateAsync();
        var entries = string.Join(',', Enumerable.Range(0, 100_001)
            .Select(i => $$"""{"objectId":"{{new string('b', 40)}}","relativePath":"f{{i}}","gitObjectType":"blob","size":1}"""));
        azure.Stub.Json(Fixture("commit.json")).Json($$"""{"treeEntries":[{{entries}}]}""");

        var files = await (await azure.ConnectAsync()).ListFilesAsync("acme/orders/orders", CommitSha, CancellationToken.None);

        Assert.IsType<GitHubFailure.Truncated>(files.Failure);
        Assert.Null(files.Value);
    }

    [Fact]
    public async Task A_redirect_to_another_origin_is_refused_and_the_token_stays()
    {
        using var azure = await CreateAsync();
        await azure.AssertRedirectOffOriginIsRefusedAsync(_host, "https://evil.test/_signin",
            async connection => (await connection.GetRepositoryAsync("acme/orders/orders", CancellationToken.None)).Failure);
    }
}
