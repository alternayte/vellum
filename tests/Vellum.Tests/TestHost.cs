using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vellum.Modules.Docs;
using Vellum.Modules.Drafts;
using Vellum.Modules.Identity;
using Vellum.Modules.Modelling;
using Vellum.Modules.Schemas;
using Npgsql;
using Vellum.Modules.Review;
using Vellum.Modules.Settings.Setup;
using Vellum.Shared;
using Vellum.Modules.Views;
using Vellum.Modules.Workspaces;

namespace Vellum.Tests;

/// <summary>The app over the fixture's database, and the calls most endpoint tests start with.</summary>
internal static class TestHost
{
    public static WebApplicationFactory<Program> Create(IntegrationFixture fixture) => Create(fixture.ConnectionString);

    /// <summary>
    /// The app over any database, with bootstrap values as the environment would give them
    /// (<c>Vellum:Profile</c>, <c>Vellum:MasterKey</c>, <c>AllowedHosts</c> and so on).
    /// </summary>
    public static WebApplicationFactory<Program> Create(
        string connectionString,
        IReadOnlyDictionary<string, string?>? settings = null,
        Action<IServiceCollection>? configure = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("ConnectionStrings:DefaultConnection", connectionString);
            foreach (var (key, value) in settings ?? new Dictionary<string, string?>())
                builder.UseSetting(key, value);
            builder.ConfigureServices(services =>
            {
                var cs = connectionString;
                Replace<AppIdentityDbContext>(services, cs);
                Replace<WorkspacesDbContext>(services, cs);
                Replace<ModellingDbContext>(services, cs);
                Replace<ViewsDbContext>(services, cs);
                Replace<DocsDbContext>(services, cs);
                Replace<DraftsDbContext>(services, cs);
                Replace<SchemasDbContext>(services, cs);
                Replace<ReviewDbContext>(services, cs);
                // Every test client has one address, and tests register accounts freely.
                services.AddSingleton(new AnonymousRateLimits(RegistrationsPerMinute: 100_000, RequestsPerMinute: 1_000_000));
                configure?.Invoke(services);
            });
        });

    /// <summary>A new empty database on the fixture's server. The app creates its schema when it starts.</summary>
    public static async Task<string> CreateDatabaseAsync(IntegrationFixture fixture)
    {
        var database = $"t_{Guid.NewGuid():N}";
        await using var admin = new NpgsqlConnection(fixture.ConnectionString);
        await admin.OpenAsync();
        await using var create = new NpgsqlCommand($"CREATE DATABASE {database}", admin);
        await create.ExecuteNonQueryAsync();
        return new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Database = database }.ToString();
    }

    public static string NewMasterKey() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    /// <summary>The bootstrap of a hosted instance that is safe to start.</summary>
    public static Dictionary<string, string?> Hosted(string masterKey, string? setupToken = SetupToken) => new()
    {
        ["Vellum:Profile"] = "hosted",
        ["Vellum:MasterKey"] = masterKey,
        ["Vellum:SetupToken"] = setupToken,
        ["AllowedHosts"] = "localhost",
    };

    public const string SetupToken = "setup-token-for-tests-0123456789";
    public const string AdminPassword = "Admin12345!";

    /// <summary>A cookie-keeping client. A hosted instance marks its cookie Secure, so its client uses https.</summary>
    public static HttpClient Client(WebApplicationFactory<Program> factory, bool https = false) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            HandleCookies = true,
            AllowAutoRedirect = false,
            BaseAddress = new Uri(https ? "https://localhost" : "http://localhost"),
        });

    /// <summary>Runs first-run setup and returns the signed-in admin client.</summary>
    public static async Task<(HttpClient Admin, SetupResponse Created, string Email)> SetUpAsync(
        WebApplicationFactory<Program> factory, string? setupToken = null, bool https = false)
    {
        var client = Client(factory, https);
        var email = $"admin-{Guid.NewGuid():N}@vellum.local";
        setupToken ??= SetupCode(factory);
        var response = await Created(client.PostAsJsonAsync("/api/setup",
            new { email, password = AdminPassword, displayName = "Admin", workspaceName = "Home", setupToken }));
        return (client, (await response.Content.ReadFromJsonAsync<SetupResponse>())!, email);
    }

    /// <summary>
    /// The setup code that a local instance with no user generated when it started, read
    /// from the file where an operator finds it.
    /// </summary>
    public static string SetupCode(WebApplicationFactory<Program> factory) =>
        File.ReadAllText(factory.Services.GetRequiredService<SetupAccess>().CodeFilePath).Trim();

    /// <summary>
    /// Saves settings as a client does: reads the current version, then sends it in
    /// <c>If-Match</c>. A caller that cannot read the settings sends version 0.
    /// </summary>
    public static async Task<HttpResponseMessage> PutSettingsAsync(this HttpClient client, string url, HttpContent content)
    {
        using var current = await client.GetAsync(url);
        using var request = new HttpRequestMessage(HttpMethod.Put, url) { Content = content };
        request.Headers.TryAddWithoutValidation("If-Match", current.Headers.ETag?.Tag ?? "\"0\"");
        return await client.SendAsync(request);
    }

    /// <summary>Saves the policy and the defaults of <paramref name="settings"/>; its other fields are response fields.</summary>
    public static Task<HttpResponseMessage> PutInstanceSettingsAsync(this HttpClient client, JsonObject settings) =>
        client.PutSettingsAsync("/api/instance/settings", JsonContent.Create(new JsonObject
        {
            ["policy"] = settings["policy"]?.DeepClone(),
            ["defaults"] = settings["defaults"]?.DeepClone(),
        }));

    /// <summary>A resolver for tests: named hosts map to fixed addresses and no test reaches DNS.</summary>
    public static void UseResolver(IServiceCollection services, IReadOnlyDictionary<string, string> hosts) =>
        services.AddSingleton<HostResolver>(_ => (host, _) => Task.FromResult(
            hosts.TryGetValue(host, out var address) ? new[] { IPAddress.Parse(address) } : [IPAddress.Parse("93.184.216.34")]));

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
            new { email = $"test-{Guid.NewGuid():N}@vellum.local", password = "Test12345!", displayName = "Test User" });
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

    /// <summary>
    /// Creates a real agent token through the endpoint, as the owner does in the app, and
    /// returns a client that sends it as a bearer credential and keeps no cookies.
    /// </summary>
    public static async Task<Agent> CreateAgentAsync(
        WebApplicationFactory<Program> factory, HttpClient owner, Guid workspaceId,
        string[]? scopes = null, Guid? projectId = null, DateTimeOffset? expiresAt = null)
    {
        var response = await Created(owner.PostAsJsonAsync($"/api/workspaces/{workspaceId}/agent-tokens",
            new { name = "Test agent", scopes = scopes ?? ["read", "propose", "export"], projectId, expiresAt }));
        var created = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
        var token = created["token"]!.GetValue<string>();
        return new Agent(BearerClient(factory, token), created["id"]!.GetValue<Guid>(), token);
    }

    /// <summary>A client that sends <paramref name="token"/> as a bearer credential and keeps no cookies.</summary>
    public static HttpClient BearerClient(WebApplicationFactory<Program> factory, string token)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false, AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    public static async Task<HttpResponseMessage> Created(Task<HttpResponseMessage> request)
    {
        var response = await request;
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return response;
    }
}

/// <summary>An agent token and the client that presents it.</summary>
internal sealed record Agent(HttpClient Client, Guid TokenId, string Token);
