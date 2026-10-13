using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Vellum.Modules.Import.Scans;

namespace Vellum.Tests.Modules.Import;

/// <summary>
/// The reference journey of SDD §10: scan the Orders repository at a commit, review what the
/// source establishes, accept it, and scan again. Everything goes through the HTTP API, the
/// real worker and the real outbound pipeline; only GitHub is a stub.
/// </summary>
[Collection("Integration")]
public class OrdersImportTests(ImportHost import) : IClassFixture<ImportHost>
{
    private static readonly string[] MappedFactKeys =
    [
        "channel:billing.order-created", "channel:inventory.order-created", "channel:order-events", "channel:order-events.dlx",
        "datastore:postgres", "message:OrderCreated",
        "operation:inventory-worker:GET /healthz", "operation:orders-api:GET /healthz",
        "operation:orders-api:GET /orders/{id}", "operation:orders-api:POST /orders",
        "schema:asyncapi.yaml#/components/schemas/OrderCreated", "schema:openapi.yaml#/components/schemas/CreateOrder",
        "schema:openapi.yaml#/components/schemas/Order", "schema:openapi.yaml#/components/schemas/OrderLine",
        "service:billing", "service:inventory-worker", "service:orders-api",
        "subscription:billing:OrderCreatedConsumer:OrderCreated", "subscription:inventory-worker:amqp:inventory.order-created",
    ];

    [Fact]
    public void The_worker_loop_is_off_in_test_hosts()
    {
        // appsettings.Testing.json turns it off for every test host; this host also replaces the options.
        using var plain = TestHost.Create(import.Fixture);
        Assert.False(plain.Services.GetRequiredService<ScanWorkerOptions>().Enabled);
    }

