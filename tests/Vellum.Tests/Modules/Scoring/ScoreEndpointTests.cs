using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Vellum.Modules.Docs;
using Vellum.Modules.Drafts;
using Vellum.Modules.Identity;
using Vellum.Modules.Modelling;
using Vellum.Modules.Schemas;
using Vellum.Modules.Scoring;
using Vellum.Modules.Views;
using Vellum.Modules.Workspaces;

namespace Vellum.Tests.Modules.Scoring;

[Collection("Integration")]
public class ScoreEndpointTests
{
    private readonly IntegrationFixture _fixture;

    public ScoreEndpointTests(IntegrationFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Status_returns_unavailable_when_no_ai_configured()
    {
        using var factory = CreateFactory();
        var client = factory.CreateClient();

        var response = await client.GetAsync("/api/scoring/status");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.GetProperty("available").GetBoolean());
    }

    [Fact]
    public async Task Create_score_returns_503_when_no_ai_configured()
    {
        using var factory = CreateFactory();
        using var client = await CreateAuthenticatedClientAsync(factory);
        var projectId = await SetupProjectAsync(client);

        var docId = Guid.NewGuid();
        await client.PostAsJsonAsync($"/api/projects/{projectId}/docs",
            new { id = docId, title = "Test ADR", type = "adr" });

        var response = await client.PostAsync(
            $"/api/projects/{projectId}/docs/{docId}/scores", null);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task Create_score_returns_422_for_doc_without_type()
    {
        using var factory = CreateFactory(registerMockChatClient: true);
        using var client = await CreateAuthenticatedClientAsync(factory);
        var projectId = await SetupProjectAsync(client);

        var docId = Guid.NewGuid();
        await client.PostAsJsonAsync($"/api/projects/{projectId}/docs",
            new { id = docId, title = "Blank Doc" });

        var response = await client.PostAsync(
            $"/api/projects/{projectId}/docs/{docId}/scores", null);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task List_scores_returns_empty_for_new_doc()
    {
        using var factory = CreateFactory();
        using var client = await CreateAuthenticatedClientAsync(factory);
        var projectId = await SetupProjectAsync(client);

        var docId = Guid.NewGuid();
        await client.PostAsJsonAsync($"/api/projects/{projectId}/docs",
            new { id = docId, title = "Test Doc", type = "adr" });

        var response = await client.GetAsync(
            $"/api/projects/{projectId}/docs/{docId}/scores");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var scores = await response.Content.ReadFromJsonAsync<JsonElement[]>();
        Assert.Empty(scores!);
    }

    private WebApplicationFactory<Program> CreateFactory(bool registerMockChatClient = false)
    {
        return new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Testing");
                builder.ConfigureServices(services =>
                {
                    var cs = _fixture.ConnectionString;

                    services.RemoveAll<DbContextOptions<Vellum.Kernel.EventStore.EventStoreDbContext>>();
                    services.RemoveAll<DbContextOptions<AppIdentityDbContext>>();
                    services.RemoveAll<DbContextOptions<WorkspacesDbContext>>();
                    services.RemoveAll<DbContextOptions<ModellingDbContext>>();
                    services.RemoveAll<DbContextOptions<ViewsDbContext>>();
                    services.RemoveAll<DbContextOptions<DocsDbContext>>();
                    services.RemoveAll<DbContextOptions<DraftsDbContext>>();
                    services.RemoveAll<DbContextOptions<SchemasDbContext>>();
                    services.RemoveAll<DbContextOptions<ScoringDbContext>>();

                    services.AddDbContext<Vellum.Kernel.EventStore.EventStoreDbContext>(o => o.UseNpgsql(cs).UseSnakeCaseNamingConvention());
                    services.AddDbContext<AppIdentityDbContext>(o => o.UseNpgsql(cs).UseSnakeCaseNamingConvention());
                    services.AddDbContext<WorkspacesDbContext>(o => o.UseNpgsql(cs).UseSnakeCaseNamingConvention());
                    services.AddDbContext<ModellingDbContext>(o => o.UseNpgsql(cs).UseSnakeCaseNamingConvention());
                    services.AddDbContext<ViewsDbContext>(o => o.UseNpgsql(cs).UseSnakeCaseNamingConvention());
                    services.AddDbContext<DocsDbContext>(o => o.UseNpgsql(cs).UseSnakeCaseNamingConvention());
                    services.AddDbContext<DraftsDbContext>(o => o.UseNpgsql(cs).UseSnakeCaseNamingConvention());
                    services.AddDbContext<SchemasDbContext>(o => o.UseNpgsql(cs).UseSnakeCaseNamingConvention());
                    services.AddDbContext<ScoringDbContext>(o => o.UseNpgsql(cs).UseSnakeCaseNamingConvention());

                    if (registerMockChatClient)
                    {
                        services.AddSingleton<IChatClient>(new FakeChatClient());
                    }
                });
            });
    }

    private static HttpClient CreateClient(WebApplicationFactory<Program> factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true, AllowAutoRedirect = false });

    private static async Task<HttpClient> CreateAuthenticatedClientAsync(WebApplicationFactory<Program> factory)
    {
        var client = CreateClient(factory);
        var email = $"test-{Guid.NewGuid():N}@vellum.local";
        await client.PostAsJsonAsync("/api/auth/register",
            new { email, password = "Test123!", displayName = "Test User" });
        return client;
    }

    private static async Task<Guid> SetupProjectAsync(HttpClient client)
    {
        var wsId = Guid.NewGuid();
        await client.PostAsJsonAsync("/api/workspaces", new { id = wsId, name = "Test WS" });
        var projId = Guid.NewGuid();
        await client.PostAsJsonAsync($"/api/workspaces/{wsId}/projects", new { id = projId, name = "Test Proj" });
        return projId;
    }

    /// <summary>Minimal fake IChatClient for tests that need AI configured but don't test LLM output.</summary>
    private sealed class FakeChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, "{}")]));

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }
}
