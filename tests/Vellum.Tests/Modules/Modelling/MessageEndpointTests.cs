using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Vellum.Modules.Modelling;

namespace Vellum.Tests.Modules.Modelling;

public class MessageEndpointTests : IClassFixture<IntegrationFixture>
{
    private readonly IntegrationFixture _fixture;

    public MessageEndpointTests(IntegrationFixture fixture) => _fixture = fixture;

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
    public async Task Create_and_get_message()
    {
        var factory = _fixture.Factory;
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
        var factory = _fixture.Factory;
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
        var factory = _fixture.Factory;
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

    [Fact]
    public async Task Add_message_with_unknown_schema_returns_400()
    {
        var factory = _fixture.Factory;
        using var client = await CreateAuthenticatedClientAsync(factory);
        var projectId = await SetupProjectAsync(client);

        var producerId = Guid.NewGuid();
        await client.PostAsJsonAsync($"/api/projects/{projectId}/elements",
            new { id = producerId, kind = "system", name = "Orders" });

        var response = await client.PostAsJsonAsync($"/api/projects/{projectId}/messages", new
        {
            id = Guid.NewGuid(),
            name = "OrderPlaced",
            producerId,
            consumerIds = Array.Empty<Guid>(),
            schemaId = Guid.NewGuid(),
            tags = Array.Empty<string>(),
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Update_message_with_unknown_schema_returns_400()
    {
        var factory = _fixture.Factory;
        using var client = await CreateAuthenticatedClientAsync(factory);
        var projectId = await SetupProjectAsync(client);

        var producerId = Guid.NewGuid();
        await client.PostAsJsonAsync($"/api/projects/{projectId}/elements",
            new { id = producerId, kind = "system", name = "Orders" });

        var messageId = Guid.NewGuid();
        var created = await client.PostAsJsonAsync($"/api/projects/{projectId}/messages", new
        {
            id = messageId,
            name = "OrderPlaced",
            producerId,
            consumerIds = Array.Empty<Guid>(),
            tags = Array.Empty<string>(),
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var patch = await client.PatchAsJsonAsync($"/api/projects/{projectId}/messages/{messageId}", new
        {
            schemaId = Guid.NewGuid(),
            setSchemaId = true,
        });

        Assert.Equal(HttpStatusCode.BadRequest, patch.StatusCode);
    }

    [Fact]
    public async Task Add_message_with_existing_schema_succeeds()
    {
        var factory = _fixture.Factory;
        using var client = await CreateAuthenticatedClientAsync(factory);
        var projectId = await SetupProjectAsync(client);

        var producerId = Guid.NewGuid();
        await client.PostAsJsonAsync($"/api/projects/{projectId}/elements",
            new { id = producerId, kind = "system", name = "Orders" });

        var schemaId = Guid.NewGuid();
        var schemaResponse = await client.PostAsJsonAsync($"/api/projects/{projectId}/schemas", new
        {
            id = schemaId,
            name = "OrderPlaced",
            content = "{\"type\":\"object\"}",
        });
        Assert.Equal(HttpStatusCode.Created, schemaResponse.StatusCode);

        var response = await client.PostAsJsonAsync($"/api/projects/{projectId}/messages", new
        {
            id = Guid.NewGuid(),
            name = "OrderPlaced",
            producerId,
            consumerIds = Array.Empty<Guid>(),
            schemaId,
            tags = Array.Empty<string>(),
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }
}

