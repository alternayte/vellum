using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Web;
using Microsoft.AspNetCore.Authentication.OAuth;
using Microsoft.Extensions.DependencyInjection;
using Vellum.Modules.Identity;

namespace Vellum.Tests.Modules.Identity;

/// <summary>
/// Sign-in with GitHub against a stand-in for GitHub's three endpoints. The browser side is
/// the real handler: challenge, state, callback, completion. The browser uses https: the
/// handler's state cookie is cross-site, so it is a Secure cookie.
/// </summary>
public class GitHubSignInTests(IntegrationFixture fixture) : IClassFixture<IntegrationFixture>
{
    private static readonly Dictionary<string, string?> GitHub = new()
    {
        ["Authentication:GitHub:ClientId"] = "client-id",
        ["Authentication:GitHub:ClientSecret"] = "client-secret",
    };

    /// <summary>GitHub's token endpoint, <c>/user</c> and <c>/user/emails</c>.</summary>
    private sealed class FakeGitHub(string emails) : HttpMessageHandler
    {
        public List<string> Calls { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls.Add($"{request.Method} {request.RequestUri}");
            var json = request.RequestUri!.AbsoluteUri switch
            {
                "https://github.com/login/oauth/access_token" => """{ "access_token": "gho_stand-in", "token_type": "bearer", "scope": "user:email" }""",
                "https://api.github.com/user" => """{ "id": 4242, "login": "octo", "name": "Octo Cat", "email": "public-and-unverified@example.com" }""",
                GitHubSignIn.EmailsEndpoint => emails,
                _ => null,
            };
            return Task.FromResult(json is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }
    }

    private Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> Create(FakeGitHub github) =>
        TestHost.Create(fixture.ConnectionString, GitHub, services =>
            services.PostConfigure<OAuthOptions>(IdentityModule.GitHubScheme, options => options.Backchannel = new HttpClient(github)));

    [Fact]
    public void The_path_the_provider_calls_back_and_the_path_that_completes_the_sign_in_are_two_paths()
    {
        Assert.NotEqual(IdentityModule.GitHubCallbackPath, IdentityEndpoints.ExternalCompletePath);
        Assert.False(IdentityEndpoints.ExternalCompletePath.StartsWith(IdentityModule.GitHubCallbackPath, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task A_sign_in_with_github_goes_from_the_challenge_through_the_callback_to_a_session()
    {
        var email = $"octo-{Guid.NewGuid():N}@example.com";
        var github = new FakeGitHub($$"""
            [ { "email": "other@example.com", "primary": false, "verified": true },
              { "email": "{{email}}", "primary": true, "verified": true } ]
            """);
        using var factory = Create(github);
        using var browser = TestHost.Client(factory, https: true);

        // The challenge sends the browser to GitHub, and tells GitHub to come back to the handler's path.
        using var challenge = await browser.GetAsync("/api/auth/external/GitHub");
        Assert.Equal(HttpStatusCode.Redirect, challenge.StatusCode);
        var authorize = challenge.Headers.Location!;
        Assert.Equal("github.com", authorize.Host);
        var query = HttpUtility.ParseQueryString(authorize.Query);
        var redirectUri = new Uri(query["redirect_uri"]!);
        Assert.Equal(IdentityModule.GitHubCallbackPath, redirectUri.AbsolutePath);
        Assert.NotEqual(IdentityEndpoints.ExternalCompletePath, redirectUri.AbsolutePath);

        // GitHub sends the browser back. The handler takes the code and sends the browser on to the app's endpoint.
        using var callback = await browser.GetAsync($"{redirectUri.AbsolutePath}?code=stand-in-code&state={Uri.EscapeDataString(query["state"]!)}");
        Assert.True(callback.StatusCode == HttpStatusCode.Redirect, await callback.Content.ReadAsStringAsync());
        Assert.Equal(IdentityEndpoints.ExternalCompletePath, callback.Headers.Location!.OriginalString);

        using var complete = await browser.GetAsync(callback.Headers.Location);
        Assert.True(complete.StatusCode == HttpStatusCode.Redirect, await complete.Content.ReadAsStringAsync());
        Assert.Equal("/", complete.Headers.Location!.OriginalString);

        // The account has the verified primary address, not the public address of the profile.
        var me = await browser.GetFromJsonAsync<UserInfoResponse>("/api/auth/me");
        Assert.Equal(email, me!.Email);
        Assert.Equal("Octo Cat", me.DisplayName);
        Assert.Equal(3, github.Calls.Count);

        // A second sign-in of the same GitHub account is the same Vellum account.
        using var again = TestHost.Client(factory, https: true);
        using var secondChallenge = await again.GetAsync("/api/auth/external/github");
        var secondQuery = HttpUtility.ParseQueryString(secondChallenge.Headers.Location!.Query);
        using var secondCallback = await again.GetAsync($"{IdentityModule.GitHubCallbackPath}?code=c&state={Uri.EscapeDataString(secondQuery["state"]!)}");
        using var secondComplete = await again.GetAsync(secondCallback.Headers.Location);
        Assert.Equal(me.Id, (await again.GetFromJsonAsync<UserInfoResponse>("/api/auth/me"))!.Id);
    }

    [Fact]
    public async Task An_account_with_no_verified_primary_address_is_not_created()
    {
        var github = new FakeGitHub("""[ { "email": "claimed@example.com", "primary": true, "verified": false } ]""");
        using var factory = Create(github);
        using var browser = TestHost.Client(factory, https: true);

        using var challenge = await browser.GetAsync("/api/auth/external/GitHub");
        var query = HttpUtility.ParseQueryString(challenge.Headers.Location!.Query);
        using var callback = await browser.GetAsync($"{IdentityModule.GitHubCallbackPath}?code=c&state={Uri.EscapeDataString(query["state"]!)}");
        using var complete = await browser.GetAsync(callback.Headers.Location);

        Assert.Equal(HttpStatusCode.BadRequest, complete.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.GetAsync("/api/auth/me")).StatusCode);
    }

    [Fact]
    public async Task A_callback_without_the_state_of_this_browser_is_a_400_and_no_session()
    {
        using var factory = Create(new FakeGitHub("[]"));
        using var browser = TestHost.Client(factory, https: true);

        using var forged = await browser.GetAsync($"{IdentityModule.GitHubCallbackPath}?code=stolen&state=forged");

        Assert.Equal(HttpStatusCode.BadRequest, forged.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.GetAsync("/api/auth/me")).StatusCode);
    }

    [Fact]
    public async Task A_provider_that_is_not_configured_is_no_page()
    {
        var factory = fixture.Factory;
        using var browser = TestHost.Client(factory, https: true);

        Assert.Equal(HttpStatusCode.NotFound, (await browser.GetAsync("/api/auth/external/GitHub")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await browser.GetAsync("/api/auth/external/callback")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.GetAsync(IdentityEndpoints.ExternalCompletePath)).StatusCode);
    }
}
