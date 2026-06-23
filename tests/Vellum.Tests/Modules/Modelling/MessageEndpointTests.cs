using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vellum.Kernel.EventStore;
using Vellum.Modules.Docs;
using Vellum.Modules.Drafts;
using Vellum.Modules.Identity;
using Vellum.Modules.Modelling;
using Vellum.Modules.Modelling.Messages;
using Vellum.Modules.Schemas;
using Vellum.Modules.Views;
using Vellum.Modules.Workspaces;

namespace Vellum.Tests.Modules.Modelling;

[Collection("Integration")]
public class MessageEndpointTests
{
    private readonly IntegrationFixture _fixture;

    public MessageEndpointTests(IntegrationFixture fixture) => _fixture = fixture;

    private WebApplicationFactory<Program> CreateFactory()
    {
        return new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Testing");
                builder.ConfigureServices(services =>
                {
                    services.RemoveAll<DbContextOptions<EventStoreDbContext>>();
                    services.RemoveAll<DbContextOptions<AppIdentityDbContext>>();
                    services.RemoveAll<DbContextOptions<WorkspacesDbContext>>();
                    services.RemoveAll<DbContextOptions<ModellingDbContext>>();
                    services.RemoveAll<DbContextOptions<SchemasDbContext>>();
                    services.RemoveAll<DbContextOptions<DraftsDbContext>>();
                    services.RemoveAll<DbContextOptions<ViewsDbContext>>();
                    services.RemoveAll<DbContextOptions<DocsDbContext>>();
                    services.AddDbContext<EventStoreDbContext>(o =>
                        o.UseNpgsql(_fixture.ConnectionString).UseSnakeCaseNamingConvention());
                    services.AddDbContext<AppIdentityDbContext>(o =>
                        o.UseNpgsql(_fixture.ConnectionString).UseSnakeCaseNamingConvention());
                    services.AddDbContext<WorkspacesDbContext>(o =>
                        o.UseNpgsql(_fixture.ConnectionString).UseSnakeCaseNamingConvention());
                    services.AddDbContext<ModellingDbContext>(o =>
                        o.UseNpgsql(_fixture.ConnectionString).UseSnakeCaseNamingConvention());
                    services.AddDbContext<SchemasDbContext>(o =>
                        o.UseNpgsql(_fixture.ConnectionString).UseSnakeCaseNamingConvention());
                    services.AddDbContext<DraftsDbContext>(o =>
                        o.UseNpgsql(_fixture.ConnectionString).UseSnakeCaseNamingConvention());
                    services.AddDbContext<ViewsDbContext>(o =>
                        o.UseNpgsql(_fixture.ConnectionString).UseSnakeCaseNamingConvention());
                    services.AddDbContext<DocsDbContext>(o =>
                        o.UseNpgsql(_fixture.ConnectionString).UseSnakeCaseNamingConvention());
                });
            });
    }

    private static HttpClient CreateClient(WebApplicationFactory<Program> factory)
    {
        return factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            HandleCookies = true,
            AllowAutoRedirect = false
        });
    }

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
        var workspaceId = Guid.NewGuid();
        await client.PostAsJsonAsync("/api/workspaces", new { id = workspaceId, name = "Test WS" });
        var projectId = Guid.NewGuid();
        await client.PostAsJsonAsync($"/api/workspaces/{workspaceId}/projects",
            new { id = projectId, name = "Test Project" });
        return projectId;
    }

    [Fact]
    public async Task Create_and_get_message()
    {
        using var factory = CreateFactory();
        using var client = await CreateAuthenticatedClientAsync(factory);
        var projectId = await SetupProjectAsync(client);

        var producerId = Guid.NewGuid();
        var consumerId = Guid.NewGuid();

        // Seed producer and consumer elements
        await client.PostAsJsonAsync($"/api/projects/{projectId}/elements",
            new { id = producerId, kind = "system", name = "Orders" });
        await client.PostAsJsonAsync($"/api/projects/{projectId}/elements",
            new { id = consumerId, kind = "system", name = "Payments" });

        var id = Guid.NewGuid();
        var response = await client.PostAsJsonAsync($"/api/projects/{projectId}/messages", new
        {
            id,
            name = "OrderPlaced",
            description = "An order was placed",
            producerId,
            consumerIds = new[] { consumerId },
            tags = new[] { "domain-event" }
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var get = await client.GetAsync($"/api/projects/{projectId}/messages/{id}");
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        var dto = await get.Content.ReadFromJsonAsync<MessageDto>();
        Assert.Equal("OrderPlaced", dto!.Name);
        Assert.Equal(producerId, dto.ProducerId);
    }

    [Fact]
    public async Task List_messages()
    {
        using var factory = CreateFactory();
        using var client = await CreateAuthenticatedClientAsync(factory);
        var projectId = await SetupProjectAsync(client);

        var producerId = Guid.NewGuid();

        // Seed producer element
        await client.PostAsJsonAsync($"/api/projects/{projectId}/elements",
            new { id = producerId, kind = "system", name = "Orders" });

        await client.PostAsJsonAsync($"/api/projects/{projectId}/messages", new
        {
            id = Guid.NewGuid(),
            name = "OrderPlaced",
            producerId,
            consumerIds = Array.Empty<Guid>(),
            tags = Array.Empty<string>()
        });
        var response = await client.GetAsync($"/api/projects/{projectId}/messages");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Delete_message()
    {
        using var factory = CreateFactory();
        using var client = await CreateAuthenticatedClientAsync(factory);
        var projectId = await SetupProjectAsync(client);

        var producerId = Guid.NewGuid();

        // Seed producer element
        await client.PostAsJsonAsync($"/api/projects/{projectId}/elements",
            new { id = producerId, kind = "system", name = "Orders" });

        var id = Guid.NewGuid();
        await client.PostAsJsonAsync($"/api/projects/{projectId}/messages", new
        {
            id,
            name = "OrderPlaced",
            producerId,
            consumerIds = Array.Empty<Guid>(),
            tags = Array.Empty<string>()
        });
        var del = await client.DeleteAsync($"/api/projects/{projectId}/messages/{id}");
        Assert.Equal(HttpStatusCode.NoContent, del.StatusCode);
        var get = await client.GetAsync($"/api/projects/{projectId}/messages/{id}");
        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
    }
}

file static class MessageTestServiceCollectionExtensions
{
    public static void RemoveAll<T>(this IServiceCollection services)
    {
        var descriptors = services.Where(d => d.ServiceType == typeof(T)).ToList();
        foreach (var d in descriptors) services.Remove(d);
    }
}
