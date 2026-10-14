using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Vellum.Modules.GitHosts;
using Vellum.Modules.GitHub;
using Vellum.Tests.Shared.Outbound;

namespace Vellum.Tests.Modules.GitHosts;

/// <summary>
/// The GitLab reads through the real Refit client and handler pipeline. The fixtures under
/// <c>Fixtures/GitLab</c> follow the GitLab REST API v4 reference (projects, commits,
/// repository tree, blobs, users, groups).
/// </summary>
public class GitLabReaderTests(SharedOutboundHost shared) : IClassFixture<SharedOutboundHost>
{
    private const string Token = "glpat-fixtureTOKEN0123456789";
    private const string Project = "acme%2Fplatform%2Forders";
    private static readonly string CommitSha = new('a', 40);
    private static readonly string BlobSha = new('b', 40);

    private readonly OutboundHost _host = shared.Host;

    private Task<GitHostScenario> CreateAsync(string? token = Token) =>
        GitHostScenario.CreateAsync(_host, GitHostInfo.Gitlab, "gitlab", "/api/v4", token);

    private static string Fixture(string name) => Fixtures.Read($"GitLab/{name}");

    [Fact]
    public async Task Each_read_sends_the_documented_request_and_returns_a_typed_value()
    {
        using var gitlab = await CreateAsync();
        var nextPage = $"https://{gitlab.HostName}/api/v4/projects/4021/repository/tree?id=4021&page_token=src&pagination=keyset&per_page=100&recursive=true&ref={CommitSha}";
        gitlab.Stub
            .Json(Fixture("project.json"))
            .Json(Fixture("commit.json"))
            .Json(HttpStatusCode.OK, Fixture("tree-page-1.json"), ("Link", $"<{nextPage}>; rel=\"next\""))
            .Json(Fixture("tree-page-2.json"))
            .Json("# Orders\n")
            .Json(Fixture("user.json"))
            .Json(HttpStatusCode.OK, Fixture("group-projects.json"), ("X-Next-Page", "2"))
            .Json(HttpStatusCode.NotFound, Fixture("error-404.json"))
            .Json(HttpStatusCode.OK, "[]", ("X-Next-Page", ""));
        var connection = await gitlab.ConnectAsync();

        var repository = await connection.GetRepositoryAsync("acme/platform/orders", CancellationToken.None);
        var sha = await connection.ResolveRefAsync("acme/platform/orders", "feature/outbox", CancellationToken.None);
        var files = await connection.ListFilesAsync("acme/platform/orders", CommitSha, CancellationToken.None);
        var file = await connection.GetFileAsync("acme/platform/orders", CommitSha, "README.md", BlobSha, 1024, CancellationToken.None);
        var identity = await connection.IdentifyAsync(CancellationToken.None);
        var group = await connection.ListRepositoriesAsync("acme/platform", 1, CancellationToken.None);
        var user = await connection.ListRepositoriesAsync("octo", 2, CancellationToken.None);

        Assert.True(connection.HasToken);
        Assert.Equal(new GitHostRepository("acme/platform/orders", "Acme/platform/Orders", "main", true), repository.Value);
        Assert.Equal(CommitSha, sha.Value);
        Assert.Equal(
            [new GitHubTreeEntry("README.md", "blob", BlobSha, null, "100644"), new GitHubTreeEntry("src/Program.cs", "blob", new string('d', 40), null, "100755")],
            files.Value);
        Assert.Equal("# Orders\n", Encoding.UTF8.GetString(file.Value!));
        Assert.Equal("orders-bot", identity.Value!.Login);
        Assert.Equal(
            [new GitHostRepository("acme/platform/orders", "Acme/platform/Orders", "main", true), new GitHostRepository("acme/platform/billing", "acme/platform/billing", "trunk", false)],
            group.Value!.Repositories);
        Assert.True(group.Value.HasMore);
        Assert.Empty(user.Value!.Repositories);
        Assert.False(user.Value.HasMore);

        var sent = gitlab.Stub.Requests;
        Assert.Equal(
        [
            $"/api/v4/projects/{Project}",
            $"/api/v4/projects/{Project}/repository/commits/feature%2Foutbox",
            $"/api/v4/projects/{Project}/repository/tree?recursive=true&pagination=keyset&per_page=100&ref={CommitSha}",
            new Uri(nextPage).PathAndQuery,
            $"/api/v4/projects/{Project}/repository/blobs/{BlobSha}/raw",
            "/api/v4/user",
            "/api/v4/groups/acme%2Fplatform/projects?include_subgroups=true&archived=false&order_by=path&sort=asc&per_page=100&page=1",
            "/api/v4/groups/octo/projects?include_subgroups=true&archived=false&order_by=path&sort=asc&per_page=100&page=2",
            "/api/v4/users/octo/projects?archived=false&order_by=path&sort=asc&per_page=100&page=2",
        ], sent.Select(s => s.Uri.PathAndQuery));
        Assert.All(sent, s =>
        {
            Assert.Equal(HttpMethod.Get, s.Method);
            Assert.Equal(Token, s.Header("PRIVATE-TOKEN"));
            Assert.Null(s.Header("Authorization"));
        });
    }

    [Fact]
    public async Task Without_a_token_the_reads_are_anonymous_and_the_identity_is_none()
    {
        using var gitlab = await CreateAsync(token: null);
        gitlab.Stub.Json(Fixture("project.json"));
        var connection = await gitlab.ConnectAsync();

        Assert.False(connection.HasToken);
        Assert.True((await connection.GetRepositoryAsync("acme/platform/orders", CancellationToken.None)).Ok);
        Assert.Null((await connection.IdentifyAsync(CancellationToken.None)).Value!.Login);
        Assert.Null(Assert.Single(gitlab.Stub.Requests).Header("PRIVATE-TOKEN"));
    }

