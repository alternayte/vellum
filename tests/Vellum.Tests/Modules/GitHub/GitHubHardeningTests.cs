using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Vellum.Modules.GitHub;
using Vellum.Tests.Shared.Outbound;

namespace Vellum.Tests.Modules.GitHub;

/// <summary>The GitHub reads against names, refs and answers that are built to mislead them.</summary>
public class GitHubHardeningTests(SharedOutboundHost shared) : IClassFixture<SharedOutboundHost>
{
    private const string Token = "ghp_fixtureTOKEN0123456789abcdefghijABCD";
    private static readonly string CommitSha = new('a', 40);
    private static readonly string BlobSha = new('b', 40);

    private readonly OutboundHost _host = shared.Host;

    private sealed record Scenario(HttpClient Owner, Guid WorkspaceId, StubTransport Stub, IServiceScope Scope) : IDisposable
    {
        public async Task<GitHubConnection> ConnectAsync()
        {
            var connection = await Scope.ServiceProvider.GetRequiredService<GitHubReader>().ConnectAsync(WorkspaceId);
            Assert.True(connection.Ok);
            return connection.Value!;
        }

        public void Dispose() => Scope.Dispose();
    }

    private async Task<Scenario> CreateAsync()
    {
        var (owner, workspaceId) = await _host.NewWorkspaceAsync();
        var hostName = $"{Guid.NewGuid():N}.gh.test";
        await OutboundHost.PutSettingsAsync(owner, workspaceId, new JsonObject { ["github"] = new JsonObject { ["apiBaseUrl"] = $"https://{hostName}" } });
        await OutboundHost.PutSecretAsync(owner, workspaceId, "github:token", Token);
        return new Scenario(owner, workspaceId, _host.Transport.For(hostName), _host.Factory.Services.CreateScope());
    }

    private static string Fixture(string name) => Fixtures.Read($"GitHub/{name}");

    [Theory]
    [InlineData("orders\n")]
    [InlineData("orders\r\nX-Evil: 1")]
    [InlineData("ord\0ers")]
    [InlineData("orders\u0085")]
    [InlineData("")]
    public async Task A_name_or_a_sha_with_a_line_break_or_a_control_character_sends_nothing(string suffixed)
    {
        using var github = await CreateAsync();
        var connection = await github.ConnectAsync();
        var badSha = CommitSha + (suffixed.Length > 6 ? suffixed[6..] : "\n");

        Assert.IsType<GitHubFailure.NotFound>((await connection.GetRepositoryAsync("acme", suffixed)).Failure);
        Assert.IsType<GitHubFailure.NotFound>((await connection.GetRepositoryAsync(suffixed, "orders")).Failure);
        Assert.IsType<GitHubFailure.NotFound>((await connection.GetTreeAsync("acme", "orders", badSha)).Failure);
        Assert.IsType<GitHubFailure.NotFound>((await connection.GetBlobAsync("acme", "orders", badSha, 10)).Failure);
        Assert.IsType<GitHubFailure.NotFound>((await connection.CompareAsync("acme", "orders", badSha, CommitSha)).Failure);
        Assert.Empty(github.Stub.Requests);
    }

