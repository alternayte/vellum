using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Deedbox;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vellum.Modules.Modelling.Model;
using Vellum.Modules.Workspaces;
using Vellum.Tests.Shared.Outbound;

namespace Vellum.Tests.Modules.Ai.Design;

/// <summary>
/// The IDs of the Orders reference model. The alias table sorts by name, so the aliases are
/// fixed: E1 Billing, E2 Customer, E3 Inventory, E4 Orders, E5 Orders DB, E6 Shop, O1 POST
/// /orders, C1 order-events, C2 order-events.dlq, S1 Billing on order-events, S2 Inventory on
/// order-events, M1 OrderCreated. The fixture replies in <c>Fixtures/Ai/design</c> use them.
/// </summary>
internal sealed record OrdersModel(
    Guid Customer, Guid Shop, Guid Orders, Guid OrdersDb, Guid Inventory, Guid Billing,
    Guid PlaceOrder, Guid OrderEvents, Guid DeadLetters, Guid InventorySub, Guid BillingSub, Guid OrderCreated)
{
    public IEnumerable<Guid> All =>
        [Customer, Shop, Orders, OrdersDb, Inventory, Billing, PlaceOrder, OrderEvents, DeadLetters, InventorySub, BillingSub, OrderCreated];
}

internal static class DesignKit
{
    public const string OrdersRequest =
        "A customer places an order; Orders saves it and publishes OrderCreated; Inventory and Billing consume it; show what happens if Billing fails.";

    /// <summary>The text a model answers with, from <c>Fixtures/Ai/design</c>.</summary>
    public static string Reply(string name) => Fixtures.Read($"Ai/design/{name}.json");

    public static string Url(Guid projectId, string path) => $"/api/projects/{projectId}/{path}";

    public static async Task<Guid> NewProjectAsync(HttpClient owner, Guid workspaceId)
    {
        var projectId = Guid.NewGuid();
        await TestHost.Created(owner.PostAsJsonAsync($"/api/workspaces/{workspaceId}/projects", new { id = projectId, name = "Orders" }));
        return projectId;
    }

    public static async Task<OrdersModel> SeedOrdersAsync(HttpClient client, Guid projectId)
    {
        async Task Post(string path, object body) => await TestHost.Created(client.PostAsJsonAsync(Url(projectId, path), body));
        static Guid Id() => Guid.NewGuid();
        var m = new OrdersModel(Id(), Id(), Id(), Id(), Id(), Id(), Id(), Id(), Id(), Id(), Id(), Id());

        var orderRequest = Guid.NewGuid();
        var orderCreated = Guid.NewGuid();
        await Post("schemas", new { id = orderRequest, name = "OrderRequest", content = """{"type":"object","required":["sku","qty"],"properties":{"sku":{"type":"string"},"qty":{"type":"integer","minimum":1}}}""" });
        await Post("schemas", new { id = orderCreated, name = "OrderCreated", content = """{"type":"object","required":["orderId"]}""" });

        await Post("elements", new { id = m.Customer, kind = "actor", name = "Customer" });
        await Post("elements", new { id = m.Shop, kind = "system", name = "Shop" });
        await Post("elements", new { id = m.Orders, kind = "app", name = "Orders", parentId = m.Shop });
        await Post("elements", new { id = m.OrdersDb, kind = "store", name = "Orders DB", parentId = m.Shop });
        await Post("elements", new { id = m.Inventory, kind = "app", name = "Inventory", parentId = m.Shop });
        await Post("elements", new { id = m.Billing, kind = "app", name = "Billing", parentId = m.Shop });
        await Post("messages", new { id = m.OrderCreated, name = "OrderCreated", producerId = m.Orders, consumerIds = new[] { m.Inventory, m.Billing }, schemaId = orderCreated });
        await Post("operations", new { id = m.PlaceOrder, ownerId = m.Orders, name = "POST /orders", transport = "http", requestSchemaId = orderRequest, timeoutMs = 2000 });
        await Post("channels", new { id = m.OrderEvents, name = "order-events", kind = "topic" });
        await Post("channels", new { id = m.DeadLetters, name = "order-events.dlq", kind = "queue" });
        await Post("subscriptions", new { id = m.InventorySub, channelId = m.OrderEvents, consumerId = m.Inventory, retry = new { maxAttempts = 2, backoffMs = 100 }, deadLetterChannelId = m.DeadLetters });
        await Post("subscriptions", new { id = m.BillingSub, channelId = m.OrderEvents, consumerId = m.Billing });
        return m;
    }

