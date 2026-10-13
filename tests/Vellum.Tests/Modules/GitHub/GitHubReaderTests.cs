using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Vellum.Modules.GitHub;
using Vellum.Tests.Shared.Outbound;

namespace Vellum.Tests.Modules.GitHub;

/// <summary>
/// The GitHub reads through the real Refit client and handler pipeline. The transport serves
/// the fixtures under <c>Fixtures/GitHub</c>, which are written from GitHub's REST reference.
/// </summary>
[Collection("Integration")]
public class GitHubReaderTests(SharedOutboundHost shared) : IClassFixture<SharedOutboundHost>
{
    private const string Token = "ghp_fixtureTOKEN0123456789abcdefghijABCD";
    private static readonly string CommitSha = new('a', 40);
    private static readonly string BlobSha = new('b', 40);

    private readonly OutboundHost _host = shared.Host;

    private sealed record Scenario(HttpClient Owner, Guid WorkspaceId, string HostName, StubTransport Stub, IServiceScope Scope) : IDisposable
    {
        public async Task<GitHubConnection> ConnectAsync()
        {
            var connection = await Scope.ServiceProvider.GetRequiredService<GitHubReader>().ConnectAsync(WorkspaceId);
            Assert.True(connection.Ok);
            return connection.Value!;
        }

        public void Dispose() => Scope.Dispose();
    }

    private async Task<Scenario> CreateAsync(bool withToken = true, string path = "")
    {
        var (owner, workspaceId) = await _host.NewWorkspaceAsync();
        var hostName = $"{Guid.NewGuid():N}.gh.test";
        await OutboundHost.PutSettingsAsync(owner, workspaceId, new JsonObject { ["github"] = new JsonObject { ["apiBaseUrl"] = $"https://{hostName}{path}" } });
        if (withToken)
            await OutboundHost.PutSecretAsync(owner, workspaceId, "github:token", Token);
        return new Scenario(owner, workspaceId, hostName, _host.Transport.For(hostName), _host.Factory.Services.CreateScope());
    }

    private static string Fixture(string name) => Fixtures.Read($"GitHub/{name}");

    [Fact]
    public async Task Each_read_sends_the_documented_request_and_returns_a_typed_value()
    {
        using var github = await CreateAsync(path: "/api/v3");
        github.Stub
            .Json(Fixture("repository.json"))
            .Json("""[{"sha":"SHA","commit":{"message":"Add outbox"}}]""".Replace("SHA", CommitSha))
            .Json(Fixture("tree.json"))
            .Json(Fixture("blob.json"))
            .Json(Fixture("compare.json"))
            .Json(Fixture("pull-request.json"))
            .Json(Fixture("rate-limit.json"));
        var connection = await github.ConnectAsync();

        var repository = await connection.GetRepositoryAsync("acme", "orders");
        var sha = await connection.ResolveRefAsync("acme", "orders", "heads/feature/outbox");
        var tree = await connection.GetTreeAsync("acme", "orders", CommitSha);
        var blob = await connection.GetBlobAsync("acme", "orders", BlobSha, maxBytes: 1024);
        var comparison = await connection.CompareAsync("acme", "orders", new string('1', 40), new string('2', 40));
        var pull = await connection.GetPullRequestAsync("acme", "orders", 7);
        var rate = await connection.GetRateLimitAsync();

        Assert.Equal(new GitHubRepository("acme/orders", "main", true, false, 1296269), repository.Value);
        Assert.Equal(CommitSha, sha.Value);
        Assert.Equal(CommitSha, tree.Value!.Sha);
        Assert.Equal(["README.md", "src", "src/Program.cs"], tree.Value.Entries.Select(e => e.Path));
        Assert.Equal(("blob", 120L), (tree.Value.Entries[2].Type, tree.Value.Entries[2].Size));
        Assert.Equal("# Orders\nHello from fixture\n", Encoding.UTF8.GetString(blob.Value!.Content));
        Assert.Equal(("ahead", 1, new string('1', 40), false),
            (comparison.Value!.Status, comparison.Value.TotalCommits, comparison.Value.MergeBaseSha, comparison.Value.FilesIncomplete));
        Assert.Equal(new GitHubChangedFile("src/Orders.cs", "renamed", new string('f', 40), "src/Order.cs"), comparison.Value.Files[1]);
        Assert.Equal(new GitHubPullRequest(7, "open", false, "main", new string('1', 40), "remove-outbox", new string('2', 40), "contributor/orders"), pull.Value);
        Assert.Equal((60, 57, DateTimeOffset.FromUnixTimeSeconds(1767225600)), (rate.Value!.Limit, rate.Value.Remaining, rate.Value.ResetAt));

        var sent = github.Stub.Requests;
        Assert.Equal(
        [
            "/api/v3/repos/acme/orders",
            "/api/v3/repos/acme/orders/commits?sha=heads%2Ffeature%2Foutbox&per_page=1",
            $"/api/v3/repos/acme/orders/git/trees/{CommitSha}?recursive=1",
            $"/api/v3/repos/acme/orders/git/blobs/{BlobSha}",
            $"/api/v3/repos/acme/orders/compare/{new string('1', 40)}...{new string('2', 40)}?per_page=1&page=1",
            "/api/v3/repos/acme/orders/pulls/7",
            "/api/v3/rate_limit",
        ], sent.Select(s => s.Uri.PathAndQuery));
        Assert.All(sent, s =>
        {
            Assert.Equal(HttpMethod.Get, s.Method);
            Assert.Equal($"Bearer {Token}", s.Header("Authorization"));
            Assert.Equal("2022-11-28", s.Header("X-GitHub-Api-Version"));
            Assert.Equal("Vellum", s.Header("User-Agent"));
        });
        Assert.All(sent, s => Assert.Equal("application/vnd.github+json", s.Header("Accept")));
    }

