using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Vellum.Modules.Build;
using Vellum.Modules.Modelling.Model;
using Vellum.Modules.Promises;
using Vellum.Modules.Trace.Engine;
using Vellum.Tests.Modules.Promises;

namespace Vellum.Tests.Modules.Build;

/// <summary>
/// The standalone SDD and the agent bundle of the seeded Orders project: what they hold, that
/// they are the same bytes for the same pinned state, and that a bundle can be checked
/// against the state it names.
/// </summary>
[Collection("Integration")]
public class BuildExportTests(IntegrationFixture fixture)
{
    private static readonly Guid Payments = new("00000000-0000-0000-0000-000000000090");
    private static readonly Guid Refund = new("00000000-0000-0000-0000-000000000091");
    private static readonly Guid Guessed = new("00000000-0000-0000-0000-000000000092");

    private sealed record Seeded(
        HttpClient Owner, Guid WorkspaceId, OrdersReference Orders, Guid Ord014, Guid Ord015, Guid RefundScenario)
    {
        public string Build(string path) => $"/api/projects/{Orders.ProjectId}/build/{path}";
    }

    /// <summary>
    /// The Orders reference case with an outbox, ORD-014 approved with its scenario check run,
    /// a draft promise, an inferred element, and a Payments service that nothing of Orders touches.
    /// </summary>
    private static async Task<Seeded> SeedAsync(WebApplicationFactory<Program> factory)
    {
        var owner = await TestHost.SignInAsync(factory);
        var (workspaceId, projectId) = await TestHost.CreateProjectAsync(owner);
        var orders = await OrdersReference.SeedAsync(owner, projectId);
        await orders.UseOutboxAsync();
        async Task Post(string path, object body) => await TestHost.Created(owner.PostAsJsonAsync(orders.Url(path), body));

        await Post("elements", new { id = Payments, kind = "app", name = "Payments", parentId = OrdersReference.Shop });
        await Post("operations", new { id = Refund, ownerId = Payments, name = "POST /refunds", transport = "http" });
        await Post("elements", new { id = Guessed, kind = "app", name = "Guessed Cache", parentId = OrdersReference.Shop });
        var inferred = await owner.PatchAsJsonAsync(orders.Url($"elements/{Guessed}"),
            new { factStatus = "inferred", setProvenance = true, provenance = new { source = "github.com/acme/orders@abc:cache.go:1-9", revision = "abc", confidence = 0.4 } });
        Assert.True(inferred.StatusCode == HttpStatusCode.OK, await inferred.Content.ReadAsStringAsync());

        var refundScenario = Guid.NewGuid();
        await TestHost.Created(owner.PostAsync(orders.Url("scenarios"), OrdersReference.Json(RefundScenario(refundScenario, 10))));

        var ord014 = (await orders.CreateOrd014Async())["id"]!.GetValue<Guid>();
        await orders.ApproveAsync(ord014);
        Assert.Equal("passed", (await orders.RunAsync(ord014, "O1"))["result"]!.GetValue<string>());

        var draft = new JsonObject
        {
            ["title"] = "Orders are never charged twice",
            ["claim"] = "A retried order request charges the customer once.",
            ["origin"] = "ai-suggested",
            ["links"] = new JsonObject { ["elementIds"] = new JsonArray(OrdersReference.OrdersApi.ToString()) },
        };
        var ord015 = (await (await TestHost.Created(owner.PostAsync(orders.Url("promises"), OrdersReference.Json(draft)))).Content.ReadFromJsonAsync<JsonObject>())!["id"]!.GetValue<Guid>();
        return new Seeded(owner, workspaceId, orders, ord014, ord015, refundScenario);
    }

    private static JsonObject RefundScenario(Guid id, int amount) => JsonNode.Parse($$"""
        {
          "id": "{{id}}",
          "name": "Refund a payment",
          "steps": [
            { "type": "call", "id": "refund", "from": "{{OrdersReference.Customer}}", "operation": "{{Refund}}",
              "request": { "paymentId": "p-1", "amount": {{amount}} }, "response": { "refunded": true }, "then": [], "onError": [] }
          ],
          "faults": [],
          "assertions": [ { "type": "outcome", "id": "refund-accepted", "step": "refund", "expected": "Succeeded" } ]
        }
        """)!.AsObject();

    private static async Task<JsonObject> ManifestAsync(Seeded s, string query = "") =>
        (await s.Owner.GetFromJsonAsync<JsonObject>(s.Build("manifest" + query)))!;

    private static Dictionary<string, string> Hashes(JsonObject manifest) =>
        manifest["files"]!.AsArray().ToDictionary(f => f!["path"]!.GetValue<string>(), f => f!["sha256"]!.GetValue<string>());

    private static async Task<byte[]> ExportAsync(Seeded s, object body)
    {
        var response = await s.Owner.PostAsJsonAsync(s.Build("export"), body);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await response.Content.ReadAsByteArrayAsync();
    }

