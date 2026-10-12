using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vellum.Modules.Docs;
using Vellum.Modules.Drafts;
using Vellum.Modules.Identity;
using Vellum.Modules.Modelling;
using Vellum.Modules.Schemas;
using Vellum.Modules.Scoring;
using Vellum.Modules.Views;
using Vellum.Modules.Workspaces;

namespace Vellum.Tests;

/// <summary>The app over the fixture's database, and the calls most endpoint tests start with.</summary>
internal static class TestHost
{
    public static WebApplicationFactory<Program> Create(IntegrationFixture fixture) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("ConnectionStrings:DefaultConnection", fixture.ConnectionString);
            builder.ConfigureServices(services =>
            {
                var cs = fixture.ConnectionString;
                Replace<AppIdentityDbContext>(services, cs);
                Replace<WorkspacesDbContext>(services, cs);
                Replace<ModellingDbContext>(services, cs);
                Replace<ViewsDbContext>(services, cs);
                Replace<DocsDbContext>(services, cs);
                Replace<DraftsDbContext>(services, cs);
                Replace<SchemasDbContext>(services, cs);
                Replace<ScoringDbContext>(services, cs);
            });
        });

    private static void Replace<TContext>(IServiceCollection services, string connectionString) where TContext : DbContext
    {
        foreach (var descriptor in services.Where(d => d.ServiceType == typeof(DbContextOptions<TContext>)).ToList())
            services.Remove(descriptor);
        services.AddDbContext<TContext>(o => o.UseNpgsql(connectionString).UseSnakeCaseNamingConvention());
    }

    /// <summary>A client signed in as a new user.</summary>
    public static async Task<HttpClient> SignInAsync(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true, AllowAutoRedirect = false });
        var response = await client.PostAsJsonAsync("/api/auth/register",
            new { email = $"test-{Guid.NewGuid():N}@vellum.local", password = "Test123!", displayName = "Test User" });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return client;
    }

    public static async Task<(Guid WorkspaceId, Guid ProjectId)> CreateProjectAsync(HttpClient client)
    {
        var workspaceId = Guid.NewGuid();
        await Created(client.PostAsJsonAsync("/api/workspaces", new { id = workspaceId, name = "Test WS" }));
        var projectId = Guid.NewGuid();
        await Created(client.PostAsJsonAsync($"/api/workspaces/{workspaceId}/projects", new { id = projectId, name = "Test Project" }));
        return (workspaceId, projectId);
    }

    /// <summary>A second user who is a member of the workspace with <paramref name="role"/>.</summary>
    public static async Task<HttpClient> SignInMemberAsync(
        WebApplicationFactory<Program> factory, HttpClient owner, Guid workspaceId, string role)
    {
        var member = await SignInAsync(factory);
        var me = await member.GetFromJsonAsync<UserInfoResponse>("/api/auth/me");
        await Created(owner.PostAsJsonAsync($"/api/workspaces/{workspaceId}/members", new { userId = me!.Id, role }));
        return member;
    }

    public static async Task<HttpResponseMessage> Created(Task<HttpResponseMessage> request)
    {
        var response = await request;
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return response;
    }
}
