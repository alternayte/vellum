using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Vellum.Shared;

namespace Vellum.Tests.Shared;

/// <summary>What every request gets before its endpoint: the same-origin rule for writes, the response headers, the limits for callers with no credential.</summary>
[Collection("Integration")]
public class RequestHardeningHostTests(IntegrationFixture fixture)
{
    private static HttpRequestMessage Post(string url, params (string Name, string Value)[] headers)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url);
        foreach (var (name, value) in headers)
            request.Headers.TryAddWithoutValidation(name, value);
        return request;
    }

    [Fact]
    public async Task A_write_that_a_page_of_another_origin_sends_is_refused_even_with_a_valid_session_cookie()
    {
        using var factory = TestHost.Create(fixture);
        using var client = await TestHost.SignInAsync(factory);
        var workspace = Guid.NewGuid();
        var body = new { id = workspace, name = "Forged" };

        // The browser says where the request comes from, and a page cannot change that header.
        foreach (var site in new[] { "cross-site", "same-site" })
        {
            using var forged = Post("/api/workspaces", ("Sec-Fetch-Site", site));
            forged.Content = JsonContent.Create(body);
            using var refused = await client.SendAsync(forged);
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
            Assert.Equal(RequestHardening.CrossSiteError, (string)(await refused.Content.ReadFromJsonAsync<JsonObject>())!["type"]!);
        }

        // An older browser sends Origin only. A sibling host name is another origin.
        using (var sibling = Post("/api/workspaces", ("Origin", "https://evil.localhost")))
        {
            sibling.Content = JsonContent.Create(body);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(sibling)).StatusCode);
        }

        // A write with no body and no content type has no other guard: sign-out.
        using (var logout = Post("/api/auth/logout", ("Sec-Fetch-Site", "cross-site")))
            Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(logout)).StatusCode);

        // A body that the endpoint reads itself, sent as a form can send it.
        using (var form = Post($"/api/projects/{Guid.NewGuid()}/stress/profiles", ("Sec-Fetch-Site", "cross-site")))
        {
            form.Content = new StringContent("{}", Encoding.UTF8, "text/plain");
            Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(form)).StatusCode);
        }

        // Nothing was written and the session still stands.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/auth/me")).StatusCode);
        Assert.DoesNotContain((await client.GetFromJsonAsync<JsonArray>("/api/workspaces"))!, w => (Guid)w!["id"]! == workspace);

        // The app's own pages, and a client that is no browser, write as before.
        using (var own = Post("/api/workspaces", ("Sec-Fetch-Site", "same-origin"), ("Origin", "http://localhost")))
        {
            own.Content = JsonContent.Create(body);
            Assert.Equal(HttpStatusCode.Created, (await client.SendAsync(own)).StatusCode);
        }

        using (var sameHost = Post("/api/workspaces", ("Origin", "https://localhost")))
        {
            sameHost.Content = JsonContent.Create(new { id = Guid.NewGuid(), name = "Behind a proxy" });
            Assert.Equal(HttpStatusCode.Created, (await client.SendAsync(sameHost)).StatusCode);
        }

        // A read is never refused by this rule.
        using var read = new HttpRequestMessage(HttpMethod.Get, "/api/auth/me");
        read.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "cross-site");
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(read)).StatusCode);
    }

    [Fact]
    public async Task An_agent_token_is_no_ambient_credential_so_its_writes_are_not_held_to_the_same_origin_rule()
    {
        using var factory = TestHost.Create(fixture);
        using var owner = await TestHost.SignInAsync(factory);
        var (workspaceId, _) = await TestHost.CreateProjectAsync(owner);
        var agent = await TestHost.CreateAgentAsync(factory, owner, workspaceId);

        using var request = Post("/api/auth/logout", ("Sec-Fetch-Site", "cross-site"));
        using var response = await agent.Client.SendAsync(request);

        // The agent boundary answers, not the same-origin rule.
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.NotEqual(RequestHardening.CrossSiteError, (string)(await response.Content.ReadFromJsonAsync<JsonObject>())!["type"]!);
    }

    [Fact]
    public async Task Every_response_has_the_security_headers_and_a_local_instance_sends_no_hsts()
    {
        using var factory = TestHost.Create(fixture);
        using var client = factory.CreateClient();

        foreach (var url in new[] { "/api/setup/status", "/api/auth/me", "/health", "/no-such-page" })
        {
            using var response = await client.GetAsync(url);
            Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
            Assert.Equal("no-referrer", response.Headers.GetValues("Referrer-Policy").Single());
            Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
            var csp = response.Headers.GetValues("Content-Security-Policy").Single();
            Assert.Contains("frame-ancestors 'none'", csp, StringComparison.Ordinal);
            Assert.Contains("script-src 'self'", csp, StringComparison.Ordinal);
            Assert.DoesNotContain("script-src 'self' 'unsafe-inline'", csp, StringComparison.Ordinal);
            Assert.False(response.Headers.Contains("Strict-Transport-Security"), url);
        }

        using var api = await client.GetAsync("/api/setup/status");
        Assert.True(api.Headers.CacheControl!.NoStore);
    }

    [Fact]
    public async Task A_hosted_instance_sends_hsts_over_https_and_marks_its_session_cookie()
    {
        var database = await TestHost.CreateDatabaseAsync(fixture);
        using var factory = TestHost.Create(database, TestHost.Hosted(TestHost.NewMasterKey()));
        using var https = TestHost.Client(factory, https: true);
        using var http = TestHost.Client(factory);

        using var secure = await https.GetAsync("/api/setup/status");
        Assert.Equal("max-age=31536000", secure.Headers.GetValues("Strict-Transport-Security").Single());
        using var plain = await http.GetAsync("/api/setup/status");
        Assert.False(plain.Headers.Contains("Strict-Transport-Security"));

        using var setup = await https.PostAsJsonAsync("/api/setup", new
        {
            email = "admin@vellum.local", password = TestHost.AdminPassword, displayName = "Admin", workspaceName = "Home", setupToken = TestHost.SetupToken,
        });
        Assert.Equal(HttpStatusCode.Created, setup.StatusCode);
        var cookie = setup.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith(".AspNetCore.Identity.Application=", StringComparison.Ordinal));
        Assert.Contains("; secure", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("; httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("; samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_caller_with_no_credential_gets_a_bounded_number_of_requests_and_of_new_accounts()
    {
        using var factory = TestHost.Create(fixture.ConnectionString, configure: services =>
            services.AddSingleton(new AnonymousRateLimits(RegistrationsPerMinute: 2, RequestsPerMinute: 7)));
        using var client = factory.CreateClient();

        // Registration has its own, smaller window. A refused body counts too.
        for (var i = 0; i < 2; i++)
            Assert.NotEqual(HttpStatusCode.TooManyRequests, (await client.PostAsJsonAsync("/api/auth/register", new { email = "x", password = "y" })).StatusCode);
        using var third = await client.PostAsJsonAsync("/api/auth/register", new { email = "x", password = "y" });
        Assert.Equal(HttpStatusCode.TooManyRequests, third.StatusCode);
        Assert.NotNull(third.Headers.RetryAfter);

        // Four of the seven requests are used: the rate limiting middleware asks twice for a request that the limit of its
        // endpoint refuses. A wrong bearer token and an open document count like any other request.
        using var wrongToken = TestHost.BearerClient(factory, "vat_not-a-token");
        Assert.Equal(HttpStatusCode.Unauthorized, (await wrongToken.GetAsync("/api/workspaces")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/llms.txt")).StatusCode);
        var allowed = 0;
        while (allowed < 10 && (await client.GetAsync("/api/setup/status")).StatusCode == HttpStatusCode.OK)
            allowed++;
        Assert.Equal(1, allowed);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await wrongToken.GetAsync("/api/workspaces")).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.GetAsync("/llms.txt")).StatusCode);

        // The probes and the app shell are not counted.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/ready")).StatusCode);
    }

    [Fact]
    public async Task A_signed_in_caller_is_not_held_to_the_limit_for_callers_with_no_credential()
    {
        using var factory = TestHost.Create(fixture.ConnectionString, configure: services =>
            services.AddSingleton(new AnonymousRateLimits(RegistrationsPerMinute: 5, RequestsPerMinute: 3)));
        using var client = await TestHost.SignInAsync(factory);

        for (var i = 0; i < 10; i++)
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/auth/me")).StatusCode);
    }

    [Fact]
    public async Task An_unexpected_error_answers_with_no_exception_text()
    {
        using var factory = TestHost.Create(fixture);
        using var client = await TestHost.SignInAsync(factory);

        // A project that does not exist surfaces as an exception in this handler.
        var missing = Guid.NewGuid();
        using var response = await client.GetAsync($"/api/projects/{missing}/stress/defaults");
        var body = await response.Content.ReadAsStringAsync();

        Assert.True((int)response.StatusCode >= 400);
        Assert.DoesNotContain("Exception", body, StringComparison.Ordinal);
        Assert.DoesNotContain("   at ", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Vellum.Modules", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Password=", body, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("views", "name")]
    [InlineData("spaces", "name")]
    [InlineData("docs", "title")]
    public async Task Text_that_cannot_be_stored_is_a_400_that_names_the_field(string resource, string field)
    {
        using var factory = TestHost.Create(fixture);
        using var client = await TestHost.SignInAsync(factory);
        var (_, projectId) = await TestHost.CreateProjectAsync(client);
        var url = $"/api/projects/{projectId}/{resource}";

        async Task RefusedAsync(Task<HttpResponseMessage> request, string expected)
        {
            using var response = await request;
            var text = await response.Content.ReadAsStringAsync();
            Assert.True(response.StatusCode == HttpStatusCode.BadRequest, $"{(int)response.StatusCode} {text}");
            Assert.Contains(JsonNode.Parse(text)!["errors"]!.AsArray(), e => (string)e!["field"]! == expected);
        }

        JsonObject Body(string value) => new() { ["id"] = Guid.NewGuid(), [field] = value };

        await RefusedAsync(client.PostAsJsonAsync(url, Body("a\u0000b")), field);
        await RefusedAsync(client.PostAsJsonAsync(url, Body(new string('n', TextRule.MaxName + 1))), field);
        await RefusedAsync(client.PostAsJsonAsync(url, Body("   ")), field);

        var id = Guid.NewGuid();
        var created = Body("Fine");
        created["id"] = id;
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync(url, created)).StatusCode);
        await RefusedAsync(client.PatchAsJsonAsync($"{url}/{id}", new JsonObject { [field] = "line\nbreak" }), field);
        await RefusedAsync(client.PatchAsJsonAsync($"{url}/{id}", new JsonObject { [field] = new string('n', TextRule.MaxName + 1) }), field);

        if (resource == "docs")
            await RefusedAsync(client.PatchAsJsonAsync($"{url}/{id}", new JsonObject { ["content"] = "page\u0000" }), "content");
        if (resource == "views")
            await RefusedAsync(client.PatchAsJsonAsync($"{url}/{id}", new JsonObject { ["activeLens"] = "lens\u0000" }), "activeLens");
    }
}