    [Theory]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("/main")]
    [InlineData("main/")]
    [InlineData("feature//x")]
    [InlineData("feature/./x")]
    [InlineData("feature/../x")]
    [InlineData("main\n")]
    [InlineData("main\u0085")]
    [InlineData("main x")]
    [InlineData("main?x=1")]
    [InlineData("main~1")]
    [InlineData("main^")]
    [InlineData("a:b")]
    [InlineData("feature/.hidden")]
    [InlineData("feature/x.lock")]
    [InlineData("main.")]
    [InlineData("main@{1}")]
    [InlineData("@")]
    [InlineData("")]
    public async Task A_ref_that_is_not_one_ref_sends_nothing(string reference)
    {
        using var github = await CreateAsync();
        var connection = await github.ConnectAsync();

        Assert.IsType<GitHubFailure.NotFound>((await connection.ResolveRefAsync("acme", "orders", reference)).Failure);
        Assert.Empty(github.Stub.Requests);
    }

    [Theory]
    [InlineData("main/status", "main%2Fstatus")]
    [InlineData("main/check-runs", "main%2Fcheck-runs")]
    [InlineData("heads/feature/outbox", "heads%2Ffeature%2Foutbox")]
    [InlineData("v1.2.3", "v1.2.3")]
    [InlineData("main&per_page=100", "main%26per_page%3D100")]
    [InlineData("fix#12", "fix%2312")]
    public async Task A_ref_with_a_slash_is_one_parameter_and_selects_no_other_endpoint(string reference, string encoded)
    {
        using var github = await CreateAsync();
        github.Stub.Json("""[{"sha":"SHA","commit":{"message":"x"}}]""".Replace("SHA", CommitSha));
        var connection = await github.ConnectAsync();

        var sha = await connection.ResolveRefAsync("acme", "orders", reference);

        Assert.Equal(CommitSha, sha.Value);
        var sent = Assert.Single(github.Stub.Requests);
        Assert.Equal("/repos/acme/orders/commits", sent.Uri.AbsolutePath);
        Assert.Equal($"?sha={encoded}&per_page=1", sent.Uri.Query);
    }

    [Fact]
    public async Task A_blob_of_exactly_the_cap_is_read_when_github_wraps_its_base64()
    {
        const int cap = 1_000_000;
        using var github = await CreateAsync();
        var bytes = new byte[cap];
        new Random(7).NextBytes(bytes);
        // GitHub breaks the base64 text into lines of 60 characters.
        var wrapped = string.Concat(Convert.ToBase64String(bytes).Chunk(60).Select(line => new string(line) + "\n"));
        var blob = new JsonObject { ["sha"] = BlobSha, ["size"] = cap, ["encoding"] = "base64", ["content"] = wrapped };
        // GitHub writes "+" and "/" as they are. The default encoder of the test would write "+" as six characters.
        github.Stub.Json(blob.ToJsonString(new System.Text.Json.JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        var connection = await github.ConnectAsync();

        var read = await connection.GetBlobAsync("acme", "orders", BlobSha, maxBytes: cap);
        var tooSmall = await connection.GetBlobAsync("acme", "orders", BlobSha, maxBytes: cap - 1);

        Assert.True(read.Ok, read.Failure?.ToString());
        Assert.Equal(bytes, read.Value!.Content);
        Assert.IsType<GitHubFailure.TooLarge>(tooSmall.Failure);
    }

    [Theory]
    [InlineData("99999999999999999")]
    [InlineData("-99999999999999999")]
    [InlineData("253402300800")]
    public async Task A_rate_limit_reset_outside_the_range_of_a_date_is_an_unexpected_response(string reset)
    {
        using var github = await CreateAsync();
        github.Stub.Json("""{"resources":{"core":{"limit":60,"remaining":57,"reset":RESET}}}""".Replace("RESET", reset));
        var connection = await github.ConnectAsync();

        var limit = await connection.GetRateLimitAsync();
        var test = await github.Owner.PostAsync($"/api/workspaces/{github.WorkspaceId}/github/test", null);

        Assert.Equal("unexpected_response", Assert.IsType<GitHubFailure.Unavailable>(limit.Failure).Reason);
        Assert.Equal(HttpStatusCode.OK, test.StatusCode);
    }

    [Fact]
    public async Task A_secondary_rate_limit_without_retry_after_is_a_rate_limit_of_one_minute()
    {
        using var github = await CreateAsync();
        const string body = """{"message":"You have exceeded a secondary rate limit. Please wait a few minutes before you try again.","documentation_url":"https://docs.github.com/rest/overview/rate-limits-for-the-rest-api#about-secondary-rate-limits"}""";
        github.Stub
            .Json(HttpStatusCode.Forbidden, body, ("x-ratelimit-remaining", "4990"))
            .Json(HttpStatusCode.Forbidden, Fixture("error-403.json"), ("x-ratelimit-remaining", "4990"));
        var connection = await github.ConnectAsync();
        var before = DateTimeOffset.UtcNow;

        var limited = Assert.IsType<GitHubFailure.RateLimited>((await connection.GetRepositoryAsync("acme", "orders")).Failure);
        var forbidden = (await connection.GetRepositoryAsync("acme", "orders")).Failure;

        Assert.InRange(limited.ResetAt!.Value, before.AddSeconds(59), before.AddSeconds(62));
        // A permission 403 stays a permission failure.
        Assert.IsType<GitHubFailure.Forbidden>(forbidden);
    }

    [Fact]
    public async Task Text_from_github_reaches_a_client_bounded_and_without_control_characters()
    {
        using var github = await CreateAsync();
        var login = "octo\r\ncat\u001b[31m\0" + new string('x', 5000);
        github.Stub
            .Json(new JsonObject { ["login"] = login }.ToJsonString())
            .Json(new JsonObject { ["id"] = 1, ["full_name"] = "acme/or\nders" + new string('y', 5000), ["default_branch"] = "ma\u0007in", ["private"] = true }.ToJsonString());

        var test = (await (await github.Owner.PostAsync($"/api/workspaces/{github.WorkspaceId}/github/test", null)).Content.ReadFromJsonAsync<JsonObject>())!;
        var repository = await (await github.ConnectAsync()).GetRepositoryAsync("acme", "orders");

        var shown = test["authenticatedAs"]!.GetValue<string>();
        Assert.StartsWith("octocat[31m", shown);
        Assert.InRange(shown.Length, 1, 100);
        Assert.DoesNotContain(shown, c => char.IsControl(c));
        Assert.DoesNotContain(repository.Value!.FullName + repository.Value.DefaultBranch, c => char.IsControl(c));
        Assert.InRange(repository.Value.FullName.Length, 1, 300);
        Assert.Equal("main", repository.Value.DefaultBranch);
    }
}
