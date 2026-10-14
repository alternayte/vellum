using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Vellum.Modules.Modelling;

namespace Vellum.Tests.Modules.Modelling;

public class ModellingEndpointTests : IClassFixture<IntegrationFixture>
{
    private readonly IntegrationFixture _fixture;

    public ModellingEndpointTests(IntegrationFixture fixture) => _fixture = fixture;

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
            new { email, password = "Test12345!", displayName = "Test User" });
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
    public async Task Add_element_and_get_returns_it()
    {
        var factory = _fixture.Factory;
        using var client = await CreateAuthenticatedClientAsync(factory);
        var projectId = await SetupProjectAsync(client);
        var elementId = Guid.NewGuid();

        var addResponse = await client.PostAsJsonAsync($"/api/projects/{projectId}/elements",
            new { id = elementId, kind = "system", name = "Orders", description = "Order system", tags = new[] { "core" } });
        Assert.Equal(HttpStatusCode.Created, addResponse.StatusCode);

        var getResponse = await client.GetAsync($"/api/projects/{projectId}/elements/{elementId}");
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
        var dto = await getResponse.Content.ReadFromJsonAsync<ElementDto>();
        Assert.Equal("Orders", dto!.Name);
        Assert.Equal("system", dto.Kind);
    }

    [Fact]
    public async Task Patch_element_renames_it()
    {
        var factory = _fixture.Factory;
        using var client = await CreateAuthenticatedClientAsync(factory);
        var projectId = await SetupProjectAsync(client);
        var elementId = Guid.NewGuid();

        await client.PostAsJsonAsync($"/api/projects/{projectId}/elements",
            new { id = elementId, kind = "system", name = "Old Name" });

        var patchResponse = await client.PatchAsJsonAsync($"/api/projects/{projectId}/elements/{elementId}",
            new { name = "New Name" });
        Assert.Equal(HttpStatusCode.OK, patchResponse.StatusCode);

        var getResponse = await client.GetAsync($"/api/projects/{projectId}/elements/{elementId}");
        var dto = await getResponse.Content.ReadFromJsonAsync<ElementDto>();
        Assert.Equal("New Name", dto!.Name);
    }

    [Fact]
    public async Task Delete_element_cascades_relationships()
    {
        var factory = _fixture.Factory;
        using var client = await CreateAuthenticatedClientAsync(factory);
        var projectId = await SetupProjectAsync(client);
        var sysA = Guid.NewGuid();
        var sysB = Guid.NewGuid();
        var relId = Guid.NewGuid();

        await client.PostAsJsonAsync($"/api/projects/{projectId}/elements",
            new { id = sysA, kind = "system", name = "A" });
        await client.PostAsJsonAsync($"/api/projects/{projectId}/elements",
            new { id = sysB, kind = "system", name = "B" });
        await client.PostAsJsonAsync($"/api/projects/{projectId}/relationships",
            new { id = relId, fromId = sysA, toId = sysB, label = "uses" });

        var deleteResponse = await client.DeleteAsync($"/api/projects/{projectId}/elements/{sysA}");
        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);

        var relResponse = await client.GetAsync($"/api/projects/{projectId}/relationships/{relId}");
        Assert.Equal(HttpStatusCode.NotFound, relResponse.StatusCode);
    }

    [Fact]
    public async Task Unauthenticated_element_request_returns_401()
    {
        var factory = _fixture.Factory;
        using var client = CreateClient(factory);
        var response = await client.GetAsync($"/api/projects/{Guid.NewGuid()}/elements");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}

