using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vellum.Modules.Ai;
using Vellum.Modules.Ai.Usage;
using Vellum.Modules.GitHub;
using Vellum.Shared;
using Vellum.Shared.Outbound;
using Vellum.Tests.Shared.Outbound;

namespace Vellum.Tests.Modules.Ai;

/// <summary>
/// <see cref="AiClient"/> and <see cref="GitHubReader"/> on a hosted instance, where the
/// policy starts closed: no private network, https for every request, no private code to a
/// provider. Each test has its own database and its own app.
/// </summary>
public class HostedPolicyTests
{
    private const string PublicAddress = "93.184.216.34";
    private const string PrivateAddress = "10.0.0.5";
    private const string Token = "ghp_hostedTOKEN0123456789abcdefghijABCDE";

    /// <summary>Records the call of every request that reaches the transport, then serves the stub for the host.</summary>
    private sealed class RecordingTransport(RoutedTransport inner) : DelegatingHandler(inner)
    {
        public ConcurrentQueue<OutboundCall> Calls { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls.Enqueue(OutboundCall.From(request));
            return base.SendAsync(request, ct);
        }

        // The client factory disposes its handlers. This one is used again.
        protected override void Dispose(bool disposing) { }
    }

    /// <param name="Addresses">The address each host name resolves to now. A test changes it between the save and the call.</param>
    private sealed record Hosted(
        WebApplicationFactory<Program> Factory, HttpClient Owner, Guid WorkspaceId,
        RoutedTransport Transport, RecordingTransport Recorder, ConcurrentDictionary<string, string> Addresses) : IAsyncDisposable
    {
        public static async Task<Hosted> StartAsync()
        {
            var transport = new RoutedTransport();
            var recorder = new RecordingTransport(transport);
            var addresses = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var database = await TestHost.CreateDatabaseAsync();
            var factory = TestHost.Create(database, TestHost.Hosted(TestHost.NewMasterKey()), services =>
            {
                services.AddSingleton<HostResolver>(_ => (host, _) =>
                    Task.FromResult(new[] { IPAddress.Parse(addresses.GetValueOrDefault(host, PublicAddress)) }));
                services.AddSingleton<OutboundPrimaryHandlerFactory>(_ => () => recorder);
                services.AddSingleton(OutboundLimits.Default with { Delay = (_, _) => Task.CompletedTask, Jitter = () => 0 });
            });
            var (admin, created, _) = await TestHost.SetUpAsync(factory, TestHost.SetupToken, https: true);
            return new Hosted(factory, admin, created.WorkspaceId, transport, recorder, addresses);
        }

        public async Task SaveSettingsAsync(JsonObject overrides, HttpStatusCode expected = HttpStatusCode.OK)
        {
            var response = await Owner.PutSettingsAsync($"/api/workspaces/{WorkspaceId}/settings",
                new StringContent(overrides.ToJsonString(), Encoding.UTF8, "application/json"));
            Assert.True(response.StatusCode == expected, await response.Content.ReadAsStringAsync());
        }

        public async Task SaveSecretAsync(string name, string value) =>
            Assert.Equal(HttpStatusCode.NoContent, (await Owner.PutAsJsonAsync($"/api/workspaces/{WorkspaceId}/secrets/{name}", new { value })).StatusCode);

        public async Task AllowPrivateNetworksAsync()
        {
            var settings = (await Owner.GetFromJsonAsync<JsonObject>("/api/instance/settings"))!;
            settings["policy"]!["allowPrivateNetworkUrls"] = true;
            Assert.Equal(HttpStatusCode.OK, (await Owner.PutInstanceSettingsAsync(settings)).StatusCode);
        }

        public async Task<Guid> AddProviderAsync(string baseUrl, JsonObject? privacy = null)
        {
            var providerId = Guid.NewGuid();
            var overrides = new JsonObject
            {
                ["ai"] = new JsonObject
                {
                    ["providers"] = new JsonArray(new JsonObject
                    {
                        ["id"] = providerId, ["kind"] = "openai-compatible", ["name"] = "Hosted", ["model"] = "model-hosted", ["baseUrl"] = baseUrl,
                    }),
                    ["taskRouting"] = new JsonObject { ["proposeFlow"] = providerId },
                },
            };
            if (privacy is not null)
                overrides["privacy"] = privacy;
            await SaveSettingsAsync(overrides);
            await SaveSecretAsync($"ai:{providerId:D}:apiKey", AiScenario.ApiKey);
            return providerId;
        }

        public AiRequest Request(params AiPart[] parts) => new(
            WorkspaceId, AiTask.ProposeFlow, "summarise", "summarise/1", "You summarise a repository.",
            [new AiMessage(AiRole.User, parts.Length > 0 ? parts : [AiPart.Instruction("Summarise the repository.")])],
            AiScenario.SummarySchema, MaxOutputTokens: 256, ActorId: "user:test");

        public async Task<AiResult<Summary>> StructuredAsync(AiRequest? request = null)
        {
            using var scope = Factory.Services.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<AiClient>().CompleteStructuredAsync<Summary>(request ?? Request());
        }

        public async Task<List<UsageRecordEntity>> RowsAsync()
        {
            using var scope = Factory.Services.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<AiUsageDbContext>().UsageRecords.AsNoTracking()
                .Where(r => r.WorkspaceId == WorkspaceId).OrderBy(r => r.StartedAt).ToListAsync();
        }

        public async Task<GitHubConnection> ConnectAsync(IServiceScope scope)
        {
            var connection = await scope.ServiceProvider.GetRequiredService<GitHubReader>().ConnectAsync(WorkspaceId);
            Assert.True(connection.Ok, connection.Failure?.ToString());
            return connection.Value!;
        }

        public async ValueTask DisposeAsync()
        {
            Owner.Dispose();
            await Factory.DisposeAsync();
        }
    }