    [Fact]
    public async Task A_scan_proposes_what_the_source_establishes_and_only_a_decision_writes_it()
    {
        var s = await ImportScenario.CreateAsync(import);
        var expected = JsonNode.Parse(File.ReadAllText(Path.Combine(Repo.Root("orders"), "..", "orders.expected-facts.json")))!["facts"]!.AsArray();
        var repository = $"{s.GitHubHost}/acme/orders";

        // 1. The scan.
        var scan = await s.ScanAsync("https://" + s.GitHubHost + "/Acme/Orders.git");
        Assert.Equal("completed", scan.Text("status"));
        Assert.True(scan["isComplete"]!.GetValue<bool>());
        Assert.Equal("acme/orders", scan.Text("repository"));
        Assert.Equal(ImportScenario.Commit1, scan.Text("commitSha"));
        Assert.Null(scan["error"]);
        Assert.Equal(s.GitHub.BlobRequests.Count, scan["progress"]!["filesRead"]!.GetValue<int>());
        Assert.Equal(s.GitHub.Files(ImportScenario.Commit1).Count, scan["progress"]!["filesListed"]!.GetValue<int>());
        Assert.DoesNotContain("README.md", s.GitHub.BlobRequests);
        Assert.Contains(scan["ruleVersions"]!.AsArray(), r => r!["rule"]!.GetValue<string>() == "dotnet.minimal-api-route" && r["version"]!.GetValue<int>() == 1);

        // 2. Every fact of the committed expectation is a proposal, part of one, or evidence.
        var proposals = await s.ProposalsAsync(scan.Id());
        var evidence = await s.EvidenceAsync(scan.Id());
        Assert.Equal(MappedFactKeys.Append($"repository:{repository}").Order(StringComparer.Ordinal), proposals.Values("factKey"));
        Assert.All(proposals, p => Assert.Equal(("add", "pending", false), (p.Text("action"), p.Text("status"), p["partial"]!.GetValue<bool>())));
        Assert.Equal(52, expected.Count);
        Assert.Equal(expected.Count, MappedFactKeys.Length + evidence.Count + 1); // the one publication that gives the message its producer
        Assert.Equal("pattern-has-no-model-home", evidence.Single("factKey", "outbox:orders-api:masstransit-entity-framework").Text("reason"));
        Assert.Single(await s.EvidenceAsync(scan.Id(), "factKey=" + Uri.EscapeDataString("outbox:orders-api:masstransit-entity-framework")));
        Assert.Equal(11, evidence.Count(e => e.Text("kind") == "Deployment"));
        Assert.Equal(11, evidence.Count(e => e.Text("kind") == "Dependency"));
        Assert.Equal(20, scan["proposalCounts"]!["pending"]!.GetValue<int>());

        // The hero items, with their exact lines.
        var postOrders = proposals.Single("factKey", "operation:orders-api:POST /orders");
        var ordersApi = proposals.Single("factKey", "service:orders-api");
        Assert.Equal(("operation", "POST /orders"), (postOrders.Text("entityKind"), postOrders.Text("name")));
        Assert.Equal(("http", ordersApi.Text("entityId"), "Orders.Api"),
            (postOrders["proposed"]!["transport"]!.GetValue<string>(), postOrders["proposed"]!["ownerId"]!.GetValue<string>(), postOrders["proposed"]!["ownerName"]!.GetValue<string>()));
        Assert.Contains(ordersApi.Id(), postOrders["dependsOn"]!.AsArray().Select(d => d!.GetValue<Guid>()));
        var cited = Assert.Single(postOrders["evidence"]!.AsArray())!.AsObject();
        Assert.Equal(("orders-api/Program.cs", 40, 50, "dotnet.minimal-api-route"),
            (cited.Text("path"), cited["startLine"]!.GetValue<int>(), cited["endLine"]!.GetValue<int>(), cited.Text("rule")));
        Assert.Equal($"{repository}@{ImportScenario.Commit1}:orders-api/Program.cs:40-50", cited.Text("pointer"));
        Assert.Equal($"https://{repository}/blob/{ImportScenario.Commit1}/orders-api/Program.cs#L40-L50", cited.Text("url"));

        var message = proposals.Single("factKey", "message:OrderCreated");
        var billing = proposals.Single("factKey", "service:billing");
        Assert.Equal(ordersApi.Text("entityId"), message["proposed"]!["producerId"]!.GetValue<string>());
        Assert.Equal([billing.Text("entityId")], message["proposed"]!["consumerIds"]!.AsArray().Select(c => c!.GetValue<string>()));
        Assert.Equal(proposals.Single("factKey", "schema:asyncapi.yaml#/components/schemas/OrderCreated").Text("entityId"),
            message["proposed"]!["schemaId"]!.GetValue<string>());
        Assert.Contains("Total:decimal", message["proposed"]!["observed"]!["fields"]!.GetValue<string>());
        Assert.Equal(4, proposals.Count(p => p.Text("entityKind") == "channel"));
        Assert.Equal("order-events", proposals.Single("factKey", "channel:order-events").Text("name"));
        Assert.Equal(2, proposals.Count(p => p.Text("entityKind") == "subscription"));

        // 3. Nothing reached the model: a scan accepts nothing by itself.
        Assert.Empty(await s.ElementsAsync());
        Assert.Empty(await s.AllAsync(s.Model("operations")));
        Assert.Empty(await s.AllAsync(s.Model("schemas")));

        // 4. One decision for everything, named in reverse order: the order is resolved for the caller.
        var decision = await s.DecideAsync(scan.Id(), accept: proposals.Select(p => p.Id()).Reverse());
        Assert.Empty(decision["failed"]!.AsArray());
        Assert.Equal(20, decision["accepted"]!.AsArray().Count);
        var me = (await s.Owner.GetFromJsonAsync<JsonObject>("/api/auth/me"))!.Text("id");

        var elements = await s.ElementsAsync();
        Assert.Equal(["Billing", "Orders.Api", "inventory-worker", "orders", "postgres"], elements.Values("name"));
        Assert.All(elements, e =>
        {
            Assert.Equal("observed-in-source", e.Text("factStatus"));
            Assert.StartsWith($"{repository}@{ImportScenario.Commit1}:", e["provenance"]!["source"]!.GetValue<string>());
            Assert.Equal(ImportScenario.Commit1, e["provenance"]!["revision"]!.GetValue<string>());
            Assert.Equal(me, e["provenance"]!["acceptedBy"]!.GetValue<string>());
            Assert.Null(e["stale"]);
        });
        var system = elements.Single("name", "orders");
        var api = elements.Single("name", "Orders.Api");
        Assert.Equal(("system", "app", "store"), (system.Text("kind"), api.Text("kind"), elements.Single("name", "postgres").Text("kind")));
        Assert.Equal((system.Text("id"), "dotnet"), (api.Text("parentId"), api.Text("technology")));

        var operations = await s.AllAsync(s.Model("operations"));
        Assert.Equal(4, operations.Count);
        var operation = operations.Single(o => o.Text("name") == "POST /orders");
        Assert.Equal((api.Text("id"), "http", "observed-in-source", $"{repository}@{ImportScenario.Commit1}:orders-api/Program.cs:40-50"),
            (operation.Text("ownerId"), operation.Text("transport"), operation.Text("factStatus"), operation["provenance"]!["source"]!.GetValue<string>()));

        var channels = await s.AllAsync(s.Model("channels"));
        Assert.Equal(["billing.order-created", "inventory.order-created", "order-events", "order-events.dlx"], channels.Values("name"));
        Assert.Equal("exchange", channels.Single("name", "order-events").Text("kind"));
        var subscriptions = await s.AllAsync(s.Model("subscriptions"));
        Assert.Equal(2, subscriptions.Count);
        var inventory = subscriptions.Single("consumerId", elements.Single("name", "inventory-worker").Text("id")!);
        Assert.Equal((channels.Single("name", "inventory.order-created").Text("id"), channels.Single("name", "order-events.dlx").Text("id")),
            (inventory.Text("channelId"), inventory.Text("deadLetterChannelId")));

        var stored = Assert.Single(await s.AllAsync(s.Model("messages")));
        var schemas = await s.AllAsync(s.Model("schemas"));
        Assert.Equal(["CreateOrder", "Order", "OrderCreated", "OrderLine"], schemas.Values("name"));
        Assert.Equal(("OrderCreated", api.Text("id"), "observed-in-source"), (stored.Text("name"), stored.Text("producerId"), stored.Text("factStatus")));
        Assert.Equal([elements.Single("name", "Billing").Text("id")], stored["consumerIds"]!.AsArray().Select(c => c!.GetValue<string>()));
        Assert.Contains(schemas, schema => schema.Text("id") == stored.Text("schemaId") && schema.Text("content")!.Contains("\"total\""));

        // 5. The source view returns exactly the cited lines.
        var window = (await s.Owner.GetFromJsonAsync<JsonObject>(s.Url($"/{scan.Id()}/source?path=orders-api/Program.cs&startLine=40&endLine=50")))!;
        var program = System.Text.Encoding.UTF8.GetString(s.GitHub.Files(ImportScenario.Commit1)["orders-api/Program.cs"]).ReplaceLineEndings("\n").Split('\n');
        Assert.Equal(program[39..50], window["lines"]!.AsArray().Select(l => l!.GetValue<string>()));
        Assert.StartsWith("orders.MapPost(\"/\"", program[39]);
        Assert.Equal((ImportScenario.Commit1, 40, 50), (window.Text("commitSha"), window["startLine"]!.GetValue<int>(), window["endLine"]!.GetValue<int>()));
        Assert.Equal(HttpStatusCode.NotFound, (await s.Owner.GetAsync(s.Url($"/{scan.Id()}/source?path=README.md"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Owner.GetAsync(s.Url($"/{scan.Id()}/source?path=orders-api/Program.cs&startLine=1&endLine=500"))).StatusCode);

        // 6. The same commit again: nothing to propose, nothing fetched, nothing changed.
        var before = await s.Owner.GetStringAsync(s.Model("elements"));
        var fetched = s.GitHub.BlobRequests.Count;
        var again = await s.ScanAsync();
        Assert.Equal(("completed", true), (again.Text("status"), again["isComplete"]!.GetValue<bool>()));
        Assert.Empty(await s.ProposalsAsync(again.Id()));
        Assert.Equal(fetched, s.GitHub.BlobRequests.Count);
        Assert.Equal(before, await s.Owner.GetStringAsync(s.Model("elements")));
        Assert.Equal(20, (await s.GetAsync(scan.Id()))["proposalCounts"]!["accepted"]!.GetValue<int>());

        // 7. A commit that removes the outbox and renames a field of the event.
        s.GitHub.Commit(ImportScenario.Commit2, FixtureGitHub.Load("orders-outbox-removed"));
        var changed = await s.ScanAsync();
        Assert.Equal((ImportScenario.Commit2, true), (changed.Text("commitSha"), changed["isComplete"]!.GetValue<bool>()));
        var delta = await s.ProposalsAsync(changed.Id());
        Assert.Equal(["message:OrderCreated", "schema:asyncapi.yaml#/components/schemas/OrderCreated"], delta.Values("factKey"));
        Assert.All(delta, p => Assert.Equal("change", p.Text("action")));
        var fields = Assert.Single(delta.Single("factKey", "message:OrderCreated")["changes"]!.AsArray())!.AsObject();
        Assert.Equal("observed.fields", fields.Text("field"));
        Assert.Contains("Total:decimal", fields.Text("from"));
        Assert.Contains("TotalAmount:decimal", fields.Text("to"));
        Assert.Equal("content", Assert.Single(delta.Single("entityKind", "schema")["changes"]!.AsArray())!["field"]!.GetValue<string>());
        Assert.DoesNotContain(await s.EvidenceAsync(changed.Id()), e => e.Text("factKey")!.StartsWith("outbox:", StringComparison.Ordinal));
        Assert.Contains(await s.EvidenceAsync(scan.Id()), e => e.Text("factKey")!.StartsWith("outbox:", StringComparison.Ordinal));

        Assert.Empty((await s.DecideAsync(changed.Id(), accept: delta.Select(p => p.Id())))["failed"]!.AsArray());
        var after = Assert.Single(await s.AllAsync(s.Model("messages")));
        Assert.Equal(ImportScenario.Commit2, after["provenance"]!["revision"]!.GetValue<string>());
        Assert.Contains("\"totalAmount\"", (await s.AllAsync(s.Model("schemas"))).Single("id", after.Text("schemaId")!).Text("content"));
        Assert.Equal(before, await s.Owner.GetStringAsync(s.Model("elements")));

        // 8. The blobs of a commit that only older scans use are not kept, and the source view says so.
        var newest = await s.ScanAsync();
        Assert.Empty(await s.ProposalsAsync(newest.Id()));
        var gone = await s.Owner.GetAsync(s.Url($"/{scan.Id()}/source?path=orders-api/Program.cs&startLine=40&endLine=50"));
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
        Assert.Equal("source_not_kept", (await gone.Content.ReadFromJsonAsync<JsonObject>())!.Text("type"));
        Assert.Equal(HttpStatusCode.OK, (await s.Owner.GetAsync(s.Url($"/{newest.Id()}/source?path=orders-api/Program.cs&startLine=1&endLine=3"))).StatusCode);

        // No text of a file reached a log, and no scan stopped on an unexpected error.
        Assert.DoesNotContain(import.Host.Logs.Lines, line => line.Contains("Goes to the outbox table", StringComparison.Ordinal));
        Assert.DoesNotContain(import.Host.Logs.Lines, line => line.Contains("stopped on", StringComparison.Ordinal));
    }
}
