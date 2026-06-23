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
using Vellum.Modules.Schemas;
using Vellum.Modules.Views;
using Vellum.Modules.Workspaces;

namespace Vellum.Tests.Modules.Modelling;

[Collection("Integration")]
public class ExportImportTests
{
    private readonly IntegrationFixture _fixture;

    public ExportImportTests(IntegrationFixture fixture) => _fixture = fixture;

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
                    services.RemoveAll<DbContextOptions<DraftsDbContext>>();
                    services.RemoveAll<DbContextOptions<ViewsDbContext>>();
                    services.RemoveAll<DbContextOptions<DocsDbContext>>();
                    services.RemoveAll<DbContextOptions<SchemasDbContext>>();
                    services.AddDbContext<EventStoreDbContext>(o =>
                        o.UseNpgsql(_fixture.ConnectionString).UseSnakeCaseNamingConvention());
                    services.AddDbContext<AppIdentityDbContext>(o =>
                        o.UseNpgsql(_fixture.ConnectionString).UseSnakeCaseNamingConvention());
                    services.AddDbContext<WorkspacesDbContext>(o =>
                        o.UseNpgsql(_fixture.ConnectionString).UseSnakeCaseNamingConvention());
                    services.AddDbContext<ModellingDbContext>(o =>
                        o.UseNpgsql(_fixture.ConnectionString).UseSnakeCaseNamingConvention());
                    services.AddDbContext<DraftsDbContext>(o =>
                        o.UseNpgsql(_fixture.ConnectionString).UseSnakeCaseNamingConvention());
                    services.AddDbContext<ViewsDbContext>(o =>
                        o.UseNpgsql(_fixture.ConnectionString).UseSnakeCaseNamingConvention());
                    services.AddDbContext<DocsDbContext>(o =>
                        o.UseNpgsql(_fixture.ConnectionString).UseSnakeCaseNamingConvention());
                    services.AddDbContext<SchemasDbContext>(o =>
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
    public async Task Export_json_returns_ok()
    {
        using var factory = CreateFactory();
        using var client = await CreateAuthenticatedClientAsync(factory);
        var projectId = await SetupProjectAsync(client);

        await client.PostAsJsonAsync($"/api/projects/{projectId}/elements",
            new { id = Guid.NewGuid(), kind = "system", name = "Orders" });

        var response = await client.GetAsync($"/api/projects/{projectId}/export?format=json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var content = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"version\"", content);
        Assert.Contains("1.0", content);
        Assert.Contains("Orders", content);
    }

    [Fact]
    public async Task Export_yaml_returns_ok()
    {
        using var factory = CreateFactory();
        using var client = await CreateAuthenticatedClientAsync(factory);
        var projectId = await SetupProjectAsync(client);

        var response = await client.GetAsync($"/api/projects/{projectId}/export?format=yaml");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var content = await response.Content.ReadAsStringAsync();
        Assert.Contains("version:", content);
    }

    [Fact]
    public async Task Export_then_import_round_trips()
    {
        using var factory = CreateFactory();
        using var client = await CreateAuthenticatedClientAsync(factory);
        var projectId = await SetupProjectAsync(client);

        await client.PostAsJsonAsync($"/api/projects/{projectId}/elements",
            new { id = Guid.NewGuid(), kind = "system", name = "Orders" });

        var export = await client.GetAsync($"/api/projects/{projectId}/export?format=json");
        Assert.Equal(HttpStatusCode.OK, export.StatusCode);
        var json = await export.Content.ReadAsStringAsync();

        // Import is idempotent — reimporting back to same project should succeed
        var import = await client.PostAsync(
            $"/api/projects/{projectId}/import",
            new StringContent(json, System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, import.StatusCode);
    }

    [Fact]
    public async Task Unauthenticated_export_returns_401()
    {
        using var factory = CreateFactory();
        using var client = CreateClient(factory);
        var response = await client.GetAsync($"/api/projects/{Guid.NewGuid()}/export");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}

file static class ExportImportServiceCollectionExtensions
{
    public static void RemoveAll<T>(this IServiceCollection services)
    {
        var descriptors = services.Where(d => d.ServiceType == typeof(T)).ToList();
        foreach (var d in descriptors) services.Remove(d);
    }
}