    private static string Completion() => Fixtures.WithText("Ai/openai-compatible/completion.json", AiScenario.ValidAnswer);

    [Fact]
    public async Task The_ai_client_refuses_a_provider_whose_name_now_resolves_to_a_private_address()
    {
        await using var hosted = await Hosted.StartAsync();
        const string host = "llm.hosted.example";
        await hosted.AddProviderAsync($"https://{host}/v1");
        hosted.Transport.For(host).Json(Completion());

        // The name was public when the settings were saved. It is private when the call is made.
        hosted.Addresses[host] = PrivateAddress;
        var refused = Assert.IsType<AiFailure.ProviderError>(Assert.IsType<AiResult<Summary>.Failed>(await hosted.StructuredAsync()).Failure);

        Assert.Equal("url_refused", refused.Code);
        Assert.Empty(hosted.Transport.For(host).Requests);
        var row = Assert.Single(await hosted.RowsAsync());
        Assert.Equal(("provider_error", "url_refused", 0m, null), (row.Outcome, row.ErrorCode, row.CostUsd, row.ReservedUsd));

        // Public again: the call goes out, over https, with both rules of a hosted instance on the call.
        hosted.Addresses[host] = PublicAddress;
        Assert.IsType<AiResult<Summary>.Succeeded>(await hosted.StructuredAsync());

        var sent = Assert.Single(hosted.Transport.For(host).Requests);
        Assert.Equal("https", sent.Uri.Scheme);
        Assert.Equal($"Bearer {AiScenario.ApiKey}", sent.Header("Authorization"));
        var call = Assert.Single(hosted.Recorder.Calls);
        Assert.Equal((true, false), (call.RequireHttps, call.AllowPrivateNetwork));
    }

    [Fact]
    public async Task The_ai_client_never_sends_a_key_over_http_to_a_host_that_is_not_private()
    {
        await using var hosted = await Hosted.StartAsync();
        const string host = "ollama.hosted.example";
        // The admin allows private networks, and the provider is plain http on a private address: that is allowed.
        await hosted.AllowPrivateNetworksAsync();
        hosted.Addresses[host] = PrivateAddress;
        await hosted.AddProviderAsync($"http://{host}:11434/v1");
        hosted.Transport.For(host).Json(Completion());

        Assert.IsType<AiResult<Summary>.Succeeded>(await hosted.StructuredAsync());
        Assert.Equal("http", Assert.Single(hosted.Transport.For(host).Requests).Uri.Scheme);
        Assert.Equal((true, true), (hosted.Recorder.Calls.Single().RequireHttps, hosted.Recorder.Calls.Single().AllowPrivateNetwork));

        // The same name now answers with a public address. The key must not go there over plain http.
        hosted.Addresses[host] = PublicAddress;
        var refused = Assert.IsType<AiFailure.ProviderError>(Assert.IsType<AiResult<Summary>.Failed>(await hosted.StructuredAsync()).Failure);

        Assert.Equal("url_refused", refused.Code);
        Assert.Single(hosted.Transport.For(host).Requests);
        Assert.Single(hosted.Recorder.Calls);
    }

