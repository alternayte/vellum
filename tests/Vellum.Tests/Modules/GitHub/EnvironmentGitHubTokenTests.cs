using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Vellum.Modules.GitHub;
using Vellum.Modules.Settings;
using Vellum.Tests.Shared.Outbound;

namespace Vellum.Tests.Modules.GitHub;

/// <summary>A local instance started with <c>GH_TOKEN</c> (as <c>just up-gh</c> does).</summary>
public sealed class GhTokenHost : IAsyncLifetime
{
    public const string Token = "gho_fromGhAuthToken0123456789abcdefABCD";

    public IntegrationFixture Fixture { get; } = new();
    public OutboundHost Host { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await Fixture.InitializeAsync();
        Host = new(Fixture, new Dictionary<string, string?> { [EnvironmentGitHubToken.VariableName] = Token });
    }

    public async Task DisposeAsync()
    {
        Host?.Dispose();
        await Fixture.DisposeAsync();
    }
}

/// <summary>
/// The gh login of a local instance reads GitHub for the workspaces its admin owns, only at
/// api.github.com, and only when the workspace has no token of its own.
/// </summary>
public class EnvironmentGitHubTokenTests(GhTokenHost shared) : IClassFixture<GhTokenHost>
{
    private const string WorkspaceToken = "github_pat_workspaceOwnToken0123456789";
    private readonly OutboundHost _host = shared.Host;

    private async Task<(HttpClient Owner, Guid WorkspaceId)> AdminWorkspaceAsync()
    {
        var admin = await _host.SignInAsAdminAsync(shared.Fixture);
        var (workspaceId, _) = await TestHost.CreateProjectAsync(admin);
        return (admin, workspaceId);
    }

    private async Task<string?> AuthorizationSentAsync(Guid workspaceId, string host)
    {
        var stub = _host.Transport.For(host);
        var before = stub.Requests.Count;
        stub.Json(Fixtures.Read("GitHub/repository.json"));
        using var scope = _host.Factory.Services.CreateScope();
        var connection = await scope.ServiceProvider.GetRequiredService<GitHubReader>().ConnectAsync(workspaceId);
        Assert.True((await connection.Value!.GetRepositoryAsync("acme", "orders")).Ok);
        return stub.Requests[before].Header("Authorization");
    }

    private static async Task<JsonObject> CredentialAsync(HttpClient owner, Guid workspaceId) =>
        (await owner.GetFromJsonAsync<JsonObject>($"/api/workspaces/{workspaceId}/github/credential"))!;

    [Fact]
    public async Task A_workspace_the_admin_owns_reads_api_github_com_with_the_gh_token_until_it_saves_its_own()
    {
        var (admin, workspaceId) = await AdminWorkspaceAsync();
        _host.Transport.For("api.github.com").Json("""{"login":"octocat"}""");

        var credential = await CredentialAsync(admin, workspaceId);
        Assert.Equal(("gh", "octocat"), ((string)credential["source"]!, (string?)credential["login"]));
        Assert.Equal($"Bearer {GhTokenHost.Token}", await AuthorizationSentAsync(workspaceId, "api.github.com"));

        await OutboundHost.PutSecretAsync(admin, workspaceId, "github:token", WorkspaceToken);
        Assert.Equal("workspace", (string)(await CredentialAsync(admin, workspaceId))["source"]!);
        Assert.Equal($"Bearer {WorkspaceToken}", await AuthorizationSentAsync(workspaceId, "api.github.com"));
    }

    [Fact]
    public async Task Another_users_workspace_never_reads_with_the_admins_gh_token()
    {
        var (owner, workspaceId) = await _host.NewWorkspaceAsync();

        var credential = await CredentialAsync(owner, workspaceId);
        Assert.Equal(("anonymous", "not_admin_owned"), ((string)credential["source"]!, (string?)credential["ghNotUsed"]));
        Assert.Null(await AuthorizationSentAsync(workspaceId, "api.github.com"));
    }

    [Fact]
    public async Task The_gh_token_goes_to_api_github_com_only()
    {
        var (admin, workspaceId) = await AdminWorkspaceAsync();
        var host = $"{Guid.NewGuid():N}.ghe.test";
        await OutboundHost.PutSettingsAsync(admin, workspaceId, new JsonObject { ["github"] = new JsonObject { ["apiBaseUrl"] = $"https://{host}/api/v3" } });

        var credential = await CredentialAsync(admin, workspaceId);
        Assert.Equal(("anonymous", "other_host"), ((string)credential["source"]!, (string?)credential["ghNotUsed"]));
        Assert.Null(await AuthorizationSentAsync(workspaceId, host));
    }

    [Fact]
    public async Task Diagnostics_say_the_token_is_in_use_and_never_show_it()
    {
        var admin = await _host.SignInAsAdminAsync(shared.Fixture);
        var body = await admin.GetStringAsync("/api/instance/diagnostics");

        Assert.Equal("in_use", (string)JsonNode.Parse(body)!["gitHubEnvironmentToken"]!["state"]!);
        Assert.DoesNotContain(GhTokenHost.Token, body);
    }

    [Theory]
    [InlineData("local", "in_use")]
    [InlineData("hosted", "ignored_hosted")]
    public void A_hosted_instance_ignores_GH_TOKEN_and_GITHUB_TOKEN_is_never_read(string profile, string state)
    {
        var settings = profile == "hosted" ? TestHost.Hosted(TestHost.NewMasterKey()) : new() { ["Vellum:MasterKey"] = TestHost.NewMasterKey() };
        var bootstrap = Bootstrap.Load(new ConfigurationBuilder().AddInMemoryCollection(settings).Build(), () => []);

        var set = new EnvironmentGitHubToken(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["GH_TOKEN"] = GhTokenHost.Token }).Build(), bootstrap);
        Assert.Equal(state, set.State);
        Assert.Equal(profile == "local" ? GhTokenHost.Token : null, set.Token);

        var ciToken = new EnvironmentGitHubToken(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["GITHUB_TOKEN"] = "ghs_ciJobToken" }).Build(), bootstrap);
        Assert.Equal(("not_set", null), (ciToken.State, ciToken.Token));
    }
}
