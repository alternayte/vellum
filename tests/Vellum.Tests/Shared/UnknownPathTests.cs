using System.Net;
using System.Net.Http.Json;
using Vellum.Shared;

namespace Vellum.Tests.Shared;

/// <summary>
/// A path that no endpoint has. Under the API, the MCP endpoint and the agent files it is a
/// 404 in the error shape of the API; anywhere else it is a page of the app.
/// </summary>
public class UnknownPathTests(IntegrationFixture fixture) : IClassFixture<IntegrationFixture>
{
    [Fact]
    public async Task An_unknown_API_path_is_a_404_with_the_error_envelope_and_an_unknown_page_is_the_app()
    {
        var webRoot = Directory.CreateTempSubdirectory("vellum-webroot-");
        try
        {
            await File.WriteAllTextAsync(Path.Combine(webRoot.FullName, "index.html"), "<!doctype html><title>Vellum</title>");
            using var factory = TestHost.Create(fixture.ConnectionString, new Dictionary<string, string?> { ["webroot"] = webRoot.FullName });
            using var client = await TestHost.SignInAsync(factory);

            foreach (var (method, path) in new[]
            {
                (HttpMethod.Get, "/api/nothing-here"),
                (HttpMethod.Get, $"/api/projects/{Guid.NewGuid()}/nothing-here"),
                (HttpMethod.Head, "/api/nothing-here"),
                (HttpMethod.Get, "/api"),
                (HttpMethod.Get, "/mcp/nothing-here"),
                (HttpMethod.Get, "/skills/nothing-here"),
            })
            {
                using var request = new HttpRequestMessage(method, path);
                var response = await client.SendAsync(request);
                var body = await response.Content.ReadAsStringAsync();
                Assert.True(response.StatusCode == HttpStatusCode.NotFound, $"{method} {path}: {response.StatusCode} {body}");
                Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
                if (method == HttpMethod.Get)
                    Assert.Equal("not_found", (await response.Content.ReadFromJsonAsync<ErrorResponse>())!.Type);
            }

            // A page of the app is still the app, whatever its path.
            var page = await client.GetAsync($"/projects/{Guid.NewGuid()}");
            Assert.Equal(HttpStatusCode.OK, page.StatusCode);
            Assert.Equal("text/html", page.Content.Headers.ContentType?.MediaType);
        }
        finally
        {
            webRoot.Delete(recursive: true);
        }
    }
}