    [Fact]
    public async Task Http_failures_become_typed_failures()
    {
        using var gitlab = await CreateAsync();
        var reset = DateTimeOffset.FromUnixTimeSeconds(1767225600);
        gitlab.Stub
            .Json(HttpStatusCode.NotFound, Fixture("error-404.json"))
            .Json(HttpStatusCode.Unauthorized, Fixture("error-401.json"))
            .Json(HttpStatusCode.TooManyRequests, """{"message":"Retry later"}""", ("Retry-After", "60"), ("RateLimit-Reset", "1767225600"))
            .Json(HttpStatusCode.TooManyRequests, """{"message":"Retry later"}""", ("RateLimit-Reset", "1767225600"));
        var connection = await gitlab.ConnectAsync();

        Assert.IsType<GitHubFailure.NotFound>((await connection.GetRepositoryAsync("acme/missing", CancellationToken.None)).Failure);
        Assert.IsType<GitHubFailure.Unauthorized>((await connection.IdentifyAsync(CancellationToken.None)).Failure);

        // Retry-After is longer than the pipeline waits, so the first 429 is the answer.
        var before = DateTimeOffset.UtcNow;
        var waited = Assert.IsType<GitHubFailure.RateLimited>((await connection.GetRepositoryAsync("acme/orders", CancellationToken.None)).Failure);
        Assert.InRange(waited.ResetAt!.Value, before.AddSeconds(59), before.AddSeconds(62));

        // Without Retry-After the pipeline retries; the last 429 gives GitLab's reset time.
        Assert.Equal(reset, Assert.IsType<GitHubFailure.RateLimited>((await connection.ResolveRefAsync("acme/orders", "main", CancellationToken.None)).Failure).ResetAt);
        Assert.Equal(6, gitlab.Stub.Requests.Count);
    }

    [Fact]
    public async Task A_link_to_a_next_page_on_another_origin_is_not_followed()
    {
        using var gitlab = await CreateAsync();
        gitlab.Stub.Json(HttpStatusCode.OK, Fixture("tree-page-1.json"), ("Link", "<https://evil.test/api/v4/projects/1/repository/tree?page=2>; rel=\"next\""));

        var files = await (await gitlab.ConnectAsync()).ListFilesAsync("acme/orders", CommitSha, CancellationToken.None);

        Assert.Equal("redirect_refused", Assert.IsType<GitHubFailure.Unavailable>(files.Failure).Reason);
        Assert.Single(gitlab.Stub.Requests);
    }

    [Fact]
    public async Task A_redirect_on_the_api_origin_is_followed_with_the_token()
    {
        using var gitlab = await CreateAsync();
        gitlab.Stub
            .Json(HttpStatusCode.MovedPermanently, "{}", ("Location", $"https://{gitlab.HostName}/api/v4/projects/4021"))
            .Json(Fixture("project.json"));

        var repository = await (await gitlab.ConnectAsync()).GetRepositoryAsync("acme/old-orders", CancellationToken.None);

        Assert.Equal("acme/platform/orders", repository.Value!.Locator);
        Assert.Equal("/api/v4/projects/4021", gitlab.Stub.Requests[1].Uri.AbsolutePath);
        Assert.Equal(Token, gitlab.Stub.Requests[1].Header("PRIVATE-TOKEN"));
    }

    [Fact]
    public async Task A_redirect_to_another_origin_is_refused_and_the_token_stays()
    {
        using var gitlab = await CreateAsync();
        await gitlab.AssertRedirectOffOriginIsRefusedAsync(_host, "https://evil.test/api/v4/projects/1",
            async connection => (await connection.GetRepositoryAsync("acme/orders", CancellationToken.None)).Failure);
    }

    [Fact]
    public async Task A_blob_over_the_cap_is_too_large()
    {
        using var gitlab = await CreateAsync();
        gitlab.Stub.Json(new string('x', 2048));

        var file = await (await gitlab.ConnectAsync()).GetFileAsync("acme/orders", CommitSha, "big.txt", BlobSha, 1024, CancellationToken.None);

        Assert.IsType<GitHubFailure.TooLarge>(file.Failure);
    }

    [Fact]
    public async Task The_test_endpoint_reports_who_the_token_belongs_to_and_names_a_failure()
    {
        using var gitlab = await CreateAsync();
        gitlab.Stub.Json(Fixture("user.json")).Json(HttpStatusCode.Unauthorized, Fixture("error-401.json"));
        var url = $"/api/workspaces/{gitlab.WorkspaceId}/git-hosts/gitlab/test";

        var ok = (await (await gitlab.Owner.PostAsync(url, null)).Content.ReadFromJsonAsync<JsonObject>())!;
        var bad = (await (await gitlab.Owner.PostAsync(url, null)).Content.ReadFromJsonAsync<JsonObject>())!;
        var unknown = await gitlab.Owner.PostAsync($"/api/workspaces/{gitlab.WorkspaceId}/git-hosts/github/test", null);

        Assert.Equal(["ok", "authenticatedAs", "error"], ok.Select(p => p.Key));
        Assert.True(ok["ok"]!.GetValue<bool>());
        Assert.Equal("orders-bot", ok["authenticatedAs"]!.GetValue<string>());
        Assert.False(bad["ok"]!.GetValue<bool>());
        Assert.Equal("unauthorized", bad["error"]!["code"]!.GetValue<string>());
        Assert.Equal("GitLab refused the token. Save a new token in the workspace secret gitlab:token.", bad["error"]!["message"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(2, gitlab.Stub.Requests.Count);
    }
}
