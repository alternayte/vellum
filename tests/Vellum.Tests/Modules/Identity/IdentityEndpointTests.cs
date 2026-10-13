using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
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
                builder.UseEnvironment("Testing");
                builder.UseSetting("ConnectionStrings:DefaultConnection", _fixture.ConnectionString);
                builder.ConfigureServices(services =>
                {
                    services.RemoveAll<DbContextOptions<AppIdentityDbContext>>();
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
            new { email, password = "Test12345!", displayName = "Test User" });
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

    [Fact]
    public async Task A_new_password_has_at_least_ten_characters()
    {
        using var factory = CreateFactory();
        using var client = CreateClient(factory);

        var nine = await client.PostAsJsonAsync("/api/auth/register",
            new { email = $"test-{Guid.NewGuid():N}@vellum.local", password = "Test1234!", displayName = "Test" });
        var ten = await client.PostAsJsonAsync("/api/auth/register",
            new { email = $"test-{Guid.NewGuid():N}@vellum.local", password = "Test12345!", displayName = "Test" });

        Assert.Equal(HttpStatusCode.BadRequest, nine.StatusCode);
        Assert.Equal(HttpStatusCode.Created, ten.StatusCode);
    }

    [Fact]
    public async Task Five_wrong_passwords_lock_the_account_and_the_answer_does_not_say_so()
    {
        using var factory = CreateFactory();
        using var client = CreateClient(factory);
        var email = $"test-{Guid.NewGuid():N}@vellum.local";
        const string password = "Test12345!";
        Assert.Equal(HttpStatusCode.Created,
            (await client.PostAsJsonAsync("/api/auth/register", new { email, password, displayName = "Test" })).StatusCode);
        using var attacker = CreateClient(factory);

        var answers = new List<string>();
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var wrong = await attacker.PostAsJsonAsync("/api/auth/login", new { email, password = "Wrong12345!" });
            Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
            answers.Add(await wrong.Content.ReadAsStringAsync());
        }

        // The right password does not open a locked account.
        var locked = await attacker.PostAsJsonAsync("/api/auth/login", new { email, password });
        Assert.Equal(HttpStatusCode.Unauthorized, locked.StatusCode);
        answers.Add(await locked.Content.ReadAsStringAsync());
        // An unknown account gets the same answer as a wrong password and as a locked account.
        var unknown = await attacker.PostAsJsonAsync("/api/auth/login", new { email = "nobody@vellum.local", password });
        Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);
        answers.Add(await unknown.Content.ReadAsStringAsync());
        Assert.Single(answers.Distinct());

        using var scope = factory.Services.CreateScope();
        var user = await scope.ServiceProvider.GetRequiredService<AppIdentityDbContext>().Users.SingleAsync(u => u.Email == email);
        Assert.True(user.LockoutEnd > DateTimeOffset.UtcNow);
        Assert.True(user.LockoutEnd < DateTimeOffset.UtcNow.AddMinutes(10));
    }

    [Theory]
    [InlineData("/api/auth/login")]
    [InlineData("/api/setup")]
    public async Task Sign_in_and_setup_are_rate_limited_for_each_client(string url)
    {
        using var factory = CreateFactory();
        using var client = CreateClient(factory);
        var body = new { email = "nobody@vellum.local", password = "Wrong12345!", displayName = "N", workspaceName = "W", setupToken = "x" };

        for (var attempt = 0; attempt < Vellum.Shared.RequestLimits.CredentialPermits; attempt++)
            Assert.NotEqual(HttpStatusCode.TooManyRequests, (await client.PostAsJsonAsync(url, body)).StatusCode);

        var limited = await client.PostAsJsonAsync(url, body);
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.InRange(int.Parse(limited.Headers.GetValues("Retry-After").Single()), 1, 60);
        Assert.Equal("rate_limited", (await limited.Content.ReadFromJsonAsync<Vellum.Shared.ErrorResponse>())!.Type);
        // Another endpoint is not affected.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/setup/status")).StatusCode);
    }

    // ---- External sign-in ------------------------------------------------------

    /// <summary>Stands in for a provider: signs the caller in to the external scheme, as a provider's callback does.</summary>
    private sealed class ExternalSignIn : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            // Not a branch: a branch changes the path base, and the cookie would get that path.
            app.Use(async (http, proceed) =>
            {
                if (http.Request.Path != "/test/external-sign-in")
                {
                    await proceed(http);
                    return;
                }

                var identity = new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.NameIdentifier, http.Request.Query["sub"].ToString()),
                    new Claim(ClaimTypes.Email, http.Request.Query["email"].ToString()),
                ], "Test");
                var properties = new AuthenticationProperties();
                properties.Items["LoginProvider"] = "Test";
                await http.SignInAsync(IdentityConstants.ExternalScheme, new ClaimsPrincipal(identity), properties);
            });
            next(app);
        };
    }

    private static async Task<HttpResponseMessage> ExternalCallbackAsync(HttpClient client, string email, string subject)
    {
        var signIn = await client.GetAsync($"/test/external-sign-in?sub={subject}&email={Uri.EscapeDataString(email)}");
        Assert.Equal(HttpStatusCode.OK, signIn.StatusCode);
        Assert.True(signIn.Headers.Contains("Set-Cookie"), "The stand-in provider set no cookie.");
        return await client.GetAsync("/api/auth/external/complete");
    }

    [Fact]
    public async Task An_external_sign_in_does_not_take_over_an_account_that_has_the_same_email()
    {
        using var factory = TestHost.Create(_fixture.ConnectionString, configure: services => services.AddTransient<IStartupFilter, ExternalSignIn>());
        using var victim = TestHost.Client(factory);
        var email = $"victim-{Guid.NewGuid():N}@vellum.local";
        Assert.Equal(HttpStatusCode.Created,
            (await victim.PostAsJsonAsync("/api/auth/register", new { email, password = "Test12345!", displayName = "Victim" })).StatusCode);

        // An attacker controls a provider account that claims the victim's address.
        using var attacker = TestHost.Client(factory);
        var response = await ExternalCallbackAsync(attacker, email, "attacker-subject");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("account_exists", (await response.Content.ReadFromJsonAsync<Vellum.Shared.ErrorResponse>())!.Type);
        Assert.Equal(HttpStatusCode.Unauthorized, (await attacker.GetAsync("/api/auth/me")).StatusCode);
        using var scope = factory.Services.CreateScope();
        Assert.False(await scope.ServiceProvider.GetRequiredService<AppIdentityDbContext>().UserLogins.AnyAsync(l => l.ProviderKey == "attacker-subject"));

        // A new address creates an account, and the same provider account signs in to it again.
        var newEmail = $"new-{Guid.NewGuid():N}@vellum.local";
        using var newcomer = TestHost.Client(factory);
        Assert.Equal(HttpStatusCode.Redirect, (await ExternalCallbackAsync(newcomer, newEmail, $"sub-{newEmail}")).StatusCode);
        Assert.Equal(newEmail, (await newcomer.GetFromJsonAsync<UserInfoResponse>("/api/auth/me"))!.Email);
        using var returning = TestHost.Client(factory);
        Assert.Equal(HttpStatusCode.Redirect, (await ExternalCallbackAsync(returning, newEmail, $"sub-{newEmail}")).StatusCode);
        Assert.Equal(newEmail, (await returning.GetFromJsonAsync<UserInfoResponse>("/api/auth/me"))!.Email);
    }

    [Fact]
    public async Task An_external_sign_in_cannot_create_the_first_user()
    {
        var database = await TestHost.CreateDatabaseAsync(_fixture);
        using var factory = TestHost.Create(database, configure: services => services.AddTransient<IStartupFilter, ExternalSignIn>());
        using var client = TestHost.Client(factory);

        var response = await ExternalCallbackAsync(client, "first@vellum.local", "first-subject");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("setup_required", (await response.Content.ReadFromJsonAsync<Vellum.Shared.ErrorResponse>())!.Type);
        using var scope = factory.Services.CreateScope();
        Assert.False(await scope.ServiceProvider.GetRequiredService<AppIdentityDbContext>().Users.AnyAsync());
    }

    [Fact]
    public async Task The_sign_in_methods_name_only_what_the_instance_offers()
    {
        using var factory = CreateFactory();
        using var client = CreateClient(factory);

        // No credential is needed. This instance has no external provider.
        var plain = await client.GetFromJsonAsync<SignInMethodsResponse>("/api/auth/methods");
        Assert.True(plain!.Password);
        Assert.Empty(plain.Providers);

        // The answer on registration is the answer that the registration itself gives.
        var register = await client.PostAsJsonAsync("/api/auth/register",
            new { email = $"test-{Guid.NewGuid():N}@vellum.local", password = "Test12345!", displayName = "Test" });
        Assert.Equal(register.StatusCode != HttpStatusCode.Forbidden, plain.RegistrationOpen);

        using var withGitHub = CreateFactory().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Authentication:GitHub:ClientId", "client-id");
            builder.UseSetting("Authentication:GitHub:ClientSecret", "client-secret");
        });
        using var second = CreateClient(withGitHub);
        var configured = await second.GetFromJsonAsync<SignInMethodsResponse>("/api/auth/methods");
        Assert.Equal([IdentityModule.GitHubScheme], configured!.Providers);
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
