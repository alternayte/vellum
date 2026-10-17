using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;

namespace Vellum.Tests.Modules.Import;

/// <summary>
/// The relationships a tracking scan derives reach the model like every other observed fact,
/// and follow the rescan rules. An inferred HTTP call is the exception: it waits in "Needs you"
/// until a person accepts or rejects it, and the scan never applies it.
/// </summary>
public class RelationshipImportTests(AutoApplyImportHost import) : IClassFixture<AutoApplyImportHost>
{
    /// <summary>The orders fixture with a <c>payments</c> app in the compose file, and optionally <c>PAYMENTS_URL</c> on orders-api.</summary>
    private static SortedDictionary<string, byte[]> WithPayments(bool url)
    {
        var files = FixtureGitHub.Load("orders");
        files["payments/go.mod"] = Encoding.UTF8.GetBytes("module github.com/acme/orders/payments\n\ngo 1.23\n");
        files["payments/main.go"] = Encoding.UTF8.GetBytes("package main\n\nfunc main() {}\n");
        var compose = Encoding.UTF8.GetString(files["docker-compose.yml"]);
        compose = compose.Replace("\nvolumes:\n", "\n  payments:\n    build: ./payments\n\nvolumes:\n", StringComparison.Ordinal);
        if (url)
        {
            const string anchor = "      - RabbitMq__Host=amqp://guest:guest@rabbitmq:5672\n    ports:\n      - \"8080:8080\"";
            Assert.Contains(anchor, compose);
            compose = compose.Replace(anchor, "      - RabbitMq__Host=amqp://guest:guest@rabbitmq:5672\n      - PAYMENTS_URL=http://payments:8080\n    ports:\n      - \"8080:8080\"", StringComparison.Ordinal);
        }
        files["docker-compose.yml"] = Encoding.UTF8.GetBytes(compose);
        return files;
    }

    private static async Task<List<JsonObject>> NeedsYouAsync(ImportScenario s)
    {
        var page = (await s.Owner.GetFromJsonAsync<JsonObject>(s.Model("needs-you")))!;
        return [.. page["sources"]!.AsArray().SelectMany(source => source!["items"]!.AsArray()).Select(i => i!.AsObject())];
    }

    private static async Task<List<string>> LinesAsync(ImportScenario s)
    {
        var elements = await s.ElementsAsync();
        string Name(JsonObject r, string end) => elements.Single("id", r.Text(end)!).Text("name")!;
        return [.. (await s.AllAsync(s.Model("relationships")))
            .Select(r => $"{Name(r, "fromId")} → {Name(r, "toId")}: {r.Text("label")} ({r.Text("factStatus")})")
            .Order(StringComparer.Ordinal)];
    }

    [Fact]
    public async Task The_first_scan_applies_message_flow_data_access_and_depends_on_relationships()
    {
        var s = await ImportScenario.CreateAsync(import);
        await s.ScanAsync();

        Assert.Equal(
        [
            "Orders.Api → Billing: publishes OrderCreated (observed-in-source)",
            "Orders.Api → inventory-worker: publishes OrderCreated (observed-in-source)",
            "Orders.Api → postgres: depends on (observed-in-source)",
            "Orders.Api → postgres: reads and writes (observed-in-source)",
            "inventory-worker → postgres: depends on (observed-in-source)",
            "inventory-worker → postgres: reads and writes (observed-in-source)",
        ], await LinesAsync(s));
        Assert.Empty(await NeedsYouAsync(s));

        // A rescan of the same commit finds the same relationships under the same keys.
        var again = await s.ScanAsync();
        Assert.Empty(await s.ProposalsAsync(again.Id()));
    }