    [Fact]
    public async Task Without_a_token_the_calls_are_anonymous()
    {
        using var github = await CreateAsync(withToken: false);
        github.Stub.Json(Fixture("repository.json"));
        var connection = await github.ConnectAsync();

        Assert.False(connection.HasToken);
        Assert.True((await connection.GetRepositoryAsync("acme", "orders")).Ok);
        Assert.Null(Assert.Single(github.Stub.Requests).Header("Authorization"));
    }

    [Fact]
    public async Task A_truncated_tree_is_a_failure_and_not_a_partial_tree()
    {
        using var github = await CreateAsync();
        github.Stub.Json(Fixture("tree-truncated.json"));

        var tree = await (await github.ConnectAsync()).GetTreeAsync("acme", "orders", CommitSha);

        Assert.IsType<GitHubFailure.Truncated>(tree.Failure);
        Assert.Null(tree.Value);
    }

    [Fact]
    public async Task A_comparison_that_reaches_the_file_limit_says_that_files_can_be_missing()
    {
        using var github = await CreateAsync();
        var compare = JsonNode.Parse(Fixture("compare.json"))!.AsObject();
        compare["files"] = new JsonArray(Enumerable.Range(0, 300)
            .Select(i => (JsonNode)new JsonObject { ["filename"] = $"src/File{i}.cs", ["status"] = "modified", ["sha"] = new string('e', 40) }).ToArray());
        github.Stub.Json(compare.ToJsonString());

        var comparison = await (await github.ConnectAsync()).CompareAsync("acme", "orders", new string('1', 40), new string('2', 40));

        Assert.True(comparison.Value!.FilesIncomplete);
        Assert.Equal(300, comparison.Value.Files.Count);
    }

    [Fact]
    public async Task Http_failures_become_typed_failures()
    {
        using var github = await CreateAsync();
        var reset = DateTimeOffset.FromUnixTimeSeconds(1767225600);
        github.Stub
            .Json(HttpStatusCode.NotFound, Fixture("error-404.json"))
            .Json(HttpStatusCode.Unauthorized, Fixture("error-401.json"))
            .Json(HttpStatusCode.Forbidden, Fixture("error-403-rate-limit.json"), ("x-ratelimit-remaining", "0"), ("x-ratelimit-reset", "1767225600"))
            .Json(HttpStatusCode.Forbidden, Fixture("error-403.json"), ("x-ratelimit-remaining", "4999"))
            .Json(HttpStatusCode.Conflict, """{"message":"Git Repository is empty.","status":"409"}""")
            .Json(HttpStatusCode.InternalServerError, "{}")
            .Json("""{"unexpected":true}""")
            .Throw(() => new HttpRequestException(HttpRequestError.ResponseEnded, "reset"));
        var connection = await github.ConnectAsync();
        _host.Waits.Clear();

        Assert.IsType<GitHubFailure.NotFound>((await connection.GetRepositoryAsync("acme", "missing")).Failure);
        Assert.IsType<GitHubFailure.Unauthorized>((await connection.GetRepositoryAsync("acme", "orders")).Failure);
        Assert.Equal(reset, Assert.IsType<GitHubFailure.RateLimited>((await connection.GetTreeAsync("acme", "orders", CommitSha)).Failure).ResetAt);
        Assert.IsType<GitHubFailure.Forbidden>((await connection.GetRepositoryAsync("acme", "orders")).Failure);
        Assert.IsType<GitHubFailure.NotFound>((await connection.ResolveRefAsync("acme", "empty", "main")).Failure);
        Assert.Equal("server_error", Assert.IsType<GitHubFailure.Unavailable>((await connection.GetRepositoryAsync("acme", "orders")).Failure).Reason);
        Assert.Equal("unexpected_response", Assert.IsType<GitHubFailure.Unavailable>((await connection.GetRepositoryAsync("acme", "orders")).Failure).Reason);
        Assert.Equal("network", Assert.IsType<GitHubFailure.Unavailable>((await connection.GetRepositoryAsync("acme", "orders")).Failure).Reason);
        Assert.Equal(8, github.Stub.Requests.Count);
    }

