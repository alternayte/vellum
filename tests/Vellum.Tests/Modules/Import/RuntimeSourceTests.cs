using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vellum.Modules.Import.Runtime;
using Vellum.Tests.Shared.Outbound;

namespace Vellum.Tests.Modules.Import;

/// <summary>
/// Runtime Sources: trace exports in three formats give the same measured edges and keep no
/// span data; malformed and oversized files are refused; New Relic is read only at its fixed
/// endpoint, mapped with the labels the spec names, and its failures are reported like GitHub's.
/// </summary>
public class RuntimeSourceTests(ImportHost import) : IClassFixture<ImportHost>
{
    private const string NewRelicHost = "api.newrelic.com";
    private const string ApiKey = "test-newrelic-user-key-0001";

    /// <summary>What the fixtures hold, in every format: four edges among five services.</summary>
    private static readonly string[] ExpectedEdges =
        ["call:frontend->orders", "call:orders->inventory", "call:orders->payments", "messaging:orders->shipping"];

    /// <summary>Text the fixtures hold in span attributes, events, status messages and resource attributes. None of it may be kept.</summary>
    private static readonly string[] Secrets = ["alice@example.com", "hunter2", "sk-live-abcdef", "secret-host-xyz", "user.email", "http.request.header"];

    [Theory]
    [InlineData("otlp.json", "otlp")]
    [InlineData("jaeger.json", "jaeger")]
    [InlineData("tempo.json", "tempo")]
    public async Task Each_trace_format_yields_the_expected_edges(string file, string format)
    {
        await using var body = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Runtime", file));
        var graph = await TraceExport.ReadAsync(body, file, TraceLimits.Default, CancellationToken.None);

        Assert.Equal(format, graph.Header["exportFormat"]);
        Assert.Equal(ExpectedEdges, graph.Edges.Select(e => e.Key).Order(StringComparer.Ordinal));
        Assert.Equal(["frontend", "inventory", "orders", "payments", "shipping"], graph.Services.Select(s => s.Name).Order(StringComparer.Ordinal));
        Assert.Equal("peer", graph.Services.Single(s => s.Name == "inventory").Attributes["seenAs"]);
        Assert.All(graph.Edges, e => Assert.Equal("measured", e.TruthLabel));
        Assert.All(graph.Services, s => Assert.Equal("measured", s.TruthLabel));

        var charge = graph.Edges.Single(e => e.Key == "call:orders->payments");
        Assert.Equal(("1", "1", """["POST /charge"]"""), (charge.Attributes["sample.calls"], charge.Attributes["sample.errors"], charge.Attributes["operations"]));
        Assert.Equal("sends messages to", graph.Edges.Single(e => e.Key == "messaging:orders->shipping").Label);
        Assert.Equal("2025-10-09T08:53:20.000Z/2025-10-09T08:53:20.070Z", graph.Header["window"]);
        Assert.Equal("7", graph.Header["spans"]);

        // One topology is one Snapshot, whatever format it came in.
        await using var otlp = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Runtime", "otlp.json"));
        var reference = await TraceExport.ReadAsync(otlp, "otlp.json", TraceLimits.Default, CancellationToken.None);
        Assert.Equal(reference.SnapshotId(), graph.SnapshotId());

        var document = Encoding.UTF8.GetString(graph.Write());
        Assert.All(Secrets, secret => Assert.DoesNotContain(secret, document, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task A_trace_upload_proposes_measured_edges_and_no_span_data_reaches_any_stored_row()
    {
        var s = await ImportScenario.CreateAsync(import, fixture: null);
        var sourceId = await CreateTracesSourceAsync(s, "Checkout traces");
        var upload = await UploadAsync(s, sourceId, "otlp.json", Read("otlp.json"));
        Assert.Equal(HttpStatusCode.Accepted, upload.StatusCode);
        var uploaded = (await upload.Content.ReadFromJsonAsync<JsonObject>())!;
        await s.DrainAsync();

        var scan = await s.GetAsync(uploaded.Id("scanId"));
        Assert.Equal(("completed", true), (scan.Text("status"), scan["isComplete"]!.GetValue<bool>()));
        Assert.Equal(uploaded.Text("snapshotId"), scan.Text("snapshotId"));
        Assert.Equal(uploaded.Text("snapshotId"), scan.Text("commitSha"));

        var proposals = await s.ProposalsAsync(scan.Id());
        Assert.Equal(4, proposals.Count(p => p.Text("entityKind") == "relationship"));
        Assert.Equal(["checkout-traces", "frontend", "inventory", "orders", "payments", "shipping"],
            proposals.Where(p => p.Text("entityKind") == "element").Select(p => p.Text("name")!).Order(StringComparer.Ordinal));
        // A citation names the Source, the Snapshot, the file and the line of the derived edge.
        Assert.All(proposals.SelectMany(p => p["evidence"]!.AsArray()), e =>
        {
            Assert.StartsWith($"traces/checkout-traces@{uploaded.Text("snapshotId")}:traces/otlp.json:", e!["pointer"]!.GetValue<string>());
            Assert.Equal("", e["url"]!.GetValue<string>());
        });
        var charge = proposals.Single(p => p.Text("factKey") == "call:orders->payments");
        Assert.Equal(("calls", "otlp.json", "7"), (charge["proposed"]!["label"]!.GetValue<string>(),
            charge["proposed"]!["observed"]!["sample.file"]!.GetValue<string>(), charge["proposed"]!["observed"]!["sample.spans"]!.GetValue<string>()));

        var decision = await s.DecideAsync(scan.Id(), accept: proposals.Select(p => p.Id()));
        Assert.Empty(decision["failed"]!.AsArray());

        var relationships = await s.AllAsync(s.Model("relationships"));
        Assert.Equal(4, relationships.Count);
        Assert.All(relationships, r => Assert.Equal("measured", r.Text("factStatus")));
        Assert.All(relationships, r => Assert.StartsWith("traces/checkout-traces@", r["provenance"]!["source"]!.GetValue<string>()));
        Assert.All((await s.ElementsAsync()).Where(e => e.Text("name") != "checkout-traces"), e => Assert.Equal("measured", e.Text("factStatus")));

        // Every table, every event and the decoded content of every blob: no span data.
        await AssertNoRowHoldsAsync(Secrets);
    }

    [Fact]
    public async Task Malformed_deep_empty_and_oversized_files_are_refused_and_nothing_is_stored()
    {
        var s = await ImportScenario.CreateAsync(import, fixture: null);
        var sourceId = await CreateTracesSourceAsync(s, "Refused traces");

        Assert.Equal((HttpStatusCode.BadRequest, "trace_malformed"), await RefusalAsync(s, sourceId, """{"resourceSpans": [ {"scopeSpans": """));
        Assert.Equal((HttpStatusCode.BadRequest, "trace_malformed"), await RefusalAsync(s, sourceId, new string('[', 200) + new string(']', 200)));
        Assert.Equal((HttpStatusCode.BadRequest, "trace_format_unknown"), await RefusalAsync(s, sourceId, """{"hello": "world"}"""));
        Assert.Equal((HttpStatusCode.BadRequest, "trace_empty"), await RefusalAsync(s, sourceId, """{"resourceSpans": []}"""));
        Assert.Equal((HttpStatusCode.BadRequest, "trace_malformed"), await RefusalAsync(s, sourceId, """{"data": []} {"data": []}"""));

        // Smaller bounds in an app of their own: a file over a bound is refused whole, never cut.
        using var small = ImportHost.With(import.Fixture, services => services.AddSingleton(new TraceLimits(MaxBytes: 3000, MaxSpans: 3, MaxServices: 100, MaxEdges: 100, MaxTokenBytes: 64 * 1024)));
        var owner = await TestHost.SignInAsync(small.Host.Factory);
        var (_, projectId) = await TestHost.CreateProjectAsync(owner);
        var created = await owner.PostAsJsonAsync($"/api/projects/{projectId}/sources", new { kind = "traces", name = "Small" });
        var smallSource = (await created.Content.ReadFromJsonAsync<JsonObject>())!.Id();
        async Task<(HttpStatusCode, string?)> SmallAsync(string json)
        {
            var response = await owner.PostAsync($"/api/projects/{projectId}/sources/{smallSource}/traces?fileName=big.json",
                new StringContent(json, Encoding.UTF8, "application/json"));
            return (response.StatusCode, (await response.Content.ReadFromJsonAsync<JsonObject>())?.Text("type"));
        }
        Assert.Equal((HttpStatusCode.RequestEntityTooLarge, "trace_too_large"), await SmallAsync(Read("otlp.json")));
        var fourSpans = """{"resourceSpans":[{"resource":{"attributes":[{"key":"service.name","value":{"stringValue":"a"}}]},"scopeSpans":[{"spans":[""" +
            string.Join(",", Enumerable.Range(1, 4).Select(i => $$"""{"traceId":"t","spanId":"s{{i}}","kind":1}""")) + "]}]}]}";
        Assert.Equal((HttpStatusCode.RequestEntityTooLarge, "trace_too_many_spans"), await SmallAsync(fourSpans));

        using var scope = import.Host.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Vellum.Shared.AppDbContext>();
        Assert.False(await db.Scans.AnyAsync(x => x.SourceId == sourceId));
        Assert.False(await db.Scans.AnyAsync(x => x.SourceId == smallSource));
    }

    [Fact]
    public async Task New_Relic_reads_only_the_fixed_endpoint_pages_through_cursors_and_labels_entities_measured_and_relationships_declared()
    {
        var s = await ImportScenario.CreateAsync(import, fixture: null);
        var nerdGraph = NewRelicAnswers();
        import.Host.Transport.Forget(NewRelicHost);
        import.Host.Transport.For(NewRelicHost).Then(nerdGraph);
        var before = import.Host.Transport.Asked.Count;

        var source = await CreateNewRelicSourceAsync(s);
        var connection = source["connection"]!;
        Assert.Equal((1234567L, "us", true), (connection["accountId"]!.GetValue<long>(), connection["region"]!.GetValue<string>(), connection["keySet"]!.GetValue<bool>()));
        Assert.DoesNotContain(ApiKey, source.ToJsonString(), StringComparison.Ordinal);

        var scan = await ScanSourceAsync(s, source.Id());
        Assert.True(scan.Text("status") == "completed" && scan["isComplete"]!.GetValue<bool>(), scan.ToJsonString());

        // Two search pages, one batch of related entities, one further page of one entity.
        var asked = import.Host.Transport.Asked.Skip(before).ToList();
        Assert.Equal(4, asked.Count);
        Assert.All(asked, uri => Assert.Equal("https://api.newrelic.com/graphql", uri.ToString()));
        var bodies = import.Host.Transport.For(NewRelicHost).Requests.Select(r => r.Body).ToList();
        Assert.All(bodies, body => Assert.DoesNotContain("mutation", body, StringComparison.OrdinalIgnoreCase));
        Assert.All(import.Host.Transport.For(NewRelicHost).Requests, r => Assert.Equal(ApiKey, r.Header("API-Key")));

        var proposals = await s.ProposalsAsync(scan.Id());
        var elements = proposals.Where(p => p.Text("entityKind") == "element").ToList();
        Assert.Equal(["Orders.Api", "account-1234567", "checkout-web", "payments", "stripe.com"], elements.Select(p => p.Text("name")!).Order(StringComparer.Ordinal));
        Assert.Equal("MTIzNDU2N3xBUE18QVBQTElDQVRJT058MQ", elements.Single(p => p.Text("name") == "Orders.Api").Text("factKey"));
        // The relationship to a Kafka topic, which is no entity the Source reads, is not an edge.
        var relationships = proposals.Where(p => p.Text("entityKind") == "relationship").ToList();
        Assert.Equal(["calls", "calls", "calls", "consumes from"], relationships.Select(p => p["proposed"]!["label"]!.GetValue<string>()).Order(StringComparer.Ordinal));

        var decision = await s.DecideAsync(scan.Id(), accept: proposals.Select(p => p.Id()));
        Assert.Empty(decision["failed"]!.AsArray());
        Assert.All(await s.AllAsync(s.Model("relationships")), r => Assert.Equal("declared", r.Text("factStatus")));
        Assert.All((await s.ElementsAsync()).Where(e => e.Text("kind") == "app"), e => Assert.Equal("measured", e.Text("factStatus")));
        var bindings = await s.Owner.GetFromJsonAsync<List<JsonObject>>($"/api/projects/{s.ProjectId}/sources/{source.Id()}/bindings");
        Assert.Contains(bindings!, b => b.Text("externalKey") == "MTIzNDU2N3xBUE18QVBQTElDQVRJT058MQ" && b.Text("elementName") == "Orders.Api");

        // A rescan in which an entity and its relationships are gone marks them stale, as a code scan does.
        import.Host.Transport.Forget(NewRelicHost);
        import.Host.Transport.For(NewRelicHost).Then(NewRelicAnswers(withoutPayments: true));
        var rescan = await ScanSourceAsync(s, source.Id());
        Assert.Equal("completed", rescan.Text("status"));
        var removals = (await s.ProposalsAsync(rescan.Id())).Where(p => p.Text("action") == "remove").Select(p => p.Text("name")).ToList();
        Assert.Contains("payments", removals);
        var payments = (await s.ElementsAsync()).Single(e => e.Text("name") == "payments");
        Assert.NotNull(payments["stale"]);
    }

    [Fact]
    public async Task New_Relic_auth_failure_rate_limit_partial_answer_and_no_network_are_reported_and_never_called_complete()
    {
        var s = await ImportScenario.CreateAsync(import, fixture: null);
        import.Host.Transport.Forget(NewRelicHost);
        var source = await CreateNewRelicSourceAsync(s);

        import.Host.Transport.For(NewRelicHost).Json(HttpStatusCode.Unauthorized, Read("newrelic/unauthorized.json"));
        var unauthorized = await ScanSourceAsync(s, source.Id());
        Assert.Equal(("failed", "newrelic_unauthorized"), (unauthorized.Text("status"), unauthorized["error"]!.AsObject().Text("code")));

        import.Host.Transport.Forget(NewRelicHost);
        import.Host.Transport.For(NewRelicHost).Json(HttpStatusCode.OK, Read("newrelic/unauthorized.json"));
        Assert.Equal("newrelic_unauthorized", (await ScanSourceAsync(s, source.Id()))["error"]!.AsObject().Text("code"));

        import.Host.Transport.Forget(NewRelicHost);
        import.Host.Transport.For(NewRelicHost).Json(HttpStatusCode.TooManyRequests, Read("newrelic/rate-limited.json"), ("Retry-After", "120"));
        var limited = await ScanSourceAsync(s, source.Id());
        Assert.Equal("waiting", limited.Text("status"));
        Assert.NotNull(limited["retryAt"]);
        await s.Owner.PostAsync(s.Url($"/{limited.Id()}/cancel"), null);

        import.Host.Transport.Forget(NewRelicHost);
        import.Host.Transport.For(NewRelicHost).Json(HttpStatusCode.OK, Read("newrelic/rate-limited.json"));
        Assert.Equal("waiting", (await ScanSourceAsync(s, source.Id())).Text("status"));
        var waiting = (await s.AllAsync(s.Url())).First(x => x.Text("status") == "waiting");
        await s.Owner.PostAsync(s.Url($"/{waiting.Id()}/cancel"), null);

        // Data and errors in one answer: the scan completes, partial, and concludes nothing from an absence.
        import.Host.Transport.Forget(NewRelicHost);
        import.Host.Transport.For(NewRelicHost).Then(sent => Task.FromResult(Json(sent.Body.Contains("entitySearch", StringComparison.Ordinal)
            ? Read("newrelic/partial.json") : """{"data":{"actor":{"entities":[]}}}""")));
        var partial = await ScanSourceAsync(s, source.Id());
        Assert.Equal(("completed", false), (partial.Text("status"), partial["isComplete"]!.GetValue<bool>()));
        Assert.Contains(partial["notes"]!.AsArray(), n => n!["code"]!.GetValue<string>() == "entity-search-errors");
        Assert.DoesNotContain(await s.ProposalsAsync(partial.Id()), p => p.Text("action") == "remove");

        // No network: the Source is unavailable and the rest of the project works.
        import.Host.Transport.Forget(NewRelicHost);
        var offline = await ScanSourceAsync(s, source.Id());
        Assert.Equal(("failed", "newrelic_unavailable", true), (offline.Text("status"), offline["error"]!.AsObject().Text("code"), offline["canResume"]!.GetValue<bool>()));
        Assert.Equal(HttpStatusCode.OK, (await s.Owner.GetAsync(s.Model("elements"))).StatusCode);
        var test = await s.Owner.PostAsync($"/api/projects/{s.ProjectId}/sources/{source.Id()}/newrelic/test", null);
        var tested = (await test.Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.Equal((false, "network"), (tested["ok"]!.GetValue<bool>(), tested["error"]!.AsObject().Text("code")));
    }

    [Fact]
    public async Task A_New_Relic_entity_with_the_name_of_a_code_derived_service_asks_same_as_and_does_not_add_a_second_element()
    {
        var s = await ImportScenario.CreateAsync(import);
        await s.ScanAndAcceptAllAsync();
        var codeElements = await s.ElementsAsync();
        Assert.True(codeElements.Count(e => e.Text("name") == "Orders.Api") == 1, string.Join(", ", codeElements.Select(e => e.Text("name"))));
        var codeElement = codeElements.Single(e => e.Text("name") == "Orders.Api");

        import.Host.Transport.Forget(NewRelicHost);
        import.Host.Transport.For(NewRelicHost).Then(NewRelicAnswers());
        var source = await CreateNewRelicSourceAsync(s);
        var scan = await ScanSourceAsync(s, source.Id());

        var proposals = await s.ProposalsAsync(scan.Id());
        var question = proposals.Single(p => p.Text("action") == "bind");
        Assert.Equal((codeElement.Id(), "MTIzNDU2N3xBUE18QVBQTElDQVRJT058MQ"), (question.Id("entityId"), question.Text("factKey")));
        Assert.DoesNotContain(proposals, p => p.Text("action") == "add" && p.Text("name") == "Orders.Api");

        var response = await s.Owner.PostAsJsonAsync(s.Url($"/{scan.Id()}/proposals/decide"), new { bind = new[] { new { id = question.Id(), same = true } } });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var element = (await s.ElementsAsync()).Where(e => e.Text("name") == "Orders.Api").ToList();
        Assert.Single(element);
        var bindings = await s.Owner.GetFromJsonAsync<List<JsonObject>>($"/api/projects/{s.ProjectId}/sources/{source.Id()}/bindings");
        Assert.Contains(bindings!, b => b.Id("elementId") == codeElement.Id());
    }

    [Fact]
    public async Task Only_an_Owner_stores_a_New_Relic_key_and_a_bad_key_never_leaves_the_instance()
    {
        var s = await ImportScenario.CreateAsync(import, fixture: null);
        var bad = await s.Owner.PostAsJsonAsync($"/api/projects/{s.ProjectId}/sources",
            new { kind = "newrelic", accountId = 1234567, region = "us", apiKey = "line\nbreak" });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        var region = await s.Owner.PostAsJsonAsync($"/api/projects/{s.ProjectId}/sources",
            new { kind = "newrelic", accountId = 1234567, region = "https://evil.example", apiKey = ApiKey });
        Assert.Equal(HttpStatusCode.BadRequest, region.StatusCode);

        // A New Relic key bound to its region's endpoint shows in the secrets list, with no value.
        var source = await CreateNewRelicSourceAsync(s);
        var secrets = await s.Owner.GetFromJsonAsync<List<JsonObject>>($"/api/workspaces/{s.WorkspaceId}/secrets");
        var secret = secrets!.Single(x => x.Text("name") == $"newrelic:{source.Id()}:us:apiKey");
        Assert.Equal(("https://api.newrelic.com", true), (secret.Text("boundOrigin"), secret["matchesCurrentUrl"]!.GetValue<bool>()));
        Assert.DoesNotContain(ApiKey, secrets!.Select(x => x.ToJsonString()).Aggregate("", string.Concat), StringComparison.Ordinal);
        await AssertNoRowHoldsAsync([ApiKey]);
    }

    private static string Read(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Runtime", name));

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    /// <summary>NerdGraph, answering by what each query asks: the search pages by cursor, the related entities, and the further page.</summary>
    private static Func<SentRequest, Task<HttpResponseMessage>> NewRelicAnswers(bool withoutPayments = false) => sent =>
    {
        var body = JsonNode.Parse(sent.Body)!;
        var query = body["query"]!.GetValue<string>();
        string answer;
        if (query.Contains("entitySearch", StringComparison.Ordinal))
            answer = body["variables"]?["cursor"] is null ? Read("newrelic/entity-search-1.json") : Read("newrelic/entity-search-2.json");
        else if (query.Contains("entities(guids", StringComparison.Ordinal))
            answer = Read("newrelic/related-entities.json");
        else
            answer = Read("newrelic/related-entities-page-2.json");
        if (withoutPayments)
        {
            // The account no longer has "payments": not in the search, not in any relationship.
            var json = JsonNode.Parse(answer)!;
            foreach (var list in json.AsObject().DescendantArrays())
            {
                foreach (var item in list.ToList())
                {
                    if (item is not null && item.ToJsonString().Contains("MTIzNDU2N3xBUE18QVBQTElDQVRJT058Mg", StringComparison.Ordinal))
                        list.Remove(item);
                }
            }
            answer = json.ToJsonString();
        }
        return Task.FromResult(Json(answer));
    };

    private static async Task<Guid> CreateTracesSourceAsync(ImportScenario s, string name)
    {
        var response = await s.Owner.PostAsJsonAsync($"/api/projects/{s.ProjectId}/sources", new { kind = "traces", name });
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<JsonObject>())!.Id();
    }

    private static async Task<JsonObject> CreateNewRelicSourceAsync(ImportScenario s)
    {
        var response = await s.Owner.PostAsJsonAsync($"/api/projects/{s.ProjectId}/sources",
            new { kind = "newrelic", accountId = 1234567, region = "us", apiKey = ApiKey });
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<JsonObject>())!;
    }

    private static Task<HttpResponseMessage> UploadAsync(ImportScenario s, Guid sourceId, string fileName, string json) =>
        s.Owner.PostAsync($"/api/projects/{s.ProjectId}/sources/{sourceId}/traces?fileName={Uri.EscapeDataString(fileName)}",
            new StringContent(json, Encoding.UTF8, "application/json"));

    private static async Task<(HttpStatusCode, string?)> RefusalAsync(ImportScenario s, Guid sourceId, string json)
    {
        var response = await UploadAsync(s, sourceId, "bad.json", json);
        return (response.StatusCode, (await response.Content.ReadFromJsonAsync<JsonObject>())?.Text("type"));
    }

    private async Task<JsonObject> ScanSourceAsync(ImportScenario s, Guid sourceId)
    {
        import.Time.Advance(TimeSpan.FromSeconds(1));
        var response = await s.Owner.PostAsJsonAsync($"/api/projects/{s.ProjectId}/sources/{sourceId}/scans", new { });
        Assert.True(response.StatusCode == HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync());
        var id = (await response.Content.ReadFromJsonAsync<JsonObject>())!.Id();
        await s.DrainAsync();
        return await s.GetAsync(id);
    }

    /// <summary>Searches every row of every table, the decoded bytes of every blob included, for each text.</summary>
    private async Task AssertNoRowHoldsAsync(IEnumerable<string> texts)
    {
        using var scope = import.Host.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Vellum.Shared.AppDbContext>();
        var tables = await db.Database.SqlQuery<string>($"""
            SELECT quote_ident(table_schema) || '.' || quote_ident(table_name) AS "Value"
            FROM information_schema.tables
            WHERE table_type = 'BASE TABLE' AND table_schema NOT IN ('pg_catalog', 'information_schema')
            """).ToListAsync();
        Assert.Contains("import.blobs", tables);
        foreach (var text in texts)
        {
            var pattern = "%" + text.ToLowerInvariant() + "%";
            foreach (var table in tables)
            {
#pragma warning disable EF1002 // The table names come from the catalog, not from input.
                var hits = await db.Database.SqlQueryRaw<int>($"SELECT count(*)::int AS \"Value\" FROM {table} t WHERE lower(t::text) LIKE {{0}}", pattern).SingleAsync();
#pragma warning restore EF1002
                Assert.True(hits == 0, $"{table} holds '{text}'.");
            }
            var inBlobs = await db.Database.SqlQuery<int>($"""
                SELECT count(*)::int AS "Value" FROM import.blobs WHERE content IS NOT NULL AND lower(convert_from(content, 'UTF8')) LIKE {pattern}
                """).SingleAsync();
            Assert.True(inBlobs == 0, $"A blob holds '{text}'.");
        }
    }
}

internal static class JsonWalk
{
    /// <summary>Every array in the tree, deepest first, so removing an item never skips another array.</summary>
    public static IEnumerable<JsonArray> DescendantArrays(this JsonNode node)
    {
        var found = new List<JsonArray>();
        void Walk(JsonNode? current)
        {
            switch (current)
            {
                case JsonObject o:
                    foreach (var (_, value) in o.ToList())
                        Walk(value);
                    break;
                case JsonArray a:
                    foreach (var item in a.ToList())
                        Walk(item);
                    found.Add(a);
                    break;
            }
        }
        Walk(node);
        return found;
    }
}