    /// <summary>A workspace with one provider of <paramref name="kind"/> and a project that holds the Orders model.</summary>
    public static async Task<(AiScenario Ai, Guid ProjectId, OrdersModel Model)> OrdersAsync(
        OutboundHost host, string kind, Action<JsonObject>? provider = null, bool route = true)
    {
        var ai = await AiScenario.CreateAsync(host, kind, provider, route: route);
        var projectId = await NewProjectAsync(ai.Owner, ai.WorkspaceId);
        return (ai, projectId, await SeedOrdersAsync(ai.Owner, projectId));
    }

    public static Task<HttpResponseMessage> ProposeFlowAsync(HttpClient client, Guid projectId, string request, Guid? revise = null) =>
        client.PostAsJsonAsync(Url(projectId, "ai/flow-proposals"), new { request, reviseScenarioId = revise });

    public static Task<HttpResponseMessage> ProposeArchitectureAsync(HttpClient client, Guid projectId, string request) =>
        client.PostAsJsonAsync(Url(projectId, "ai/architecture-proposals"), new { request });

    public static Task<HttpResponseMessage> ExplainAsync(HttpClient client, Guid projectId, string kind, Guid id, string? question = null) =>
        client.PostAsJsonAsync(Url(projectId, "ai/explain"), new { target = new { kind, id }, question });

    public static async Task<JsonObject> BodyAsync(HttpResponseMessage response, HttpStatusCode expected)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode}: {text}");
        return JsonNode.Parse(text)!.AsObject();
    }

    /// <summary>The version of the project's main model stream. An append of any kind raises it.</summary>
    public static async Task<long> ModelVersionAsync(OutboundHost host, Guid projectId)
    {
        using var scope = host.Factory.Services.CreateScope();
        var streamId = await scope.ServiceProvider.GetRequiredService<WorkspacesDbContext>().Projects.AsNoTracking()
            .Where(p => p.Id == projectId).Select(p => p.StreamId).FirstAsync();
        return (await scope.ServiceProvider.GetRequiredService<IEventStore>().Load<ModelState>(StreamId.From(streamId))).Version;
    }

    /// <summary>The text of a request to a provider. The JSON writer escapes quotes, '<' and line breaks; this is what they stand for.</summary>
    public static string Text(SentRequest sent) => System.Text.RegularExpressions.Regex.Unescape(sent.Body);

    /// <summary>The <c>data:</c> lines of a server-sent event body, each parsed as JSON.</summary>
    public static List<JsonObject> Events(string body) =>
        [.. body.Split('\n').Where(l => l.StartsWith("data: ", StringComparison.Ordinal)).Select(l => JsonNode.Parse(l[6..])!.AsObject())];

    public static string[] Codes(JsonObject proposal) => [.. proposal["issues"]!.AsArray().Select(i => (string)i!["code"]!)];

    /// <summary>A flow answer with <paramref name="count"/> top-level write steps.</summary>
    public static string ManySteps(int count) => JsonSerializer.Serialize(new
    {
        name = "Many steps",
        summary = "",
        steps = Enumerable.Range(0, count).Select(i => new
        {
            id = $"s{i}", parentStepId = (string?)null, slot = "root", slotKey = (string?)null, order = i, type = "write",
            from = (string?)null, operation = (string?)null, channel = (string?)null, message = (string?)null,
            by = "E4", store = "E5", dataset = "d", whenStep = (string?)null, @is = (string?)null,
            requestJson = (string?)null, responseJson = (string?)null, payloadJson = (string?)null, changeJson = (string?)null,
        }),
        faults = Array.Empty<object>(),
        assertions = Array.Empty<object>(),
        missing = Array.Empty<object>(),
        openQuestions = Array.Empty<string>(),
        reasoningSummary = "",
        evidence = Array.Empty<object>(),
    });
}