    [Fact]
    public async Task An_inferred_http_call_waits_in_needs_you_and_only_a_person_adds_it()
    {
        var s = await ImportScenario.CreateAsync(import);
        s.GitHub.Commit(ImportScenario.Commit1, WithPayments(url: false));
        await s.ScanAsync();
        Assert.DoesNotContain(await LinesAsync(s), l => l.Contains("calls", StringComparison.Ordinal));

        s.GitHub.Commit(ImportScenario.Commit2, WithPayments(url: true));
        var rescan = await s.ScanAsync();

        // The suggestion waits, labelled inferred, with its confidence and the line that named the service.
        var suggestion = Assert.Single(await NeedsYouAsync(s));
        Assert.Equal(("add", "relationship", "pending", "inferred"),
            (suggestion.Text("action"), suggestion.Text("entityKind"), suggestion.Text("status"), suggestion.Text("factStatus")));
        Assert.Equal(0.6, suggestion["confidence"]!.GetValue<double>());
        Assert.Equal("Orders.Api → payments: calls", suggestion.Text("name"));
        Assert.Equal(("calls", "PAYMENTS_URL"),
            (suggestion["proposed"]!["label"]!.GetValue<string>(), suggestion["proposed"]!["observed"]!["variables"]!.GetValue<string>()));
        var line = Assert.Single(suggestion["evidence"]!.AsArray(), e => e!["rule"]!.GetValue<string>() == "relationship.http-call")!;
        Assert.Equal(("docker-compose.yml", 26), (line["path"]!.GetValue<string>(), line["startLine"]!.GetValue<int>()));

        // The scan applied nothing of it: the canvas has no such line.
        Assert.DoesNotContain(await LinesAsync(s), l => l.Contains("calls", StringComparison.Ordinal));

        // A person accepts it: the relationship is inferred, with the confidence and the line.
        Assert.Empty((await s.DecideAsync(rescan.Id(), accept: [suggestion.Id()]))["failed"]!.AsArray());
        Assert.Contains("Orders.Api → payments: calls (inferred)", await LinesAsync(s));
        var call = (await s.AllAsync(s.Model("relationships"))).Single("label", "calls");
        Assert.Equal((0.6, $"{s.GitHubHost}/acme/orders@{ImportScenario.Commit2}:docker-compose.yml:26-26"),
            (call["provenance"]!["confidence"]!.GetValue<double>(), call["provenance"]!["source"]!.GetValue<string>()));
        Assert.Empty(await NeedsYouAsync(s));
        Assert.Empty(await s.ProposalsAsync((await s.ScanAsync()).Id()));
    }

    [Fact]
    public async Task A_rejected_suggestion_and_a_relationship_the_model_holds_are_not_proposed()
    {
        var s = await ImportScenario.CreateAsync(import);
        s.GitHub.Commit(ImportScenario.Commit1, WithPayments(url: true));
        var first = await s.ScanAsync();
        var suggestion = Assert.Single(await NeedsYouAsync(s));
        await s.DecideAsync(first.Id(), reject: [suggestion.Id()]);
        Assert.Empty(await s.ProposalsAsync((await s.ScanAsync()).Id()));

        // Another project, where a person drew the same relationship by hand before the scan said it.
        var t = await ImportScenario.CreateAsync(import);
        t.GitHub.Commit(ImportScenario.Commit1, WithPayments(url: false));
        await t.ScanAsync();
        var elements = await t.ElementsAsync();
        var drawn = await t.Owner.PostAsJsonAsync(t.Model("relationships"), new
        {
            id = Guid.NewGuid(), fromId = elements.Single("name", "Orders.Api").Id(), toId = elements.Single("name", "payments").Id(),
            label = " Calls", kind = "behavioural",
        });
        Assert.Equal(HttpStatusCode.Created, drawn.StatusCode);

        t.GitHub.Commit(ImportScenario.Commit2, WithPayments(url: true));
        var rescan = await t.ScanAsync();
        Assert.Empty(await t.ProposalsAsync(rescan.Id()));
        Assert.Empty(await NeedsYouAsync(t));
        var held = Assert.Single(await t.EvidenceAsync(rescan.Id()), e => e.Text("kind") == "Relationship");
        Assert.Equal("relationship-in-model", held.Text("reason"));
        Assert.Single(await t.AllAsync(t.Model("relationships")), r => r.Text("toId") == elements.Single("name", "payments").Text("id"));
    }

    [Fact]
    public async Task Removing_a_consumer_marks_its_relationship_not_seen_and_keeps_it()
    {
        var s = await ImportScenario.CreateAsync(import);
        await s.ScanAsync();

        // Billing stays a service; it no longer consumes OrderCreated.
        var files = FixtureGitHub.Load("orders");
        files.Remove("billing/OrderCreatedConsumer.cs");
        var program = Encoding.UTF8.GetString(files["billing/Program.cs"]);
        files["billing/Program.cs"] = Encoding.UTF8.GetBytes(program
            .Replace("    bus.AddConsumer<OrderCreatedConsumer>();\n", "", StringComparison.Ordinal)
            .Replace("            endpoint.ConfigureConsumer<OrderCreatedConsumer>(context);\n", "", StringComparison.Ordinal));
        s.GitHub.Commit(ImportScenario.Commit2, files);
        await s.ScanAsync();

        var elements = await s.ElementsAsync();
        var billing = elements.Single("name", "Billing");
        Assert.Null(billing["stale"]);
        var relationships = await s.AllAsync(s.Model("relationships"));
        var flow = relationships.Single(r => r.Text("toId") == billing.Text("id"));
        Assert.Equal("publishes OrderCreated", flow.Text("label"));
        Assert.Equal((ImportScenario.Commit2, "evidence-not-found"), (flow["stale"]!.AsObject().Text("sinceRevision"), flow["stale"]!.AsObject().Text("reason")));
        Assert.All(relationships.Where(r => r.Text("id") != flow.Text("id")), r => Assert.Null(r["stale"]));
        Assert.Contains(await NeedsYouAsync(s), i => i.Text("action") == "remove" && i.Text("entityId") == flow.Text("id"));
    }
}