    [Fact]
    public async Task A_secondary_rate_limit_is_retried_as_github_asks_and_then_reported()
    {
        using var github = await CreateAsync();
        github.Stub
            .Json(HttpStatusCode.TooManyRequests, Fixture("error-403-rate-limit.json"), ("Retry-After", "3"))
            .Json(HttpStatusCode.TooManyRequests, Fixture("error-403-rate-limit.json"), ("Retry-After", "3"))
            .Json(Fixture("repository.json"))
            .Json(HttpStatusCode.Forbidden, Fixture("error-403-rate-limit.json"), ("Retry-After", "60"));
        var connection = await github.ConnectAsync();
        _host.Waits.Clear();

        Assert.True((await connection.GetRepositoryAsync("acme", "orders")).Ok);
        Assert.Equal([TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3)], _host.Waits.ToArray());

        var before = DateTimeOffset.UtcNow;
        var limited = Assert.IsType<GitHubFailure.RateLimited>((await connection.GetRepositoryAsync("acme", "orders")).Failure);
        Assert.InRange(limited.ResetAt!.Value, before.AddSeconds(59), before.AddSeconds(62));
    }

    [Fact]
    public async Task A_server_fault_on_a_read_is_retried_within_bounds()
    {
        using var github = await CreateAsync();
        github.Stub.Json(HttpStatusCode.BadGateway, "{}").Json(HttpStatusCode.GatewayTimeout, "{}").Json(Fixture("repository.json"));

        Assert.True((await (await github.ConnectAsync()).GetRepositoryAsync("acme", "orders")).Ok);
        Assert.Equal(3, github.Stub.Requests.Count);
    }

    [Fact]
    public async Task A_redirect_on_the_api_origin_is_followed_and_checked_again()
    {
        using var github = await CreateAsync();
        github.Stub
            .Json(HttpStatusCode.MovedPermanently, """{"message":"Moved Permanently"}""", ("Location", $"https://{github.HostName}/repositories/1296269"))
            .Json(Fixture("repository.json"));

        var repository = await (await github.ConnectAsync()).GetRepositoryAsync("acme", "old-name");

        Assert.Equal("acme/orders", repository.Value!.FullName);
        Assert.Equal("/repositories/1296269", github.Stub.Requests[1].Uri.AbsolutePath);
        Assert.Equal($"Bearer {Token}", github.Stub.Requests[1].Header("Authorization"));
        Assert.Equal("application/vnd.github+json", github.Stub.Requests[1].Header("Accept"));
    }

    [Theory]
    [InlineData("https://evil.test/repositories/1")]
    [InlineData("http://169.254.169.254/latest/meta-data/")]
    public async Task A_redirect_to_another_origin_is_refused_and_the_token_stays(string location)
    {
        using var github = await CreateAsync();
        var elsewhere = _host.Transport.For(new Uri(location).Host);
        try
        {
            github.Stub.Json(HttpStatusCode.Found, "{}", ("Location", location));

            var repository = await (await github.ConnectAsync()).GetRepositoryAsync("acme", "orders");

            Assert.Equal("redirect_refused", Assert.IsType<GitHubFailure.Unavailable>(repository.Failure).Reason);
            Assert.Single(github.Stub.Requests);
            Assert.Empty(elsewhere.Requests);
        }
        finally
        {
            _host.Transport.Forget(new Uri(location).Host);
        }
    }

    [Fact]
    public async Task A_redirect_loop_ends()
    {
        using var github = await CreateAsync();
        github.Stub.Json(HttpStatusCode.TemporaryRedirect, "{}", ("Location", "/repos/acme/orders"));

        var repository = await (await github.ConnectAsync()).GetRepositoryAsync("acme", "orders");

        Assert.Equal("redirect_refused", Assert.IsType<GitHubFailure.Unavailable>(repository.Failure).Reason);
        Assert.Equal(4, github.Stub.Requests.Count);
    }

    [Fact]
    public async Task A_blob_over_the_cap_is_too_large_by_its_size_field_and_by_its_bytes()
    {
        using var github = await CreateAsync();
        var huge = JsonNode.Parse(Fixture("blob.json"))!.AsObject();
        huge["size"] = 1;
        huge["content"] = Convert.ToBase64String(new byte[200_000]);
        github.Stub.Json(Fixture("blob.json")).Json(huge.ToJsonString());
        var connection = await github.ConnectAsync();

        // The fixture's blob is 28 bytes.
        Assert.IsType<GitHubFailure.TooLarge>((await connection.GetBlobAsync("acme", "orders", BlobSha, maxBytes: 27)).Failure);
        // A body far over the cap is cut off by the pipeline whatever its size field says.
        Assert.IsType<GitHubFailure.TooLarge>((await connection.GetBlobAsync("acme", "orders", BlobSha, maxBytes: 1024)).Failure);
    }

    [Fact]
    public async Task Names_that_cannot_be_a_repository_a_ref_or_a_sha_send_nothing()
    {
        using var github = await CreateAsync();
        var connection = await github.ConnectAsync();

        Assert.IsType<GitHubFailure.NotFound>((await connection.GetRepositoryAsync("..", "orders")).Failure);
        Assert.IsType<GitHubFailure.NotFound>((await connection.GetRepositoryAsync("acme", "orders/../../user")).Failure);
        Assert.IsType<GitHubFailure.NotFound>((await connection.ResolveRefAsync("acme", "orders", "main/../../../user")).Failure);
        Assert.IsType<GitHubFailure.NotFound>((await connection.ResolveRefAsync("acme", "orders", "main?recursive=1")).Failure);
        Assert.IsType<GitHubFailure.NotFound>((await connection.GetTreeAsync("acme", "orders", "main")).Failure);
        Assert.IsType<GitHubFailure.NotFound>((await connection.GetBlobAsync("acme", "orders", "not-a-sha", 10)).Failure);
        Assert.Empty(github.Stub.Requests);
    }

    [Fact]
    public async Task The_token_reaches_no_log_line()
    {
        using var github = await CreateAsync();
        github.Stub
            .Json(Fixture("repository.json"))
            .Json(HttpStatusCode.Unauthorized, Fixture("error-401.json"))
            .Throw(() => new HttpRequestException(HttpRequestError.ResponseEnded, $"reset {Token}"));
        var connection = await github.ConnectAsync();
        _host.Logs.Clear();

        await connection.GetRepositoryAsync("acme", "orders");
        await connection.GetRepositoryAsync("acme", "orders");
        await connection.GetRepositoryAsync("acme", "orders");

        var lines = _host.Logs.Lines;
        Assert.Equal(3, lines.Count(line => line.Contains("Outbound github get-repository ended", StringComparison.Ordinal)));
        Assert.All(lines, line =>
        {
            Assert.DoesNotContain(Token, line);
            Assert.DoesNotContain(github.HostName, line);
        });
    }

    [Fact]
    public async Task The_test_endpoint_reports_who_the_token_belongs_to()
    {
        using var github = await CreateAsync();
        github.Stub.Json(HttpStatusCode.OK, Fixture("user.json"), ("x-ratelimit-remaining", "4987"));

        var response = await github.Owner.PostAsync($"/api/workspaces/{github.WorkspaceId}/github/test", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var test = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.Equal(["ok", "authenticatedAs", "rateLimitRemaining", "error"], test.Select(p => p.Key));
        Assert.True(test["ok"]!.GetValue<bool>());
        Assert.Equal("octocat", test["authenticatedAs"]!.GetValue<string>());
        Assert.Equal(4987, test["rateLimitRemaining"]!.GetValue<int>());
        Assert.Null(test["error"]);
        Assert.Equal("/user", Assert.Single(github.Stub.Requests).Uri.AbsolutePath);
    }

    [Fact]
    public async Task The_test_endpoint_works_without_a_token_and_names_a_failure()
    {
        using var anonymous = await CreateAsync(withToken: false);
        anonymous.Stub.Json(Fixture("rate-limit.json"));
        using var refused = await CreateAsync();
        refused.Stub.Json(HttpStatusCode.Unauthorized, Fixture("error-401.json"));

        var open = await (await anonymous.Owner.PostAsync($"/api/workspaces/{anonymous.WorkspaceId}/github/test", null)).Content.ReadFromJsonAsync<JsonObject>();
        var bad = await (await refused.Owner.PostAsync($"/api/workspaces/{refused.WorkspaceId}/github/test", null)).Content.ReadFromJsonAsync<JsonObject>();

        Assert.True(open!["ok"]!.GetValue<bool>());
        Assert.Null(open["authenticatedAs"]);
        Assert.Equal(57, open["rateLimitRemaining"]!.GetValue<int>());
        Assert.Equal("/rate_limit", Assert.Single(anonymous.Stub.Requests).Uri.AbsolutePath);

        Assert.False(bad!["ok"]!.GetValue<bool>());
        Assert.Equal("unauthorized", bad["error"]!["code"]!.GetValue<string>());
        Assert.Contains("Save a new token", bad["error"]!["message"]!.GetValue<string>());
    }
}
