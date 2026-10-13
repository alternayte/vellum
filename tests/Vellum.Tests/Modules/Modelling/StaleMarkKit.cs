using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Deedbox;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vellum.Modules.Modelling.Model;
using Vellum.Modules.Workspaces;

namespace Vellum.Tests.Modules.Modelling;

/// <summary>
/// A project with one fact of every kind, and the two things no HTTP endpoint does: a scan's
/// decision appended to a model stream, and a read of the stored state.
/// </summary>
internal sealed class StaleMarkScenario(WebApplicationFactory<Program> factory, HttpClient client, Guid projectId)
{
    public static readonly StaleMark Mark = new("4f2a9c1", StaleDecider.EvidenceNotFound);

    public HttpClient Client => client;
    public Guid ProjectId => projectId;

    public Guid Orders { get; } = Guid.NewGuid();
    public Guid Inventory { get; } = Guid.NewGuid();
    public Guid Reserves { get; } = Guid.NewGuid();
    public Guid OrderPlaced { get; } = Guid.NewGuid();
    public Guid PlaceOrder { get; } = Guid.NewGuid();
    public Guid OrderEvents { get; } = Guid.NewGuid();
    public Guid InventorySub { get; } = Guid.NewGuid();

    /// <summary>One fact of each of the six kinds, in the order element, relationship, message, operation, channel, subscription.</summary>
    public (string Path, Guid Id)[] Facts =>
    [
        ("elements", Orders), ("relationships", Reserves), ("messages", OrderPlaced),
        ("operations", PlaceOrder), ("channels", OrderEvents), ("subscriptions", InventorySub),
    ];

    public static async Task<StaleMarkScenario> CreateAsync(WebApplicationFactory<Program> factory)
    {
        var client = await TestHost.SignInAsync(factory);
        var (_, projectId) = await TestHost.CreateProjectAsync(client);
        var s = new StaleMarkScenario(factory, client, projectId);

        // Root elements: an import reads the elements in document order, and an export orders
        // them by ID, so a child can come before its parent.
        await TestHost.Created(client.PostAsJsonAsync(s.Url("elements"), new { id = s.Orders, kind = "system", name = "Orders" }));
        await TestHost.Created(client.PostAsJsonAsync(s.Url("elements"), new { id = s.Inventory, kind = "system", name = "Inventory" }));
        await TestHost.Created(client.PostAsJsonAsync(s.Url("relationships"),
            new { id = s.Reserves, fromId = s.Orders, toId = s.Inventory, label = "reserves", kind = "behavioural" }));
        await TestHost.Created(client.PostAsJsonAsync(s.Url("messages"),
            new { id = s.OrderPlaced, name = "OrderPlaced", producerId = s.Orders, consumerIds = new[] { s.Inventory } }));
        await TestHost.Created(client.PostAsJsonAsync(s.Url("operations"),
            new { id = s.PlaceOrder, ownerId = s.Orders, name = "POST /orders", transport = "http", timeoutMs = 2000 }));
        await TestHost.Created(client.PostAsJsonAsync(s.Url("channels"), new { id = s.OrderEvents, name = "order-events", kind = "topic" }));
        await TestHost.Created(client.PostAsJsonAsync(s.Url("subscriptions"),
            new { id = s.InventorySub, channelId = s.OrderEvents, consumerId = s.Inventory, retry = new { maxAttempts = 2, backoffMs = 100 } }));
        return s;
    }

    public string Url(string path, Guid? branchId = null) =>
        $"/api/projects/{projectId}/{path}" + (branchId is null ? "" : $"?branchId={branchId}");

    public async Task<Guid> MainStreamAsync()
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<WorkspacesDbContext>().Projects
            .Where(p => p.Id == projectId).Select(p => p.StreamId).FirstAsync();
    }

    /// <summary>What a scan does: appends the decision of <see cref="StaleDecider.Mark"/> to a model stream.</summary>
    public async Task<int> MarkAsync(Guid id, StaleMark? mark, Guid? streamId = null)
    {
        var stream = streamId ?? await MainStreamAsync();
        using var scope = factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IEventStore>().ForProject(projectId);
        var written = 0;
        await store.Execute<ModelState>(StreamId.From(stream), state =>
        {
            var events = StaleDecider.Mark(state, id, mark);
            written = events.Count;
            return events;
        });
        return written;
    }

    public async Task MarkAllAsync(Guid? streamId = null)
    {
        foreach (var (_, id) in Facts)
            Assert.Equal(1, await MarkAsync(id, Mark, streamId));
    }

    public async Task<ModelState> StateAsync(Guid? streamId = null)
    {
        var stream = streamId ?? await MainStreamAsync();
        using var scope = factory.Services.CreateScope();
        var (state, _) = await scope.ServiceProvider.GetRequiredService<IEventStore>().Load<ModelState>(StreamId.From(stream));
        return state;
    }

    /// <summary>The marks of the six facts in the stored state, in the order of <see cref="Facts"/>.</summary>
    public async Task<StaleMark?[]> StateMarksAsync(Guid? streamId = null) => MarksOf(await StateAsync(streamId));

    public StaleMark?[] MarksOf(ModelState state) =>
    [
        state.Elements[Orders].Stale, state.Relationships[Reserves].Stale, state.Messages[OrderPlaced].Stale,
        state.Operations[PlaceOrder].Stale, state.Channels[OrderEvents].Stale, state.Subscriptions[InventorySub].Stale,
    ];

    /// <summary>The marks of the six facts as the API returns them, in the order of <see cref="Facts"/>.</summary>
    public Task<StaleMark?[]> ApiMarksAsync(Guid? branchId = null) => ApiMarksAsync(client, projectId, branchId);

    public async Task<StaleMark?[]> ApiMarksAsync(HttpClient reader, Guid project, Guid? branchId = null)
    {
        var marks = new List<StaleMark?>();
        foreach (var (path, id) in Facts)
            marks.Add(Read(await FactAsync(reader, project, path, id, branchId)));
        return [.. marks];
    }

    public Task<JsonObject> FactAsync(string path, Guid id, Guid? branchId = null) => FactAsync(client, projectId, path, id, branchId);

    private static async Task<JsonObject> FactAsync(HttpClient reader, Guid project, string path, Guid id, Guid? branchId)
    {
        var branch = branchId is null ? "" : $"&branchId={branchId}";
        // Elements, relationships and messages are read-model rows; contracts come from the state.
        var page = (await reader.GetFromJsonAsync<JsonObject>($"/api/projects/{project}/{path}?limit=200{branch}"))!;
        return page["items"]!.AsArray().Select(item => item!.AsObject()).Single(item => (Guid)item["id"]! == id);
    }

    public static StaleMark? Read(JsonObject fact) =>
        fact["stale"] is JsonObject stale ? new((string)stale["sinceRevision"]!, (string)stale["reason"]!) : null;

    public async Task PatchAsync(string path, object body, Guid? branchId = null)
    {
        var response = await client.PatchAsJsonAsync(Url(path, branchId), body);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    public static StaleMark?[] All(StaleMark? mark) => [mark, mark, mark, mark, mark, mark];
}
