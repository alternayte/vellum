using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Vellum.Tests.Modules.Ai;
using Vellum.Tests.Modules.Promises;
using Vellum.Tests.Shared.Outbound;
using static Vellum.Tests.Modules.Ai.Design.DesignKit;

namespace Vellum.Tests.Modules.Workspaces;

/// <summary>
/// A project that touches every module, then its delete. The test reads the whole database, so
/// the class has a database of its own and one test: nothing else writes while it counts.
/// </summary>
public class ProjectDeleteTests(SharedOutboundHost shared) : IClassFixture<SharedOutboundHost>
{
    /// <summary>
    /// Tables without a project column that may grow while the project lives and keep what they
    /// got: the workspace and its members, the accounts, the audit log, and the AI usage
    /// records, which belong to the workspace and hold the spend its budget counts.
    /// </summary>
    private static readonly string[] Kept = ["workspaces.", "identity.", "settings.audit_entries", "ai.usage_records"];

    [Fact]
    public async Task Deleting_a_project_leaves_no_row_in_any_module_and_no_live_event_stream()
    {
        var host = shared.Host;
        var ai = await AiScenario.CreateAsync(host, "anthropic");
        var owner = ai.Owner;
        var before = await CountAsync();

        var projectId = await NewProjectAsync(owner, ai.WorkspaceId);
        string Url(string path) => $"/api/projects/{projectId}/{path}";
        async Task<JsonObject> Post(string path, object? body, HttpStatusCode expected = HttpStatusCode.Created)
        {
            var response = await owner.PostAsJsonAsync(Url(path), body ?? new { });
            var text = await response.Content.ReadAsStringAsync();
            Assert.True(response.StatusCode == expected, $"POST {path}: {(int)response.StatusCode} {text}");
            return JsonNode.Parse(text) as JsonObject ?? [];
        }
        async Task Send(HttpMethod method, string path, object? body = null)
        {
            var response = await owner.SendAsync(new HttpRequestMessage(method, Url(path)) { Content = body is null ? null : JsonContent.Create(body) });
            Assert.True(response.IsSuccessStatusCode, $"{method} {path}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        }

        // Modelling, schemas and a scenario.
        var orders = await OrdersReference.SeedAsync(owner, projectId);
        // A deleted schema and a deleted scenario have no row, but their streams hold their events.
        var goneSchema = Guid.NewGuid();
        await Post("schemas", new { id = goneSchema, name = "Gone", content = """{"type":"object"}""" });
        await Send(HttpMethod.Delete, $"schemas/{goneSchema}");
        var goneScenario = orders.WithoutOutbox();
        goneScenario["id"] = Guid.NewGuid().ToString();
        goneScenario["name"] = "Gone";
        await TestHost.Created(owner.PostAsync(Url("scenarios"), OrdersReference.Json(goneScenario)));
        await Send(HttpMethod.Delete, $"scenarios/{goneScenario["id"]}");

        // A revision, a variant, and a draft with a change and a comment.
        var revision = Guid.NewGuid();
        await Post("revisions", new { id = revision, name = "Baseline" });
        await Post("variants", new { id = Guid.NewGuid(), name = "Variant", baseRevisionId = revision });
        var draft = await Post("drafts", new { id = Guid.NewGuid(), name = "Draft" });
        await TestHost.Created(owner.PostAsJsonAsync(Url($"elements?branchId={draft["streamId"]}"), new { id = Guid.NewGuid(), kind = "system", name = "On the draft" }));
        await Post($"drafts/{draft["id"]}/comments", new { id = Guid.NewGuid(), body = "A comment" });

        // A view with a layout, a space and a doc.
        var view = Guid.NewGuid();
        await Post("views", new { id = view, name = "View", rootElementId = (Guid?)null });
        await Send(HttpMethod.Put, $"views/{view}/layout", new
        {
            positions = new[] { new { elementId = OrdersReference.OrdersApi, x = 1.0, y = 2.0 } },
            edges = Array.Empty<object>(),
        });
        var space = Guid.NewGuid();
        await Post("spaces", new { id = space, name = "Space" });
        await Post("docs", new { id = Guid.NewGuid(), title = "Doc", spaceId = space });

        // A promise, a cost sheet with a saved estimate, review data, a stress run and a load-test target.
        await orders.CreateOrd014Async();
        await Post("costs/resources", new
        {
            elementId = OrdersReference.OrdersApi, name = "API", category = "compute", provider = "custom", region = "local", sku = "small",
            quantity = 1m, unit = "hours", priceRef = (Guid?)null, usage = (object[]?)null,
        });
        await Post("costs/estimates", null);
        await Post("decisions", new
        {
            title = "Relay without schema", context = "Context.", decision = "Decision.", consequences = "Consequences.",
            alternatives = Array.Empty<object>(), affectedIds = new[] { OrdersReference.RelayOutbox }, settles = Array.Empty<object>(),
        });
        await Post("questions", new { text = "Who reads the dead-letter queue?" });
        await Post("review/snapshots", null);
        var profile = await Post("stress/profiles", JsonNode.Parse($$"""
            {
              "name": "Load", "scenarioId": "{{orders.ScenarioId}}", "seed": "1",
              "arrivals": { "kind": "poisson", "ratePerSecond": 5, "durationSeconds": 5, "warmUpSeconds": 0 },
              "resources": [
                { "elementId": "{{OrdersReference.OrdersApi}}", "concurrency": 2, "queueCapacity": 10 },
                { "elementId": "{{OrdersReference.OrdersDb}}", "concurrency": 2, "queueCapacity": null }
              ],
              "serviceTimes": [
                { "target": "{{OrdersReference.PlaceOrder}}", "time": { "kind": "fixed", "ms": 5 } },
                { "target": "{{OrdersReference.OrdersDb}}", "time": { "kind": "fixed", "ms": 5 } },
                { "target": "{{OrdersReference.InventorySub}}", "time": { "kind": "fixed", "ms": 5 } },
                { "target": "{{OrdersReference.BillingSub}}", "time": { "kind": "fixed", "ms": 5 } }
              ],
              "subscriptions": [
                { "subscriptionId": "{{OrdersReference.InventorySub}}", "consumers": 1, "queueCapacity": null },
                { "subscriptionId": "{{OrdersReference.BillingSub}}", "consumers": 1, "queueCapacity": null }
              ],
              "branches": [], "overrides": [], "outages": []
            }
            """));
        await Post($"stress/profiles/{profile["id"]}/runs", null);
        await Post("load-tests/targets", new { name = "Staging", baseUrl = "https://staging.example", environment = "test" });

        // An AI flow proposal and an agent token for the project.
        ai.Stub.Json(ai.Completion(Reply("flow-orders")));
        var proposed = await ProposeFlowAsync(owner, projectId, OrdersRequest);
        Assert.True(proposed.IsSuccessStatusCode, await proposed.Content.ReadAsStringAsync());
        await TestHost.CreateAgentAsync(host.Factory, owner, ai.WorkspaceId, projectId: projectId);

        // Every module holds something of the project.
        var during = await CountAsync();
        var touched = during.Rows.Where(t => t.Value > before.Rows.GetValueOrDefault(t.Key)).Select(t => t.Key.Split('.')[0]).ToHashSet();
        Assert.Superset(
            new HashSet<string> { "modelling", "drafts", "schemas", "trace", "views", "docs", "promises", "costs", "review", "stress", "loadtests", "ai", "identity" },
            touched);
        Assert.True(during.LiveStreams > before.LiveStreams);

        var deleted = await owner.DeleteAsync($"/api/projects/{projectId}");
        Assert.True(deleted.StatusCode == HttpStatusCode.NoContent, await deleted.Content.ReadAsStringAsync());

        var after = await CountAsync();
        Assert.Empty(after.ProjectRows);
        var grown = after.Rows
            .Where(t => !Kept.Any(t.Key.StartsWith) && t.Value != before.Rows.GetValueOrDefault(t.Key))
            .Select(t => $"{t.Key}: {before.Rows.GetValueOrDefault(t.Key)} -> {t.Value}");
        Assert.Empty(grown);
        Assert.Equal(before.LiveStreams, after.LiveStreams);
        // A deleted stream keeps only its tombstone.
        Assert.Equal(after.DeletedStreams, after.EventsOfDeletedStreams);
    }

    private sealed record Counts(
        Dictionary<string, long> Rows, List<string> ProjectRows, long LiveStreams, long DeletedStreams, long EventsOfDeletedStreams);

    /// <summary>The rows of every app table, the tables with a row of a project that no longer exists, and the event streams.</summary>
    private async Task<Counts> CountAsync()
    {
        await using var connection = new NpgsqlConnection(shared.Fixture.ConnectionString);
        await connection.OpenAsync();

        var tables = new List<(string Name, bool HasProject)>();
        await using (var command = new NpgsqlCommand("""
            SELECT t.table_schema || '.' || t.table_name,
                   EXISTS (SELECT 1 FROM information_schema.columns c
                           WHERE c.table_schema = t.table_schema AND c.table_name = t.table_name AND c.column_name = 'project_id')
            FROM information_schema.tables t
            WHERE t.table_type = 'BASE TABLE' AND t.table_schema NOT IN ('pg_catalog', 'information_schema', 'public', 'deedbox')
            ORDER BY 1
            """, connection))
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
                tables.Add((reader.GetString(0), reader.GetBoolean(1)));
        }

        async Task<long> Scalar(string sql)
        {
            await using var command = new NpgsqlCommand(sql, connection);
            return (long)(await command.ExecuteScalarAsync())!;
        }

        var rows = new Dictionary<string, long>();
        var projectRows = new List<string>();
        foreach (var (name, hasProject) in tables)
        {
            var quoted = string.Join('.', name.Split('.').Select(part => $"\"{part}\""));
            rows[name] = await Scalar($"SELECT count(*) FROM {quoted}");
            if (hasProject && await Scalar($"SELECT count(*) FROM {quoted} x WHERE NOT EXISTS (SELECT 1 FROM workspaces.projects p WHERE p.id = x.project_id)") > 0)
                projectRows.Add(name);
        }

        return new Counts(
            rows, projectRows,
            await Scalar("SELECT count(*) FROM deedbox.streams WHERE deleted_at IS NULL"),
            await Scalar("SELECT count(*) FROM deedbox.streams WHERE deleted_at IS NOT NULL"),
            await Scalar("""
                SELECT count(*) FROM deedbox.events e
                JOIN deedbox.streams s ON s.tenant_id = e.tenant_id AND s.stream_id = e.stream_id
                WHERE s.deleted_at IS NOT NULL
                """));
    }
}
