using System.Net;
using System.Text;
using Vellum.Modules.GitHosts;
using Vellum.Modules.GitHub;
using Vellum.Tests.Shared.Outbound;

namespace Vellum.Tests.Modules.GitHosts;

/// <summary>
/// The Bitbucket Cloud reads through the real Refit client and handler pipeline. The fixtures
/// under <c>Fixtures/Bitbucket</c> follow the Bitbucket Cloud REST API 2.0 reference
/// (repositories, commit, refs, src, user).
/// </summary>
public class BitbucketReaderTests(SharedOutboundHost shared) : IClassFixture<SharedOutboundHost>
{
    private const string Token = "bbat-fixtureTOKEN0123456789";
    private static readonly string CommitSha = new('c', 40);

    private readonly OutboundHost _host = shared.Host;

    private Task<GitHostScenario> CreateAsync(string? token = Token) =>
        GitHostScenario.CreateAsync(_host, GitHostInfo.Bitbucket, "bitbucket", "/2.0", token);

    private static string Fixture(string name) => Fixtures.Read($"Bitbucket/{name}");

    [Fact]
    public async Task Each_read_sends_the_documented_request_and_returns_a_typed_value()
    {
        using var bitbucket = await CreateAsync();
        var nextPage = $"https://{bitbucket.HostName}/2.0/repositories/acme/orders/src/{CommitSha}/?max_depth=10&page=2&pagelen=100";
        bitbucket.Stub
            .Json(Fixture("repository.json"))
            .Json(Fixture("branches.json"))
            .Json("""{"pagelen":10,"page":1,"values":[]}""")
            .Json(Fixture("tags.json"))
            .Json(Fixture("commit.json"))
            .Json(Fixture("src-page-1.json").Replace("__NEXT__", nextPage, StringComparison.Ordinal))
            .Json(Fixture("src-page-2.json"))
            .Json(Fixture("src-deep.json"))
            .Json("# Notes\nDeep.\n")
            .Json(Fixture("user.json"))
            .Json(Fixture("repositories.json"));
        var connection = await bitbucket.ConnectAsync();

        var repository = await connection.GetRepositoryAsync("acme/orders", CancellationToken.None);
        var branch = await connection.ResolveRefAsync("acme/orders", "release/1.2", CancellationToken.None);
        var tag = await connection.ResolveRefAsync("acme/orders", "v1.0", CancellationToken.None);
        var commit = await connection.ResolveRefAsync("acme/orders", new string('a', 40), CancellationToken.None);
        var files = await connection.ListFilesAsync("acme/orders", CommitSha, CancellationToken.None);
        var file = await connection.GetFileAsync("acme/orders", CommitSha, "deep/my level/notes.md", "", 1024, CancellationToken.None);
        var identity = await connection.IdentifyAsync(CancellationToken.None);
        var listed = await connection.ListRepositoriesAsync("acme", 1, CancellationToken.None);

        Assert.Equal(new GitHostRepository("acme/orders", "acme/orders", "main", true), repository.Value);
        Assert.Equal(new string('a', 40), branch.Value);
        Assert.Equal(new string('b', 40), tag.Value);
        Assert.Equal(new string('a', 40), commit.Value);
        Assert.Equal(
        [
            new GitHubTreeEntry("README.md", "blob", "", 9, "100644"),
            new GitHubTreeEntry("src/Program.cs", "blob", "", 120, "100644"),
            new GitHubTreeEntry("src/run.sh", "blob", "", 30, "100755"),
            new GitHubTreeEntry("current", "blob", "", 7, "120000"),
            new GitHubTreeEntry("deep/my level/notes.md", "blob", "", 14, "100644"),
        ], files.Value);
        Assert.Equal("# Notes\nDeep.\n", Encoding.UTF8.GetString(file.Value!));
        Assert.Equal("acme-bot", identity.Value!.Login);
        Assert.Equal(
            [new GitHostRepository("acme/billing", "acme/billing", "trunk", false), new GitHostRepository("acme/orders", "acme/orders", "main", true)],
            listed.Value!.Repositories);
        Assert.True(listed.Value.HasMore);

        var sent = bitbucket.Stub.Requests;
        Assert.Equal(
        [
            "/2.0/repositories/acme/orders",
            "/2.0/repositories/acme/orders/refs/branches?q=name%3D%22release%2F1.2%22",
            "/2.0/repositories/acme/orders/refs/branches?q=name%3D%22v1.0%22",
            "/2.0/repositories/acme/orders/refs/tags?q=name%3D%22v1.0%22",
            $"/2.0/repositories/acme/orders/commit/{new string('a', 40)}",
            $"/2.0/repositories/acme/orders/src/{CommitSha}/?max_depth=10&pagelen=100",
            new Uri(nextPage).PathAndQuery,
            $"/2.0/repositories/acme/orders/src/{CommitSha}/deep/my%20level/?max_depth=10&pagelen=100",
            $"/2.0/repositories/acme/orders/src/{CommitSha}/deep/my%20level/notes.md",
            "/2.0/user",
            "/2.0/repositories/acme?pagelen=100&sort=slug&page=1",
        ], sent.Select(s => s.Uri.PathAndQuery));
        Assert.All(sent, s =>
        {
            Assert.Equal(HttpMethod.Get, s.Method);
            Assert.Equal($"Bearer {Token}", s.Header("Authorization"));
        });
    }

