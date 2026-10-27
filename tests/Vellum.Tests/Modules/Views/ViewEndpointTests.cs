using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vellum.Modules.Views;
using Vellum.Shared;

namespace Vellum.Tests.Modules.Views;

public class ViewEndpointTests : IClassFixture<IntegrationFixture>
{
    private readonly IntegrationFixture _fixture;

    public ViewEndpointTests(IntegrationFixture fixture) => _fixture = fixture;

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
    public async Task Create_view_and_get_returns_it_with_empty_layout()
    {
        var factory = _fixture.Factory;
        using var client = await CreateAuthenticatedClientAsync(factory);
        var projectId = await SetupProjectAsync(client);
        var viewId = Guid.NewGuid();

        var createResponse = await client.PostAsJsonAsync($"/api/projects/{projectId}/views",
            new { id = viewId, name = "Context View" });
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);

        var getResponse = await client.GetAsync($"/api/projects/{projectId}/views/{viewId}");
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
        var detail = await getResponse.Content.ReadFromJsonAsync<ViewDetailDto>();
        Assert.Equal("Context View", detail!.Name);
        Assert.Empty(detail.Positions);
    }

    [Fact]
    public async Task Save_layout_and_get_returns_positions()
    {
        var factory = _fixture.Factory;
        using var client = await CreateAuthenticatedClientAsync(factory);
        var projectId = await SetupProjectAsync(client);
        var viewId = Guid.NewGuid();
        var elementId = Guid.NewGuid();

        await client.PostAsJsonAsync($"/api/projects/{projectId}/views",
            new { id = viewId, name = "Layout Test" });

        var layoutResponse = await client.PutAsJsonAsync($"/api/projects/{projectId}/views/{viewId}/layout",
            new { positions = new[] { new { elementId, x = 100.0, y = 200.0 } } });
        Assert.Equal(HttpStatusCode.OK, layoutResponse.StatusCode);

        var getResponse = await client.GetAsync($"/api/projects/{projectId}/views/{viewId}");
        var detail = await getResponse.Content.ReadFromJsonAsync<ViewDetailDto>();
        Assert.Single(detail!.Positions);
        Assert.Equal(100.0, detail.Positions[0].X);
    }

    [Fact]
    public async Task Update_view_with_stale_updated_at_returns_409()
    {
        var factory = _fixture.Factory;
        using var client = await CreateAuthenticatedClientAsync(factory);
        var projectId = await SetupProjectAsync(client);
        var viewId = Guid.NewGuid();

        await client.PostAsJsonAsync($"/api/projects/{projectId}/views",
            new { id = viewId, name = "Concurrency Test" });

        // First update succeeds
        await client.PatchAsJsonAsync($"/api/projects/{projectId}/views/{viewId}",
            new { name = "Updated" });

        // Second update with stale timestamp returns 409
        var staleResponse = await client.PatchAsJsonAsync($"/api/projects/{projectId}/views/{viewId}",
            new { name = "Stale", updatedAt = DateTimeOffset.MinValue });
        Assert.Equal(HttpStatusCode.Conflict, staleResponse.StatusCode);
    }

    [Fact]
    public async Task List_views_returns_views_for_project()
    {
        var factory = _fixture.Factory;
        using var client = await CreateAuthenticatedClientAsync(factory);
        var projectId = await SetupProjectAsync(client);

        await client.PostAsJsonAsync($"/api/projects/{projectId}/views",
            new { id = Guid.NewGuid(), name = "View A" });
        await client.PostAsJsonAsync($"/api/projects/{projectId}/views",
            new { id = Guid.NewGuid(), name = "View B" });

        var listResponse = await client.GetAsync($"/api/projects/{projectId}/views");
        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
        var views = await listResponse.Content.ReadFromJsonAsync<List<ViewDto>>();
        Assert.Equal(2, views!.Count);
    }

    [Fact]
    public async Task Delete_view_removes_it()
    {
        var factory = _fixture.Factory;
        using var client = await CreateAuthenticatedClientAsync(factory);
        var projectId = await SetupProjectAsync(client);
        var viewId = Guid.NewGuid();

        await client.PostAsJsonAsync($"/api/projects/{projectId}/views",
            new { id = viewId, name = "To Delete" });

        var deleteResponse = await client.DeleteAsync($"/api/projects/{projectId}/views/{viewId}");
        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);

        var getResponse = await client.GetAsync($"/api/projects/{projectId}/views/{viewId}");
        Assert.Equal(HttpStatusCode.NotFound, getResponse.StatusCode);
    }

    [Fact]
    public async Task Unauthenticated_request_returns_401()
    {
        var factory = _fixture.Factory;
        using var client = CreateClient(factory);
        var response = await client.GetAsync($"/api/projects/{Guid.NewGuid()}/views");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Delete_view_cascades_to_layout_positions_and_edges()
    {
        var factory = _fixture.Factory;
        using var client = await CreateAuthenticatedClientAsync(factory);
        var projectId = await SetupProjectAsync(client);
        var viewId = Guid.NewGuid();
        var elementId = Guid.NewGuid();
        var relationshipId = Guid.NewGuid();

        await client.PostAsJsonAsync($"/api/projects/{projectId}/views",
            new { id = viewId, name = "Cascade Test" });

        await client.PutAsJsonAsync($"/api/projects/{projectId}/views/{viewId}/layout",
            new
            {
                positions = new[] { new { elementId, x = 10.0, y = 20.0 } },
                edges = new[] { new { relationshipId, routePoints = (object?)null } }
            });

        var deleteResponse = await client.DeleteAsync($"/api/projects/{projectId}/views/{viewId}");
        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var orphanedPositions = await db.LayoutPositions.Where(p => p.ViewId == viewId).CountAsync();
        var orphanedEdges = await db.LayoutEdges.Where(e => e.ViewId == viewId).CountAsync();
        Assert.Equal(0, orphanedPositions);
        Assert.Equal(0, orphanedEdges);
    }

    [Fact]
    public async Task Edge_sides_round_trip_through_the_layout_and_survive_a_save_of_positions()
    {
        using var client = await CreateAuthenticatedClientAsync(_fixture.Factory);
        var projectId = await SetupProjectAsync(client);
        var viewId = Guid.NewGuid();
        var relationshipId = Guid.NewGuid();
        await client.PostAsJsonAsync($"/api/projects/{projectId}/views", new { id = viewId, name = "Sides" });
        var view = $"/api/projects/{projectId}/views/{viewId}";

        var saved = await client.PutAsJsonAsync($"{view}/layout/edges/{relationshipId}", new { sourceSide = "top", targetSide = (string?)null });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);

        // The canvas saves positions with no edges: the sides stay.
        await client.PutAsJsonAsync($"{view}/layout", new { positions = new[] { new { elementId = Guid.NewGuid(), x = 1.0, y = 2.0 } } });
        var edge = Assert.Single((await client.GetFromJsonAsync<ViewDetailDto>(view))!.Edges);
        Assert.Equal((relationshipId, "top", (string?)null), (edge.RelationshipId, edge.SourceSide, edge.TargetSide));

        var refused = await client.PutAsJsonAsync($"{view}/layout/edges/{relationshipId}", new { sourceSide = "middle", targetSide = "left" });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);

        // Both ends auto: nothing is left to keep.
        await client.PutAsJsonAsync($"{view}/layout/edges/{relationshipId}", new { sourceSide = (string?)null, targetSide = (string?)null });
        Assert.Empty((await client.GetFromJsonAsync<ViewDetailDto>(view))!.Edges);

        // A whole layout with edges keeps their sides too.
        await client.PutAsJsonAsync($"{view}/layout", new
        {
            positions = Array.Empty<object>(),
            edges = new[] { new { relationshipId, routePoints = (object?)null, sourceSide = "bottom", targetSide = "right" } },
        });
        edge = Assert.Single((await client.GetFromJsonAsync<ViewDetailDto>(view))!.Edges);
        Assert.Equal(("bottom", "right"), (edge.SourceSide, edge.TargetSide));
    }

    [Fact]
    public async Task Line_visibility_round_trips_with_the_view_and_hiding_nothing_clears_it()
    {
        using var client = await CreateAuthenticatedClientAsync(_fixture.Factory);
        var projectId = await SetupProjectAsync(client);
        var viewId = Guid.NewGuid();
        await client.PostAsJsonAsync($"/api/projects/{projectId}/views", new { id = viewId, name = "Busy" });
        var view = $"/api/projects/{projectId}/views/{viewId}";

        var fresh = (await client.GetFromJsonAsync<ViewDetailDto>(view))!.LineVisibility;
        Assert.True(fresh.DerivedLines && fresh.Structural && fresh.Behavioural && fresh.CrossLevel);
        Assert.Empty(fresh.HiddenRelationshipIds);
        Assert.Empty(fresh.HiddenDerivedLines);

        var (relationship, from, to) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var saved = await client.PutAsJsonAsync($"{view}/layout/visibility", new
        {
            derivedLines = false, structural = true, behavioural = false, crossLevel = false,
            hiddenRelationshipIds = new[] { relationship, relationship },
            hiddenDerivedLines = new[] { new { fromId = from, toId = to } },
        });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);

        // A save of the layout keeps the visibility.
        await client.PutAsJsonAsync($"{view}/layout", new { positions = new[] { new { elementId = from, x = 1.0, y = 2.0 } } });
        var read = (await client.GetFromJsonAsync<ViewDetailDto>(view))!.LineVisibility;
        Assert.Equal((false, true, false, false), (read.DerivedLines, read.Structural, read.Behavioural, read.CrossLevel));
        Assert.Equal([relationship], read.HiddenRelationshipIds);
        Assert.Equal([new HiddenDerivedLineDto(from, to)], read.HiddenDerivedLines);

        var refused = await client.PutAsJsonAsync($"{view}/layout/visibility", new
        {
            derivedLines = true, structural = true, behavioural = true, crossLevel = true,
            hiddenRelationshipIds = Array.Empty<Guid>(),
            hiddenDerivedLines = new[] { new { fromId = from, toId = from } },
        });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);

        await client.PutAsJsonAsync($"{view}/layout/visibility", new
        {
            derivedLines = true, structural = true, behavioural = true, crossLevel = true,
            hiddenRelationshipIds = Array.Empty<Guid>(), hiddenDerivedLines = Array.Empty<object>(),
        });
        using (var scope = _fixture.Factory.Services.CreateScope())
            Assert.False(await scope.ServiceProvider.GetRequiredService<AppDbContext>().LineVisibilities.AnyAsync(l => l.ViewId == viewId));
    }
}
