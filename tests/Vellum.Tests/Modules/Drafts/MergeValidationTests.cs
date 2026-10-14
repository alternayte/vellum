using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Vellum.Modules.Drafts;
using Vellum.Modules.Modelling.Elements;
using Vellum.Shared;

namespace Vellum.Tests.Modules.Drafts;

/// <summary>
/// A merge compares three copies of the model that were each read from stored JSON, and it
/// writes events that no decider produced. These tests go through the real snapshot, the real
/// streams and the real append.
/// </summary>
public class MergeValidationTests(IntegrationFixture fixture) : IClassFixture<IntegrationFixture>
{
    private static string Url(Guid projectId, string path, Guid? branchId = null) =>
        $"/api/projects/{projectId}/{path}" + (branchId is null ? "" : $"?branchId={branchId}");

    private static async Task<DraftDto> CreateDraftAsync(HttpClient client, Guid projectId)
    {
        var response = await TestHost.Created(client.PostAsJsonAsync(Url(projectId, "drafts"), new { id = Guid.NewGuid(), name = $"Draft {Guid.NewGuid():N}" }));
        return (await response.Content.ReadFromJsonAsync<DraftDto>())!;
    }

    private static async Task<JsonObject> PreviewAsync(HttpClient client, Guid projectId, Guid draftId)
    {
        var response = await client.PostAsync(Url(projectId, $"drafts/{draftId}/merge/preview"), null);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<JsonObject>())!;
    }

    [Fact]
    public async Task Unchanged_entities_that_hold_lists_do_not_conflict()
    {
        var factory = fixture.Factory;
        using var client = await TestHost.SignInAsync(factory);
        var (_, projectId) = await TestHost.CreateProjectAsync(client);

        // One entity of every kind. The element and the message hold non-empty lists.
        var orders = Guid.NewGuid();
        var inventory = Guid.NewGuid();
        var message = Guid.NewGuid();
        var channel = Guid.NewGuid();
        await TestHost.Created(client.PostAsJsonAsync(Url(projectId, "elements"),
            new { id = orders, kind = "system", name = "Orders", tags = new[] { "core", "billing" } }));
        await TestHost.Created(client.PostAsJsonAsync(Url(projectId, "elements"),
            new { id = inventory, kind = "system", name = "Inventory", tags = new[] { "core" } }));
        var facts = await client.PatchAsJsonAsync(Url(projectId, $"elements/{inventory}"),
            new { factStatus = "inferred", setProvenance = true, provenance = new { source = "repo@abc:src/Inventory.cs:1-9", revision = "abc", confidence = 0.5 } });
        Assert.Equal(HttpStatusCode.OK, facts.StatusCode);
        await TestHost.Created(client.PostAsJsonAsync(Url(projectId, "relationships"),
            new { id = Guid.NewGuid(), fromId = orders, toId = inventory, label = "reserves", kind = "behavioural" }));
        await TestHost.Created(client.PostAsJsonAsync(Url(projectId, "messages"),
            new { id = message, name = "OrderPlaced", producerId = orders, consumerIds = new[] { inventory }, tags = new[] { "event" } }));
        await TestHost.Created(client.PostAsJsonAsync(Url(projectId, "channels"), new { id = channel, name = "order-events", kind = "topic" }));
        await TestHost.Created(client.PostAsJsonAsync(Url(projectId, "operations"),
            new { id = Guid.NewGuid(), ownerId = orders, name = "POST /orders", transport = "http", timeoutMs = 2000, retry = new { maxAttempts = 3, backoffMs = 250 } }));
        await TestHost.Created(client.PostAsJsonAsync(Url(projectId, "subscriptions"),
            new { id = Guid.NewGuid(), channelId = channel, consumerId = inventory, retry = new { maxAttempts = 2, backoffMs = 100 } }));

        var draft = await CreateDraftAsync(client, projectId);

        // Nothing changed on either side.
        var untouched = await PreviewAsync(client, projectId, draft.Id);
        Assert.Empty(untouched["conflicts"]!.AsArray());
        Assert.Empty(untouched["autoResolved"]!.AsArray());

        // The draft renames the tagged element and removes the message with consumers.
        Assert.Equal(HttpStatusCode.OK, (await client.PatchAsJsonAsync(
            Url(projectId, $"elements/{orders}", draft.StreamId), new { name = "Order Service" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync(Url(projectId, $"messages/{message}", draft.StreamId))).StatusCode);

        var preview = await PreviewAsync(client, projectId, draft.Id);
        Assert.Empty(preview["conflicts"]!.AsArray());
        Assert.Equal(
            new[] { ("element", orders, "modified"), ("message", message, "removed") },
            preview["autoResolved"]!.AsArray()
                .Select(c => ((string)c!["entityType"]!, (Guid)c["entityId"]!, (string)c["changeKind"]!))
                .OrderBy(c => c.Item1)
                .ToArray());

        var merged = await client.PostAsJsonAsync(Url(projectId, $"drafts/{draft.Id}/merge/execute"),
            new { resolutions = Array.Empty<object>(), expectedMainVersion = (int)preview["mainVersion"]! });
        Assert.True(merged.StatusCode == HttpStatusCode.OK, await merged.Content.ReadAsStringAsync());

        var renamed = (await client.GetFromJsonAsync<ElementDto>(Url(projectId, $"elements/{orders}")))!;
        Assert.Equal("Order Service", renamed.Name);
        Assert.Equal(["core", "billing"], renamed.Tags);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(Url(projectId, $"messages/{message}"))).StatusCode);
    }

    [Fact]
    public async Task A_merge_whose_events_do_not_fit_main_is_refused_and_writes_nothing()
    {
        var factory = fixture.Factory;
        using var client = await TestHost.SignInAsync(factory);
        var (_, projectId) = await TestHost.CreateProjectAsync(client);

        var parent = Guid.NewGuid();
        var child = Guid.NewGuid();
        await TestHost.Created(client.PostAsJsonAsync(Url(projectId, "elements"), new { id = parent, kind = "system", name = "Orders" }));
        var draft = await CreateDraftAsync(client, projectId);

        // Main removes the parent. The draft adds a child under it.
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync(Url(projectId, $"elements/{parent}"))).StatusCode);
        await TestHost.Created(client.PostAsJsonAsync(Url(projectId, "elements", draft.StreamId),
            new { id = child, kind = "app", name = "Orders API", parentId = parent }));

        var preview = await PreviewAsync(client, projectId, draft.Id);
        var mainVersion = (int)preview["mainVersion"]!;
        Assert.Contains(preview["conflicts"]!.AsArray(), c => (Guid)c!["entityId"]! == child);

        // "take_ours" still adds the child, under a parent main no longer has.
        // "take_theirs" adds the child twice.
        foreach (var resolution in new[] { "take_ours", "take_theirs" })
        {
            var refused = await client.PostAsJsonAsync(Url(projectId, $"drafts/{draft.Id}/merge/execute"),
                new { resolutions = new[] { new { entityId = child, resolution } }, expectedMainVersion = mainVersion });
            Assert.True(refused.StatusCode == HttpStatusCode.Conflict, $"{resolution}: {(int)refused.StatusCode} {await refused.Content.ReadAsStringAsync()}");
            Assert.Equal("conflict", (await refused.Content.ReadFromJsonAsync<ErrorResponse>())!.Type);
        }

        Assert.Empty((await client.GetFromJsonAsync<Page<ElementDto>>(Url(projectId, "elements")))!.Items);
        Assert.Equal("open", (await client.GetFromJsonAsync<DraftDto>(Url(projectId, $"drafts/{draft.Id}")))!.Status);
        Assert.Equal(mainVersion, (int)(await PreviewAsync(client, projectId, draft.Id))["mainVersion"]!);
    }
}
