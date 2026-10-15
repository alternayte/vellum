using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Vellum.Modules.GitHosts;
using Vellum.Modules.GitHub;
using Vellum.Tests.Shared.Outbound;

namespace Vellum.Tests.Modules.GitHosts;

/// <summary>
/// One workspace with the API base URL of one git host on its own test host name, and its
/// token saved for that origin. The transport of that name serves the fixtures.
/// </summary>
internal sealed record GitHostScenario(
    GitHostInfo Info, HttpClient Owner, Guid WorkspaceId, string HostName, StubTransport Stub, IServiceScope Scope) : IDisposable
{
    public static async Task<GitHostScenario> CreateAsync(OutboundHost host, GitHostInfo info, string settingsKey, string path, string? token)
    {
        var (owner, workspaceId) = await host.NewWorkspaceAsync();
        var hostName = $"{Guid.NewGuid():N}.{info.Name}.test";
        await OutboundHost.PutSettingsAsync(owner, workspaceId,
            new JsonObject { [settingsKey] = new JsonObject { ["apiBaseUrl"] = $"https://{hostName}{path}" } });
        if (token is not null)
            await OutboundHost.PutSecretAsync(owner, workspaceId, info.SecretName, token);
        return new GitHostScenario(info, owner, workspaceId, hostName, host.Transport.For(hostName), host.Factory.Services.CreateScope());
    }

    public async Task<IGitHostConnection> ConnectAsync()
    {
        var connection = await Scope.ServiceProvider.GetRequiredService<GitHostReader>().ConnectAsync(Info, WorkspaceId);
        Assert.True(connection.Ok);
        return connection.Value!;
    }

    /// <summary>
    /// A redirect to <paramref name="location"/> on another origin is refused: nothing reaches
    /// that origin, so the token stays on the configured one.
    /// </summary>
    public async Task AssertRedirectOffOriginIsRefusedAsync(OutboundHost host, string location, Func<IGitHostConnection, Task<GitHubFailure?>> read)
    {
        var elsewhere = host.Transport.For(new Uri(location).Host);
        try
        {
            Stub.Json(HttpStatusCode.Found, "{}", ("Location", location));

            var failure = await read(await ConnectAsync());

            Assert.Equal("redirect_refused", Assert.IsType<GitHubFailure.Unavailable>(failure).Reason);
            Assert.Single(Stub.Requests);
            Assert.Empty(elsewhere.Requests);
        }
        finally
        {
            host.Transport.Forget(new Uri(location).Host);
        }
    }

    public void Dispose() => Scope.Dispose();
}