    [Fact]
    public async Task The_github_reader_keeps_the_hosted_rules_on_every_request_and_on_a_redirect()
    {
        await using var hosted = await Hosted.StartAsync();
        const string host = "ghe.hosted.example";
        await hosted.SaveSettingsAsync(new JsonObject { ["github"] = new JsonObject { ["apiBaseUrl"] = $"https://{host}/api/v3" } });
        await hosted.SaveSecretAsync("github:token", Token);
        var stub = hosted.Transport.For(host);
        stub.Json(Fixtures.Read("GitHub/repository.json"))
            .Then(() =>
            {
                var moved = new HttpResponseMessage(HttpStatusCode.MovedPermanently);
                moved.Headers.Location = new Uri($"http://{host}/api/v3/repositories/1");
                return moved;
            });
        using var scope = hosted.Factory.Services.CreateScope();
        var connection = await hosted.ConnectAsync(scope);

        // Private when the call is made: nothing is sent, by the reader and by the test endpoint.
        hosted.Addresses[host] = PrivateAddress;
        var refused = await connection.GetRepositoryAsync("acme", "orders");
        var test = (await (await hosted.Owner.PostAsync($"/api/workspaces/{hosted.WorkspaceId}/github/test", null)).Content.ReadFromJsonAsync<JsonObject>())!;

        Assert.Equal("url_refused", Assert.IsType<GitHubFailure.Unavailable>(refused.Failure).Reason);
        Assert.Equal("url_refused", test["error"]!["code"]!.GetValue<string>());
        Assert.Empty(stub.Requests);

        hosted.Addresses[host] = PublicAddress;
        Assert.True((await connection.GetRepositoryAsync("acme", "orders")).Ok);
        // A redirect from https to http on the same host is not followed, so the token does not go over http.
        var redirected = await connection.GetRepositoryAsync("acme", "orders");

        Assert.Equal("redirect_refused", Assert.IsType<GitHubFailure.Unavailable>(redirected.Failure).Reason);
        Assert.Equal(2, stub.Requests.Count);
        Assert.All(stub.Requests, sent => Assert.Equal(("https", $"Bearer {Token}"), (sent.Uri.Scheme, sent.Header("Authorization"))));
        Assert.All(hosted.Recorder.Calls, call => Assert.Equal((true, false), (call.RequireHttps, call.AllowPrivateNetwork)));
    }

    [Fact]
    public async Task The_github_reader_never_sends_a_token_over_http_to_a_host_that_is_not_private()
    {
        await using var hosted = await Hosted.StartAsync();
        const string host = "ghe-internal.hosted.example";
        await hosted.AllowPrivateNetworksAsync();
        hosted.Addresses[host] = PrivateAddress;
        await hosted.SaveSettingsAsync(new JsonObject { ["github"] = new JsonObject { ["apiBaseUrl"] = $"http://{host}:3000/api/v3" } });
        await hosted.SaveSecretAsync("github:token", Token);
        var stub = hosted.Transport.For(host);
        stub.Json(HttpStatusCode.ServiceUnavailable, "{}").Json(Fixtures.Read("GitHub/repository.json"));
        using var scope = hosted.Factory.Services.CreateScope();
        var connection = await hosted.ConnectAsync(scope);

        // Plain http to a private address is allowed here. The retry after the 503 is checked again and passes too.
        var first = await connection.GetRepositoryAsync("acme", "orders");
        Assert.True(first.Ok, first.Failure?.ToString());
        Assert.Equal(2, stub.Requests.Count);
        Assert.All(stub.Requests, sent => Assert.Equal(("http", $"Bearer {Token}"), (sent.Uri.Scheme, sent.Header("Authorization"))));

        // The same name now answers with a public address. The token must not go there over plain http.
        hosted.Addresses[host] = PublicAddress;
        var refused = await connection.GetRepositoryAsync("acme", "orders");

        Assert.Equal("url_refused", Assert.IsType<GitHubFailure.Unavailable>(refused.Failure).Reason);
        Assert.Equal(2, stub.Requests.Count);
        Assert.All(hosted.Recorder.Calls, call => Assert.Equal((true, true), (call.RequireHttps, call.AllowPrivateNetwork)));
    }

    [Fact]
    public async Task A_hosted_workspace_cannot_send_private_code_and_cannot_switch_the_setting_on()
    {
        await using var hosted = await Hosted.StartAsync();
        const string host = "llm-privacy.hosted.example";
        await hosted.AddProviderAsync($"https://{host}/v1");
        hosted.Transport.For(host).Json(Completion());
        var privateCode = hosted.Request(AiPart.Instruction("Summarise."), AiPart.Untrusted("class Orders { }", AiDataClass.PrivateRepository));

        var byDefault = Assert.IsType<AiFailure.Disabled>(Assert.IsType<AiResult<Summary>.Failed>(await hosted.StructuredAsync(privateCode)).Failure);
        // The instance policy does not allow the workspace to choose it.
        await hosted.SaveSettingsAsync(new JsonObject { ["privacy"] = new JsonObject { ["sendPrivateCodeToAi"] = true } }, HttpStatusCode.BadRequest);
        var afterTheAttempt = Assert.IsType<AiFailure.Disabled>(Assert.IsType<AiResult<Summary>.Failed>(await hosted.StructuredAsync(privateCode)).Failure);

        Assert.Equal(("privacy.sendPrivateCodeToAi", "privacy.sendPrivateCodeToAi"), (byDefault.Setting, afterTheAttempt.Setting));
        Assert.Empty(hosted.Transport.For(host).Requests);
        Assert.Empty(await hosted.RowsAsync());

        // Public text is not private code. It goes out.
        Assert.IsType<AiResult<Summary>.Succeeded>(await hosted.StructuredAsync(
            hosted.Request(AiPart.Instruction("Summarise."), AiPart.Untrusted("README of a public repository", AiDataClass.Public))));
        Assert.Single(hosted.Transport.For(host).Requests);
    }
}