    [Fact]
    public async Task An_app_password_is_sent_as_basic_credentials()
    {
        using var bitbucket = await CreateAsync(token: "acme-bot:app-password-0123");
        bitbucket.Stub.Json(Fixture("user.json"));

        Assert.Equal("acme-bot", (await (await bitbucket.ConnectAsync()).IdentifyAsync(CancellationToken.None)).Value!.Login);

        Assert.Equal($"Basic {Convert.ToBase64String(Encoding.ASCII.GetBytes("acme-bot:app-password-0123"))}",
            Assert.Single(bitbucket.Stub.Requests).Header("Authorization"));
    }

    [Fact]
    public async Task Http_failures_become_typed_failures()
    {
        using var bitbucket = await CreateAsync();
        bitbucket.Stub
            .Json(HttpStatusCode.NotFound, Fixture("error-404.json"))
            .Json(HttpStatusCode.Unauthorized, Fixture("error-401.json"))
            .Json(HttpStatusCode.TooManyRequests, """{"type":"error","error":{"message":"Rate limit for this resource has been exceeded"}}""", ("Retry-After", "120"))
            .Json(HttpStatusCode.TooManyRequests, """{"type":"error","error":{"message":"Rate limit for this resource has been exceeded"}}""");
        var connection = await bitbucket.ConnectAsync();

        Assert.IsType<GitHubFailure.NotFound>((await connection.GetRepositoryAsync("acme/missing", CancellationToken.None)).Failure);
        Assert.IsType<GitHubFailure.Unauthorized>((await connection.GetRepositoryAsync("acme/orders", CancellationToken.None)).Failure);

        var before = DateTimeOffset.UtcNow;
        var asked = Assert.IsType<GitHubFailure.RateLimited>((await connection.GetRepositoryAsync("acme/orders", CancellationToken.None)).Failure);
        Assert.InRange(asked.ResetAt!.Value, before.AddSeconds(119), before.AddSeconds(122));

        // Without Retry-After the wait is one minute.
        before = DateTimeOffset.UtcNow;
        var unsaid = Assert.IsType<GitHubFailure.RateLimited>((await connection.GetRepositoryAsync("acme/orders", CancellationToken.None)).Failure);
        Assert.InRange(unsaid.ResetAt!.Value, before.AddSeconds(59), before.AddSeconds(62));
        Assert.Equal(6, bitbucket.Stub.Requests.Count);
    }

    [Fact]
    public async Task A_next_page_on_another_origin_is_not_followed()
    {
        using var bitbucket = await CreateAsync();
        bitbucket.Stub.Json(Fixture("src-page-1.json").Replace("__NEXT__", "https://evil.test/2.0/page2", StringComparison.Ordinal));

        var files = await (await bitbucket.ConnectAsync()).ListFilesAsync("acme/orders", CommitSha, CancellationToken.None);

        Assert.Equal("redirect_refused", Assert.IsType<GitHubFailure.Unavailable>(files.Failure).Reason);
        Assert.Single(bitbucket.Stub.Requests);
    }

    [Fact]
    public async Task A_redirect_to_another_origin_is_refused_and_the_token_stays()
    {
        using var bitbucket = await CreateAsync();
        await bitbucket.AssertRedirectOffOriginIsRefusedAsync(_host, "https://evil.test/2.0/repositories/acme/orders",
            async connection => (await connection.GetRepositoryAsync("acme/orders", CancellationToken.None)).Failure);
    }

    [Fact]
    public async Task A_path_that_would_change_the_address_sends_nothing()
    {
        using var bitbucket = await CreateAsync();
        var connection = await bitbucket.ConnectAsync();

        Assert.IsType<GitHubFailure.NotFound>((await connection.GetFileAsync("acme/orders", CommitSha, "src/../../../user", "", 10, CancellationToken.None)).Failure);
        Assert.IsType<GitHubFailure.NotFound>((await connection.ResolveRefAsync("acme/orders", "main\"OR\"x", CancellationToken.None)).Failure);
        Assert.Empty(bitbucket.Stub.Requests);
    }
}
