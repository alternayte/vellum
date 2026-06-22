using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vellum.Kernel.EventStore;
using Vellum.Modules.Identity;

namespace Vellum.Tests.Modules.Identity;

[Collection("Integration")]
public class IdentityEndpointTests
{
    private readonly IntegrationFixture _fixture;

    public IdentityEndpointTests(IntegrationFixture fixture) => _fixture = fixture;

    private WebApplicationFactory<Program> CreateFactory()
    {
        return new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureServices(services =>
                {
                    services.RemoveAll<DbContextOptions<EventStoreDbContext>>();
                    services.RemoveAll<DbContextOptions<AppIdentityDbContext>>();
                    services.AddDbContext<EventStoreDbContext>(o =>
                        o.UseNpgsql(_fixture.ConnectionString).UseSnakeCaseNamingConvention());
                    services.AddDbContext<AppIdentityDbContext>(o =>
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

    [Fact]
    public async Task Register_and_get_me_returns_user()
    {
        using var factory = CreateFactory();
        using var client = CreateClient(factory);
        var email = $"test-{Guid.NewGuid():N}@vellum.local";

        var registerResponse = await client.PostAsJsonAsync("/api/auth/register",
            new { email, password = "Test123!", displayName = "Test User" });
        Assert.Equal(HttpStatusCode.Created, registerResponse.StatusCode);

        var meResponse = await client.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.OK, meResponse.StatusCode);

        var me = await meResponse.Content.ReadFromJsonAsync<UserInfoResponse>();
        Assert.Equal(email, me!.Email);
        Assert.Equal("Test User", me.DisplayName);
    }

    [Fact]
    public async Task Login_with_wrong_password_returns_401()
    {
        using var factory = CreateFactory();
        using var client = CreateClient(factory);

        var response = await client.PostAsJsonAsync("/api/auth/login",
            new { email = "noone@vellum.local", password = "Wrong123!" });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Me_without_auth_returns_401()
    {
        using var factory = CreateFactory();
        using var client = CreateClient(factory);

        var response = await client.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Register_with_weak_password_returns_400()
    {
        using var factory = CreateFactory();
        using var client = CreateClient(factory);
        var email = $"test-{Guid.NewGuid():N}@vellum.local";

        var response = await client.PostAsJsonAsync("/api/auth/register",
            new { email, password = "weak", displayName = "Test" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}

// Helper to remove all registrations of a type
file static class ServiceCollectionExtensions
{
    public static void RemoveAll<T>(this IServiceCollection services)
    {
        var descriptors = services.Where(d => d.ServiceType == typeof(T)).ToList();
        foreach (var d in descriptors) services.Remove(d);
    }
}