    private static Dictionary<string, byte[]> Unzip(byte[] zip)
    {
        using var archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read);
        return archive.Entries.ToDictionary(e => e.FullName, e =>
        {
            using var stream = e.Open();
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            return buffer.ToArray();
        });
    }

    private static string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static int Count(string text, string part) => text.Split(part).Length - 1;

    [Fact]
    public async Task The_sdd_holds_every_id_contract_payload_scenario_promise_and_blocker_of_the_orders_project()
    {
        using var factory = TestHost.Create(fixture);
        var s = await SeedAsync(factory);
        var orders = s.Orders;

        var response = await s.Owner.PostAsJsonAsync(s.Build("export"), new { format = "sdd" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/markdown", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("false", response.Headers.GetValues("X-Vellum-Build-Ready").Single());
        var sdd = await response.Content.ReadAsStringAsync();
        var manifest = await ManifestAsync(s);

        Assert.StartsWith("# Test Project: Software Design Document\n", sdd);
        Assert.DoesNotContain("\r", sdd);
        foreach (var section in SddDocument.Sections)
            Assert.Equal(1, Count(sdd, $"\n## {section}\n"));
        Assert.Contains($"Pinned model: branch stream `{manifest["pinned"]!["branch"]!.GetValue<Guid>()}` at version {manifest["pinned"]!["streamVersion"]!.GetValue<long>()}.", sdd);

        // Every stable ID of the model, the schemas and the scenarios.
        Guid[] ids =
        [
            OrdersReference.Customer, OrdersReference.Shop, OrdersReference.OrdersApi, OrdersReference.OrdersDb, OrdersReference.Inventory,
            OrdersReference.Billing, OrdersReference.Relay, OrdersReference.PlaceOrder, OrdersReference.RelayOutbox, OrdersReference.OrderEvents,
            OrdersReference.DeadLetters, OrdersReference.InventorySub, OrdersReference.BillingSub, OrdersReference.OrderCreated,
            orders.OrderRequestSchema, orders.OrderCreatedSchema, orders.ScenarioId, Payments, Refund, Guessed, s.RefundScenario, s.Ord014,
        ];
        Assert.All(ids, id => Assert.Contains($"`{id}`", sdd));

        // Contracts: transport, schemas, delivery rules and the dead-letter channel.
        Assert.Contains($"| `{OrdersReference.PlaceOrder}` | Orders API (`{OrdersReference.OrdersApi}`) | POST /orders | http | OrderRequest (`{orders.OrderRequestSchema}`) | none | 2000 | none | `openapi/{OrdersReference.OrdersApi}.yaml` | declared |", sdd);
        Assert.Contains($"| `{OrdersReference.RelayOutbox}` | Outbox Relay (`{OrdersReference.Relay}`) | Relay outbox | command | none | none | none | 3 attempts, 1000 ms backoff | none (not a routed HTTP operation) | declared |", sdd);
        Assert.Contains($"| `{OrdersReference.InventorySub}` | order-events (`{OrdersReference.OrderEvents}`) | Inventory (`{OrdersReference.Inventory}`) | 2 | 100 | order-events.dlq (`{OrdersReference.DeadLetters}`) | declared |", sdd);
        Assert.Contains("none: a message that fails every attempt is lost", sdd);
        Assert.Contains($"| `{OrdersReference.OrderCreated}` | OrderCreated |  | Orders API (`{OrdersReference.OrdersApi}`) |", sdd);
        Assert.Contains("\"required\": [\n    \"sku\",\n    \"qty\"\n  ]", sdd);

        // Payloads taken from the scenarios, and the scenario as a numbered step table.
        Assert.Contains("```json\n{\n  \"qty\": 2,\n  \"sku\": \"apple\"\n}\n```", sdd);
        Assert.Contains("```json\n{\n  \"orderId\": \"o-1\"\n}\n```", sdd);
        Assert.Contains("Headers: `x-correlation-id: c-1`", sdd);
        Assert.Contains($"### Place an order (`{orders.ScenarioId}`)", sdd);
        Assert.Contains($"| 1 | `place` | call |  | Customer (`{OrdersReference.Customer}`) | Orders API (`{OrdersReference.OrdersApi}`) | POST /orders (`{OrdersReference.PlaceOrder}`) | Payload 1 Payload 2 |", sdd);
        Assert.Contains("| 3 | `save-outbox` | write | 1 (callee) |", sdd);
        Assert.Contains($"| 5 | `{OrdersReference.PublishStep}` | publish | 4 (callee) |", sdd);
        Assert.Contains($"| `{orders.ScenarioId}/order-accepted` | step `place` has the outcome Succeeded | holds:", sdd);
        Assert.Contains("| Orders DB (`00000000-0000-0000-0000-000000000004`) | outbox | Orders API", sdd);

        // The promise with its claim, forbidden outcome, obligations, evidence-scoped status and gaps.
        Assert.Contains("### ORD-014: An accepted order must survive a broker outage", sdd);
        Assert.Contains("| Forbidden outcome | The request reports success and OrderCreated is lost. |", sdd);
        Assert.Contains("| Preconditions | The order is valid.", sdd);
        Assert.Contains("Obligations: 1 passed, 0 failed, 3 not run, 0 unsupported.", sdd);
        Assert.Contains("| `ORD-014/O1` | scenario-check | The order flow holds while the broker is down | passed | Scenario 'Place an order'", sdd);
        Assert.Contains("| declared-design |", sdd);
        Assert.Contains("- Limitation: This result shows only that the declared design behaves so", sdd);
        Assert.Contains("- ORD-014/O2 is a test plan with no external evidence. A generated plan alone establishes nothing.", sdd);
        Assert.Contains("- ORD-014/O4 has not been run.", sdd);
        Assert.Contains("Required source pattern: `transactional-outbox` in `acme/orders`.", sdd);

        // A draft promise is never a requirement.
        var notApproved = sdd[sdd.IndexOf("### Not approved: do not implement as requirements", StringComparison.Ordinal)..];
        Assert.Contains("| `ORD-015` | Orders are never charged twice | ai-suggested |", notApproved);
        Assert.DoesNotContain("### ORD-015", sdd);

        // Slices in dependency order: the relay and the consumers come after the service they depend on.
        var slices = sdd.Split('\n').Where(l => l.StartsWith("### Slice ", StringComparison.Ordinal)).ToList();
        Assert.Equal(5, slices.Count);
        Assert.StartsWith("### Slice 1: Orders API", slices[0]);
        Assert.True(slices.FindIndex(l => l.Contains("Outbox Relay")) < slices.FindIndex(l => l.Contains("Inventory")));
        Assert.Contains("- Promises that bind it: `ORD-014`, `ORD-015`", sdd);

        // Acceptance tests: each scenario assertion and each obligation of an approved promise, with its ID.
        var checklist = sdd.Split('\n').Where(l => l.StartsWith("- [ ] ", StringComparison.Ordinal)).ToList();
        Assert.Equal(3 + 4, checklist.Count);
        Assert.Contains($"- [ ] `{s.RefundScenario}/refund-accepted` Refund a payment: step `refund` has the outcome Succeeded", checklist);
        Assert.Contains("- [ ] `ORD-014/O3` (contract-test) Orders API: POST /orders keeps its contract", checklist);

        // Blockers: the same list in the SDD and in the manifest. They prevent Build Ready, not the export.
        var blockers = manifest["blockers"]!.AsArray().Select(b => (Code: b!["code"]!.GetValue<string>(), Subject: b["subject"]!.GetValue<string>(), Message: b["message"]!.GetValue<string>())).ToList();
        Assert.False(manifest["buildReady"]!.GetValue<bool>());
        Assert.Equal(["fact-inferred", "promise-gap", "promise-gap", "promise-gap", "promise-not-approved"], blockers.Select(b => b.Code));
        Assert.Equal((Guessed.ToString(), "ORD-014", "ORD-015"), (blockers[0].Subject, blockers[1].Subject, blockers[4].Subject));
        Assert.Contains($"{blockers.Count} blocker(s). The pinned state is not build ready.", sdd);
        Assert.All(blockers, b => Assert.Contains($"| {b.Code} | `{b.Subject}` | {b.Message} |", sdd));

        // The generated contract documents read back as the model.
        var check = manifest["selfCheck"]!;
        Assert.True(check["passed"]!.GetValue<bool>(), check.ToJsonString());
        Assert.Equal((2, 2), (check["openApiOperations"]!["expected"]!.GetValue<int>(), check["openApiOperations"]!["parsed"]!.GetValue<int>()));
        Assert.Equal((2, 2), (check["asyncApiChannels"]!["expected"]!.GetValue<int>(), check["asyncApiChannels"]!["parsed"]!.GetValue<int>()));
        Assert.Equal((1, 1), (check["asyncApiMessages"]!["expected"]!.GetValue<int>(), check["asyncApiMessages"]!["parsed"]!.GetValue<int>()));
        Assert.Equal((2, 2), (check["asyncApiSubscriptions"]!["expected"]!.GetValue<int>(), check["asyncApiSubscriptions"]!["parsed"]!.GetValue<int>()));
        Assert.Equal((1, 1), (check["asyncApiPublications"]!["expected"]!.GetValue<int>(), check["asyncApiPublications"]!["parsed"]!.GetValue<int>()));

        Assert.Equal(
            new[]
            {
                "SDD.md", "architecture.json", "asyncapi.yaml",
                $"openapi/{OrdersReference.OrdersApi}.yaml", $"openapi/{Payments}.yaml",
                "promises/ORD-014.json", "promises/ORD-015.json",
                $"scenarios/{orders.ScenarioId}.json", $"scenarios/{s.RefundScenario}.json",
            }.Order(StringComparer.Ordinal),
            manifest["files"]!.AsArray().Select(f => f!["path"]!.GetValue<string>()));
        Assert.Equal(("vellum-bundle/1", "vellum-build", orders.ProjectId), (
            manifest["format"]!.GetValue<string>(), manifest["generator"]!["name"]!.GetValue<string>(),
            manifest["pinned"]!["projectId"]!.GetValue<Guid>()));
        Assert.Null(manifest["pinned"]!["revisionId"]);
    }

    [Fact]
    public async Task The_same_pinned_state_gives_the_same_bytes_and_a_changed_payload_changes_only_the_files_that_hold_it()
    {
        using var factory = TestHost.Create(fixture);
        var s = await SeedAsync(factory);

        // Two exports: the same zip, byte for byte.
        var zip = await ExportAsync(s, new { format = "bundle" });
        Assert.Equal(zip, await ExportAsync(s, new { format = "bundle" }));

        var manifest = await ManifestAsync(s);
        var entries = Unzip(zip);
        using (var archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read))
        {
            Assert.Equal(archive.Entries.Select(e => e.FullName).Order(StringComparer.Ordinal), archive.Entries.Select(e => e.FullName));
            Assert.All(archive.Entries, e => Assert.Equal(new DateTime(1980, 1, 1), e.LastWriteTime.DateTime));
            Assert.All(archive.Entries, e => Assert.Equal(e.Length, e.CompressedLength));
        }

        // The manifest in the zip is the manifest endpoint's, and it lists the real hash and size of every file.
        Assert.True(JsonNode.DeepEquals(manifest, JsonNode.Parse(entries["manifest.json"])), Encoding.UTF8.GetString(entries["manifest.json"]));
        var hashes = Hashes(manifest);
        Assert.Equal(entries.Keys.Where(k => k != "manifest.json").Order(StringComparer.Ordinal), hashes.Keys.Order(StringComparer.Ordinal));
        Assert.All(manifest["files"]!.AsArray(), f =>
        {
            var bytes = entries[f!["path"]!.GetValue<string>()];
            Assert.Equal((Sha(bytes), bytes.LongLength), (f["sha256"]!.GetValue<string>(), f["bytes"]!.GetValue<long>()));
        });
        var listing = string.Concat(hashes.OrderBy(h => h.Key, StringComparer.Ordinal).Select(h => $"{h.Key} {h.Value}\n"));
        Assert.Equal(Sha(Encoding.UTF8.GetBytes(listing)), manifest["bundleHash"]!.GetValue<string>());

        // No file holds a line end other than \n.
        Assert.All(entries, e => Assert.DoesNotContain((byte)'\r', e.Value));

        // One payload of one scenario changes: that scenario's file and the SDD that shows the payload, and nothing else.
        var changed = await s.Owner.PutAsync(s.Orders.Url($"scenarios/{s.RefundScenario}"), OrdersReference.Json(RefundScenario(s.RefundScenario, 25)));
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        var after = await ManifestAsync(s);
        var afterHashes = Hashes(after);
        Assert.Equal(
            new[] { "SDD.md", $"scenarios/{s.RefundScenario}.json" },
            hashes.Keys.Where(path => hashes[path] != afterHashes[path]).Order(StringComparer.Ordinal));
        Assert.NotEqual(manifest["bundleHash"]!.GetValue<string>(), after["bundleHash"]!.GetValue<string>());
        Assert.Equal(manifest["pinned"]!.ToJsonString(), after["pinned"]!.ToJsonString());

        // Changing it back gives the first bytes again.
        await s.Owner.PutAsync(s.Orders.Url($"scenarios/{s.RefundScenario}"), OrdersReference.Json(RefundScenario(s.RefundScenario, 10)));
        Assert.Equal(zip, await ExportAsync(s, new { format = "bundle" }));
    }

    [Fact]
    public async Task Verify_shows_that_a_bundle_is_what_its_pinned_state_generates_and_finds_an_edited_file()
    {
        using var factory = TestHost.Create(fixture);
        var s = await SeedAsync(factory);
        var manifest = await ManifestAsync(s);
        var zip = await ExportAsync(s, new { format = "bundle" });

        async Task<JsonObject> VerifyAsync(HttpContent content)
        {
            var response = await s.Owner.PostAsync(s.Build("verify"), content);
            Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
            return (await response.Content.ReadFromJsonAsync<JsonObject>())!;
        }
        Task<JsonObject> VerifyManifestAsync(JsonObject m) => VerifyAsync(OrdersReference.Json(new JsonObject { ["manifest"] = m.DeepClone() }));
        Task<JsonObject> VerifyZipAsync(byte[] bytes)
        {
            var content = new ByteArrayContent(bytes);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
            return VerifyAsync(content);
        }
        static Dictionary<string, string> Statuses(JsonObject result) =>
            result["files"]!.AsArray().ToDictionary(f => f!["path"]!.GetValue<string>(), f => f!["status"]!.GetValue<string>());

        // The untouched manifest and the untouched zip: every file matches.
        var same = await VerifyManifestAsync(manifest);
        Assert.True(same["identical"]!.GetValue<bool>());
        Assert.Equal(same["expectedBundleHash"]!.GetValue<string>(), same["actualBundleHash"]!.GetValue<string>());
        Assert.All(Statuses(same).Values, status => Assert.Equal("match", status));
        Assert.True(same["selfCheck"]!["passed"]!.GetValue<bool>());
        Assert.True((await VerifyZipAsync(zip))["identical"]!.GetValue<bool>());

        // A manifest with one changed hash, one file left out and one file that is not generated.
        var edited = manifest.DeepClone().AsObject();
        var files = edited["files"]!.AsArray();
        files.Single(f => f!["path"]!.GetValue<string>() == "SDD.md")!["sha256"] = new string('0', 64);
        files.Remove(files.Single(f => f!["path"]!.GetValue<string>() == "asyncapi.yaml"));
        files.Add(new JsonObject { ["path"] = "extra/notes.md", ["sha256"] = new string('1', 64), ["bytes"] = 1 });
        var differs = await VerifyManifestAsync(edited);
        Assert.False(differs["identical"]!.GetValue<bool>());
        var statuses = Statuses(differs);
        Assert.Equal(("differs", "missing", "unknown-file", "match"), (statuses["SDD.md"], statuses["asyncapi.yaml"], statuses["extra/notes.md"], statuses["architecture.json"]));
        Assert.NotEqual(differs["expectedBundleHash"]!.GetValue<string>(), differs["actualBundleHash"]!.GetValue<string>());

        // A zip with one edited file. Its manifest still lists the old hash; the bytes decide.
        var entries = Unzip(zip);
        entries["SDD.md"] = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(entries["SDD.md"]).Replace("must survive", "may survive"));
        using var rebuilt = new MemoryStream();
        using (var archive = new ZipArchive(rebuilt, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, bytes) in entries)
            {
                using var stream = archive.CreateEntry(path).Open();
                stream.Write(bytes);
            }
        }
        var tampered = await VerifyZipAsync(rebuilt.ToArray());
        Assert.False(tampered["identical"]!.GetValue<bool>());
        Assert.Equal(["SDD.md"], Statuses(tampered).Where(f => f.Value != "match").Select(f => f.Key));

        // The model moves on. The old manifest names its stream version, so it still verifies against that state.
        await TestHost.Created(s.Owner.PostAsJsonAsync(s.Orders.Url("elements"), new { id = Guid.NewGuid(), kind = "system", name = "Added later" }));
        Assert.NotEqual(manifest["bundleHash"]!.GetValue<string>(), (await ManifestAsync(s))["bundleHash"]!.GetValue<string>());
        Assert.True((await VerifyManifestAsync(manifest))["identical"]!.GetValue<bool>());

        // A manifest of another project, of no branch of this one, or of a version that does not exist is refused.
        async Task<HttpStatusCode> RefusedAsync(Action<JsonObject> change)
        {
            var body = manifest.DeepClone().AsObject();
            change(body);
            return (await s.Owner.PostAsync(s.Build("verify"), OrdersReference.Json(new JsonObject { ["manifest"] = body }))).StatusCode;
        }
        Assert.Equal(HttpStatusCode.BadRequest, await RefusedAsync(m => m["pinned"]!["projectId"] = Guid.NewGuid()));
        Assert.Equal(HttpStatusCode.BadRequest, await RefusedAsync(m => m["pinned"]!["branch"] = Guid.NewGuid()));
        Assert.Equal(HttpStatusCode.BadRequest, await RefusedAsync(m => m["pinned"]!["streamVersion"] = 100_000));
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Owner.PostAsync(s.Build("verify"), new StringContent("{}", Encoding.UTF8, "application/json"))).StatusCode);
        var notZip = new ByteArrayContent([1, 2, 3]);
        notZip.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Owner.PostAsync(s.Build("verify"), notZip)).StatusCode);
    }

    [Fact]
    public async Task An_export_of_a_named_revision_stays_the_same_when_the_model_changes()
    {
        using var factory = TestHost.Create(fixture);
        var s = await SeedAsync(factory);
        var revisionId = Guid.NewGuid();
        await TestHost.Created(s.Owner.PostAsJsonAsync(s.Orders.Url("revisions"), new { id = revisionId, name = "Release 1" }));

        var pinned = await ExportAsync(s, new { format = "bundle", revisionId });
        var pinnedManifest = await ManifestAsync(s, $"?revisionId={revisionId}");
        var current = await ManifestAsync(s);
        Assert.Equal(revisionId, pinnedManifest["pinned"]!["revisionId"]!.GetValue<Guid>());
        Assert.Equal(current["pinned"]!["streamVersion"]!.GetValue<long>(), pinnedManifest["pinned"]!["streamVersion"]!.GetValue<long>());
        // The same model: only the SDD differs, because it names what was pinned.
        Assert.Equal(["SDD.md"], Hashes(current).Where(f => Hashes(pinnedManifest)[f.Key] != f.Value).Select(f => f.Key));
        Assert.Contains($"Pinned model: revision 'Release 1' (`{revisionId}`)", Encoding.UTF8.GetString(Unzip(pinned)["SDD.md"]));

        // A model edit: the current export changes, the revision's does not.
        var renamed = await s.Owner.PatchAsJsonAsync(s.Orders.Url($"elements/{OrdersReference.Billing}"), new { name = "Invoicing" });
        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
        var moved = await ManifestAsync(s);
        Assert.True(moved["pinned"]!["streamVersion"]!.GetValue<long>() > current["pinned"]!["streamVersion"]!.GetValue<long>());
        Assert.Contains("architecture.json", Hashes(current).Where(f => Hashes(moved)[f.Key] != f.Value).Select(f => f.Key));
        Assert.Contains("Invoicing", Encoding.UTF8.GetString(await ExportAsync(s, new { format = "sdd" })));

        Assert.Equal(pinned, await ExportAsync(s, new { format = "bundle", revisionId }));
        Assert.DoesNotContain("Invoicing", Encoding.UTF8.GetString(Unzip(pinned)["SDD.md"]));

        // A revision and a branch together, an unknown revision and an unknown format are refused.
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Owner.PostAsJsonAsync(s.Build("export"), new { format = "bundle", revisionId, branchId = Guid.NewGuid() })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Owner.PostAsJsonAsync(s.Build("export"), new { format = "bundle", revisionId = Guid.NewGuid() })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Owner.PostAsJsonAsync(s.Build("export"), new { format = "pdf" })).StatusCode);
    }

    [Fact]
    public async Task A_brief_holds_the_closure_of_the_selection_and_no_unapproved_requirement()
    {
        using var factory = TestHost.Create(fixture);
        var s = await SeedAsync(factory);

        async Task<JsonObject> BriefAsync(object body)
        {
            var response = await s.Owner.PostAsJsonAsync(s.Build("brief"), body);
            Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
            return (await response.Content.ReadFromJsonAsync<JsonObject>())!;
        }
        static Guid[] Ids(JsonNode list) => [.. list.AsArray().Select(i => i!.GetValue<Guid>())];

        // ORD-014: its scenario, what the scenario's steps name, the consumers of the channel, and their parents.
        var byPromise = await BriefAsync(new { promiseIds = new[] { s.Ord014 } });
        var included = byPromise["manifest"]!["included"]!;
        Assert.Equal(
            new[] { OrdersReference.Customer, OrdersReference.Shop, OrdersReference.OrdersApi, OrdersReference.OrdersDb, OrdersReference.Inventory, OrdersReference.Billing, OrdersReference.Relay },
            Ids(included["elementIds"]!));
        Assert.Equal(new[] { OrdersReference.PlaceOrder, OrdersReference.RelayOutbox }, Ids(included["operationIds"]!));
        Assert.Equal(new[] { OrdersReference.OrderEvents, OrdersReference.DeadLetters }, Ids(included["channelIds"]!));
        Assert.Equal(new[] { OrdersReference.InventorySub, OrdersReference.BillingSub }, Ids(included["subscriptionIds"]!));
        Assert.Equal(new[] { OrdersReference.OrderCreated }, Ids(included["messageIds"]!));
        Assert.Equal(new[] { s.Orders.ScenarioId }, Ids(included["scenarioIds"]!));
        Assert.Equal(new[] { s.Orders.OrderRequestSchema, s.Orders.OrderCreatedSchema }.Order(), Ids(included["schemaIds"]!));
        Assert.Equal(["ORD-014"], included["requirementIds"]!.AsArray().Select(r => r!.GetValue<string>()));
        // ORD-015 links the Orders API, which is in scope. It is a draft, so it is named as not approved.
        Assert.Equal(["ORD-015"], byPromise["manifest"]!["notApproved"]!.AsArray().Select(r => r!.GetValue<string>()));

        var brief = byPromise["brief"]!.GetValue<string>();
        Assert.StartsWith("# Test Project: build brief\n", brief);
        Assert.Contains("### ORD-014: An accepted order must survive a broker outage", brief);
        Assert.Contains("- [ ] `ORD-014/O1` (scenario-check)", brief);
        var notApproved = brief[brief.IndexOf("## 6. Not approved: do not implement as requirements", StringComparison.Ordinal)..];
        Assert.Contains("| `ORD-015` | Orders are never charged twice |", notApproved);
        Assert.DoesNotContain("ORD-015", brief[..brief.IndexOf("## 6. Not approved", StringComparison.Ordinal)]);
        // Nothing of Payments, which no step, contract or link of the selection reaches.
        Assert.DoesNotContain("Payments", brief);
        Assert.DoesNotContain(Refund.ToString(), brief);
        Assert.DoesNotContain("Refund a payment", brief);
        Assert.Equal((Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(brief))), (long)Encoding.UTF8.GetByteCount(brief)),
            (byPromise["manifest"]!["sha256"]!.GetValue<string>(), byPromise["manifest"]!["bytes"]!.GetValue<long>()));
        Assert.Equal(byPromise.ToJsonString(), (await BriefAsync(new { promiseIds = new[] { s.Ord014 } })).ToJsonString());

        // One element: what it owns, its parent, and no promise that does not link it.
        var byElement = await BriefAsync(new { elementIds = new[] { Payments } });
        Assert.Equal(new[] { OrdersReference.Shop, Payments }, Ids(byElement["manifest"]!["included"]!["elementIds"]!));
        Assert.Equal(new[] { Refund }, Ids(byElement["manifest"]!["included"]!["operationIds"]!));
        Assert.Empty(byElement["manifest"]!["included"]!["requirementIds"]!.AsArray());
        Assert.Empty(byElement["manifest"]!["included"]!["scenarioIds"]!.AsArray());
        Assert.DoesNotContain("ORD-014", byElement["brief"]!.GetValue<string>());

        // An inferred fact is in scope by ID and is left out of the approved facts.
        var byGuess = await BriefAsync(new { elementIds = new[] { Guessed } });
        Assert.Equal(new[] { Guessed }, Ids(byGuess["manifest"]!["unconfirmedFacts"]!));
        var guessBrief = byGuess["brief"]!.GetValue<string>();
        var facts = guessBrief[guessBrief.IndexOf("## 2. Approved architecture facts", StringComparison.Ordinal)..guessBrief.IndexOf("## 3. Scenarios", StringComparison.Ordinal)];
        Assert.DoesNotContain("Guessed Cache", facts);
        Assert.Contains($"| `{Guessed}` | element | Guessed Cache | inferred |", guessBrief);
        Assert.False(byGuess["manifest"]!["buildReady"]!.GetValue<bool>());

        // A draft promise that is selected is not expanded and not a requirement.
        var byDraft = await BriefAsync(new { promiseIds = new[] { s.Ord015 } });
        Assert.Empty(byDraft["manifest"]!["included"]!["requirementIds"]!.AsArray());
        Assert.Empty(byDraft["manifest"]!["included"]!["elementIds"]!.AsArray());
        Assert.Equal(["ORD-015"], byDraft["manifest"]!["notApproved"]!.AsArray().Select(r => r!.GetValue<string>()));

        // The selection must name things of the project.
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Owner.PostAsJsonAsync(s.Build("brief"), new { })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Owner.PostAsJsonAsync(s.Build("brief"), new { promiseIds = new[] { Guid.NewGuid() } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Owner.PostAsJsonAsync(s.Build("brief"), new { scenarioIds = new[] { Guid.NewGuid() } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Owner.PostAsJsonAsync(s.Build("brief"), new { elementIds = new[] { Guid.NewGuid() } })).StatusCode);
    }

    [Fact]
    public async Task A_viewer_exports_and_an_outsider_does_not_and_an_export_has_a_size_bound()
    {
        using var factory = TestHost.Create(fixture);
        var s = await SeedAsync(factory);
        var viewer = await TestHost.SignInMemberAsync(factory, s.Owner, s.WorkspaceId, "Viewer");
        var outsider = await TestHost.SignInAsync(factory);
        var anonymous = factory.CreateClient();
        var manifest = await ManifestAsync(s);

        async Task<HttpStatusCode[]> AllAsync(HttpClient client) =>
        [
            (await client.PostAsJsonAsync(s.Build("export"), new { format = "bundle" })).StatusCode,
            (await client.PostAsJsonAsync(s.Build("export"), new { format = "sdd" })).StatusCode,
            (await client.GetAsync(s.Build("manifest"))).StatusCode,
            (await client.PostAsJsonAsync(s.Build("brief"), new { promiseIds = new[] { s.Ord014 } })).StatusCode,
            (await client.PostAsync(s.Build("verify"), OrdersReference.Json(new JsonObject { ["manifest"] = manifest.DeepClone() }))).StatusCode,
        ];

        // Every build endpoint reads and writes nothing, so a viewer may call them all.
        Assert.All(await AllAsync(viewer), status => Assert.Equal(HttpStatusCode.OK, status));
        Assert.All(await AllAsync(outsider), status => Assert.Equal(HttpStatusCode.Forbidden, status));
        Assert.All(await AllAsync(anonymous), status => Assert.Equal(HttpStatusCode.Unauthorized, status));

        // An export larger than the bound is refused whole. Nothing is cut to fit.
        using var small = TestHost.Create(fixture.ConnectionString, configure: services =>
            services.AddSingleton(BuildLimits.Default with { MaxBundleBytes = 4_000 }));
        var client = TestHost.Client(small);
        // The same user signs in to the second host: both hosts share the database.
        var email = (await s.Owner.GetFromJsonAsync<JsonObject>("/api/auth/me"))!["email"]!.GetValue<string>();
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/auth/login", new { email, password = "Test12345!" })).StatusCode);
        foreach (var response in new[]
                 {
                     await client.PostAsJsonAsync(s.Build("export"), new { format = "bundle" }),
                     await client.PostAsJsonAsync(s.Build("export"), new { format = "sdd" }),
                     await client.GetAsync(s.Build("manifest")),
                 })
        {
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
            Assert.Equal("bundle_too_large", (await response.Content.ReadFromJsonAsync<JsonObject>())!["type"]!.GetValue<string>());
        }

        using var smallFile = TestHost.Create(fixture.ConnectionString, configure: services =>
            services.AddSingleton(BuildLimits.Default with { MaxFileBytes = 1_000 }));
        var second = TestHost.Client(smallFile);
        Assert.Equal(HttpStatusCode.OK, (await second.PostAsJsonAsync("/api/auth/login", new { email, password = "Test12345!" })).StatusCode);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await second.PostAsJsonAsync(s.Build("brief"), new { promiseIds = new[] { s.Ord014 } })).StatusCode);
    }

    /// <summary>
    /// The export is a function of what the input holds, not of how it was put together: the
    /// same model built from events in another order, with every list reversed, gives the same files.
    /// </summary>
    [Fact]
    public void The_order_of_inserts_does_not_change_a_byte()
    {
        Guid G(int n) => new($"00000000-0000-0000-0000-{n:D12}");
        var schema = new BuildSchema(G(70), "OrderCreated", null, """{"required":["orderId"],"type":"object"}""", 1);
        var other = new BuildSchema(G(71), "OrderRequest", "The body of POST /orders", """{"type":"object","properties":{"qty":{"type":"integer"}}}""", 3);
        object[] events =
        [
            new ModelEvent.ElementAdded(G(1), ElementKind.Actor, "Customer", null, null, null, ElementStatus.Current, null, []),
            new ModelEvent.ElementAdded(G(2), ElementKind.System, "Shop", "Sells things", null, null, ElementStatus.Current, null, ["core"]),
            new ModelEvent.ElementAdded(G(3), ElementKind.App, "Orders", null, "dotnet", null, ElementStatus.Current, G(2), []),
            new ModelEvent.ElementAdded(G(4), ElementKind.App, "Billing", null, "go", null, ElementStatus.Planned, G(2), []),
            new ModelEvent.ElementAdded(G(5), ElementKind.Store, "Orders DB", null, null, null, ElementStatus.Current, G(2), []),
            new ModelEvent.RelationshipAdded(G(50), G(3), G(5), "writes", "SQL", null),
            new ModelEvent.RelationshipAdded(G(51), G(1), G(3), "orders", "HTTPS", null, RelationshipKind.Behavioural),
            new ModelEvent.MessageAdded(G(40), "OrderCreated", null, G(3), [G(4)], G(70), []),
            new ModelEvent.OperationAdded(G(10), G(3), "POST /orders", Transport.Http, G(71), null, 500, new RetryPolicy(2, 50)),
            new ModelEvent.OperationAdded(G(11), G(4), "GET /invoices/{id}", Transport.Http, null, null, 0, RetryPolicy.None),
            new ModelEvent.ChannelAdded(G(20), "order-events", ChannelKind.Topic),
            new ModelEvent.ChannelAdded(G(21), "dead-letters", ChannelKind.Queue),
            new ModelEvent.SubscriptionAdded(G(30), G(20), G(4), new RetryPolicy(3, 100), G(21)),
        ];

        Scenario ScenarioOf(Guid id, string name) => JsonSerializer.Deserialize<Scenario>($$"""
            {
              "id": "{{id}}", "name": "{{name}}",
              "steps": [
                { "type": "call", "id": "place", "from": "{{G(1)}}", "operation": "{{G(10)}}", "request": { "qty": 1.0, "b": 2, "a": 1 }, "response": null,
                  "then": [ { "type": "write", "id": "save", "by": "{{G(3)}}", "store": "{{G(5)}}", "dataset": "orders", "change": null },
                            { "type": "publish", "id": "publish", "from": "{{G(3)}}", "channel": "{{G(20)}}", "message": "{{G(40)}}", "payload": { "orderId": "o-1" }, "headers": {}, "handlers": {} } ],
                  "onError": [] }
              ],
              "faults": [ { "target": "{{G(4)}}", "kind": "Error", "failFirst": 1 } ],
              "assertions": [ { "type": "event-count", "id": "no-drop", "kind": "Dropped", "target": null, "expected": 0 } ]
            }
            """, BuildText.Json)!;

        BuildInput Input(IEnumerable<object> order, bool reversed)
        {
            var model = order.Aggregate(ModelState.Initial, ModelState.Evolve);
            BuildSchema[] schemas = [schema, other];
            Scenario[] scenarios = [ScenarioOf(G(60), "Place an order"), ScenarioOf(G(61), "Place another order")];
            return new BuildInput(
                G(99), "Orders", new ModelPin(G(98), 13, null, null), model,
                reversed ? [.. schemas.Reverse()] : schemas, reversed ? [.. scenarios.Reverse()] : scenarios, []);
        }

        var first = Bundle.Build(Input(events, reversed: false), BuildLimits.Default);
        // Elements keep their order (a child needs its parent); everything else is added in reverse.
        var second = Bundle.Build(Input(events.Take(5).Concat(events.Skip(5).Reverse().OrderBy(e => e is ModelEvent.SubscriptionAdded ? 1 : 0)), reversed: true), BuildLimits.Default);

        Assert.Equal(first.Files.Keys, second.Files.Keys);
        Assert.All(first.Files, file => Assert.True(file.Value.AsSpan().SequenceEqual(second.Files[file.Key]), file.Key));
        Assert.Equal(first.ManifestBytes, second.ManifestBytes);
        Assert.Equal(first.Zip(), second.Zip());
        Assert.True(first.Manifest.SelfCheck.Passed, Encoding.UTF8.GetString(first.ManifestBytes));
        Assert.True(first.Manifest.BuildReady);

        // Canonical JSON: sorted keys and one spelling of a number, whatever the scenario held.
        var scenarioFile = Encoding.UTF8.GetString(first.Files[$"scenarios/{G(60)}.json"]);
        Assert.Contains("\"request\": {\n          \"a\": 1,\n          \"b\": 2,\n          \"qty\": 1\n        }", scenarioFile);
        // The OpenAPI document of each service holds its own operations only.
        var billing = Encoding.UTF8.GetString(first.Files[$"openapi/{G(4)}.yaml"]);
        Assert.Contains("/invoices/{id}:", billing);
        Assert.DoesNotContain("/orders:", billing);
        var asyncApi = Encoding.UTF8.GetString(first.Files["asyncapi.yaml"]);
        Assert.Contains("asyncapi: 3.0.0", asyncApi);
        Assert.Contains("address: order-events", asyncApi);
        Assert.Contains($"x-vellum-dead-letter-channel-id: {G(21)}", asyncApi);
    }
}
