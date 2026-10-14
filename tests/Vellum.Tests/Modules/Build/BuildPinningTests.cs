using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Vellum.Modules.Build;
using Vellum.Tests.Modules.Promises;

namespace Vellum.Tests.Modules.Build;

/// <summary>
/// What a bundle is bound to and what binds an implementer: a bundle is generated again from
/// exactly the versions its manifest pins, a changed bundle or manifest is told apart from a
/// project that moved on, a zip that is not a bundle is refused, only approved promises in
/// force are requirements, and the SDD states what the verification matrix asks of it.
/// </summary>
public class BuildPinningTests(IntegrationFixture fixture) : IClassFixture<IntegrationFixture>
{
    private sealed record Seeded(HttpClient Owner, Guid WorkspaceId, OrdersReference Orders, Guid Ord014)
    {
        public string Build(string path) => $"/api/projects/{Orders.ProjectId}/build/{path}";
        public string Promises(string path = "") => Orders.Url("promises" + path);
    }

    /// <summary>The Orders reference case with ORD-014 approved and its scenario check run.</summary>
    private static async Task<Seeded> SeedAsync(WebApplicationFactory<Program> factory, bool outbox = true)
    {
        var owner = await TestHost.SignInAsync(factory);
        var (workspaceId, projectId) = await TestHost.CreateProjectAsync(owner);
        var orders = await OrdersReference.SeedAsync(owner, projectId);
        if (outbox)
            await orders.UseOutboxAsync();
        var ord014 = (await orders.CreateOrd014Async())["id"]!.GetValue<Guid>();
        await orders.ApproveAsync(ord014);
        await orders.RunAsync(ord014, "O1");
        return new Seeded(owner, workspaceId, orders, ord014);
    }

    private static async Task<JsonObject> Ok(Task<HttpResponseMessage> request, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = await request;
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode}: {text}");
        return JsonNode.Parse(text)!.AsObject();
    }

    private static async Task<byte[]> ExportAsync(Seeded s, object body)
    {
        var response = await s.Owner.PostAsJsonAsync(s.Build("export"), body);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await response.Content.ReadAsByteArrayAsync();
    }

    private static List<(string Path, byte[] Bytes)> Unzip(byte[] zip)
    {
        using var archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read);
        return archive.Entries.Select(e =>
        {
            using var stream = e.Open();
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            return (e.FullName, buffer.ToArray());
        }).ToList();
    }

    /// <summary>A zip as any zip tool writes it. Entry names may repeat and need not be plain.</summary>
    private static byte[] Zip(IEnumerable<(string Path, byte[] Bytes)> entries)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, bytes) in entries)
            {
                using var stream = archive.CreateEntry(path).Open();
                stream.Write(bytes);
            }
        }
        return buffer.ToArray();
    }

    private static Task<HttpResponseMessage> VerifyZipAsync(HttpClient client, string url, byte[] zip)
    {
        var content = new ByteArrayContent(zip);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        return client.PostAsync(url, content);
    }

    private static Task<HttpResponseMessage> VerifyManifestAsync(Seeded s, JsonNode manifest) =>
        s.Owner.PostAsync(s.Build("verify"), OrdersReference.Json(new JsonObject { ["manifest"] = manifest.DeepClone() }));

    private static string[] Text(JsonNode list) => [.. list.AsArray().Select(i => i!.GetValue<string>())];

    [Fact]
    public async Task Verify_finds_an_edited_manifest_and_refuses_a_zip_that_is_not_a_bundle()
    {
        var factory = fixture.Factory;
        var s = await SeedAsync(factory);
        var zip = await ExportAsync(s, new { format = "bundle" });
        var entries = Unzip(zip);

        // The zip as it was exported: every file, the manifest and the archive itself are what the pinned versions generate.
        var same = await Ok(VerifyZipAsync(s.Owner, s.Build("verify"), zip));
        Assert.Equal((true, true, "match", "match", true), (
            same["identical"]!.GetValue<bool>(), same["matchesPinned"]!.GetValue<bool>(), same["manifest"]!.GetValue<string>(),
            same["archive"]!.GetValue<string>(), same["current"]!.GetValue<bool>()));
        Assert.Empty(same["manifestDifferences"]!.AsArray());
        Assert.Empty(same["moved"]!.AsArray());

        // The manifest says "not build ready" and lists blockers. Someone edits it to say the opposite; no file is touched.
        var manifest = JsonNode.Parse(entries.Single(e => e.Path == "manifest.json").Bytes)!.AsObject();
        Assert.False(manifest["buildReady"]!.GetValue<bool>());
        Assert.NotEmpty(manifest["blockers"]!.AsArray());
        var flipped = manifest.DeepClone().AsObject();
        flipped["buildReady"] = true;
        flipped["blockers"] = new JsonArray();
        var edited = await Ok(VerifyZipAsync(s.Owner, s.Build("verify"),
            Zip(entries.Select(e => e.Path == "manifest.json" ? (e.Path, Encoding.UTF8.GetBytes(flipped.ToJsonString())) : e))));
        Assert.Equal((false, false, "differs"), (edited["identical"]!.GetValue<bool>(), edited["matchesPinned"]!.GetValue<bool>(), edited["manifest"]!.GetValue<string>()));
        Assert.Equal(["blockers", "buildReady"], Text(edited["manifestDifferences"]!));
        Assert.All(edited["files"]!.AsArray(), f => Assert.Equal("match", f!["status"]!.GetValue<string>()));
        // The same edit, sent as a manifest alone.
        var editedAlone = await Ok(VerifyManifestAsync(s, flipped));
        Assert.Equal((false, "differs"), (editedAlone["identical"]!.GetValue<bool>(), editedAlone["manifest"]!.GetValue<string>()));
        Assert.Equal(["blockers", "buildReady"], Text(editedAlone["manifestDifferences"]!));
        // An untouched manifest alone matches, whatever its formatting.
        var alone = await Ok(VerifyManifestAsync(s, manifest));
        Assert.Equal((true, "match"), (alone["identical"]!.GetValue<bool>(), alone["manifest"]!.GetValue<string>()));
        Assert.Null(alone["archive"]);

        // The same files in a zip that another tool wrote: the content is the pinned content; the archive is not Vellum's.
        var rezipped = await Ok(VerifyZipAsync(s.Owner, s.Build("verify"), Zip(entries)));
        Assert.Equal((true, "match", "differs", false), (
            rezipped["matchesPinned"]!.GetValue<bool>(), rezipped["manifest"]!.GetValue<string>(), rezipped["archive"]!.GetValue<string>(), rezipped["identical"]!.GetValue<bool>()));

        async Task<JsonObject> InvalidAsync(IEnumerable<(string Path, byte[] Bytes)> zipped, string field)
        {
            var refused = await Ok(VerifyZipAsync(s.Owner, s.Build("verify"), Zip(zipped)), HttpStatusCode.BadRequest);
            Assert.Equal("invalid_bundle", refused["type"]!.GetValue<string>());
            Assert.Contains(refused["errors"]!.AsArray(), e => e!["field"]!.GetValue<string>() == field);
            return refused;
        }

        // A second SDD.md: one reader takes the first entry and another takes the last.
        var sdd = entries.Single(e => e.Path == "SDD.md");
        var twice = await InvalidAsync(entries.Append(("SDD.md", Encoding.UTF8.GetBytes("# Another design\n"))), "SDD.md");
        Assert.Contains("more than once", twice["errors"]![0]!["message"]!.GetValue<string>());
        await InvalidAsync(entries.Prepend(("SDD.md", Encoding.UTF8.GetBytes("# Another design\n"))), "SDD.md");
        // A file the manifest does not list, and a listed file that is not there.
        await InvalidAsync(entries.Append(("notes/extra.md", [1])), "notes/extra.md");
        await InvalidAsync(entries.Where(e => e.Path != "SDD.md"), "SDD.md");
        await InvalidAsync(entries.Where(e => e.Path != "manifest.json"), "manifest.json");
        // Names that are another spelling of a path.
        foreach (var name in new[] { "./SDD.md", "/SDD.md", "x/../SDD.md", "promises\\ORD-014.json", "promises//ORD-014.json", "promises/", "SDD.md​" })
            await InvalidAsync(entries.Append((name, sdd.Bytes)), name);

        // More entries than a bundle holds.
        using var small = TestHost.Create(fixture.ConnectionString, configure: services =>
            services.AddSingleton(BuildLimits.Default with { MaxFiles = 4 }));
        var client = TestHost.Client(small);
        var email = (await s.Owner.GetFromJsonAsync<JsonObject>("/api/auth/me"))!["email"]!.GetValue<string>();
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/auth/login", new { email, password = "Test12345!" })).StatusCode);
        var tooMany = await Ok(VerifyZipAsync(client, s.Build("verify"), zip), HttpStatusCode.BadRequest);
        Assert.Equal("invalid_bundle", tooMany["type"]!.GetValue<string>());
        Assert.Contains("a bundle holds at most 5", tooMany["errors"]![0]!["message"]!.GetValue<string>());
        // A manifest alone that lists more files than a bundle holds, or one file twice.
        Assert.Equal("invalid_bundle", (await Ok(client.PostAsync(s.Build("verify"), OrdersReference.Json(new JsonObject { ["manifest"] = manifest.DeepClone() })), HttpStatusCode.BadRequest))["type"]!.GetValue<string>());
        var listedTwice = manifest.DeepClone().AsObject();
        listedTwice["files"]!.AsArray().Add(listedTwice["files"]![0]!.DeepClone());
        Assert.Equal("invalid_bundle", (await Ok(VerifyManifestAsync(s, listedTwice), HttpStatusCode.BadRequest))["type"]!.GetValue<string>());

        // A manifest alone has a size limit of its own, far below that of a bundle.
        var huge = new StringContent("{\"manifest\":{}}" + new string(' ', (int)BuildLimits.Default.MaxManifestBytes), Encoding.UTF8, "application/json");
        var tooLarge = await Ok(s.Owner.PostAsync(s.Build("verify"), huge), HttpStatusCode.RequestEntityTooLarge);
        Assert.Equal("payload_too_large", tooLarge["type"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_bundle_is_generated_again_from_its_pinned_versions_whatever_changed_later()
    {
        var factory = fixture.Factory;
        var s = await SeedAsync(factory);
        var orders = s.Orders;
        var zip = await ExportAsync(s, new { format = "bundle" });
        var manifest = JsonNode.Parse(Unzip(zip).Single(e => e.Path == "manifest.json").Bytes)!.AsObject();

        // The manifest names the version of every input, and the runs it shows, by ID.
        var pinned = manifest["pinned"]!;
        Assert.Equal("Test Project", pinned["projectName"]!.GetValue<string>());
        Assert.Equal((orders.ScenarioId, 2L), (pinned["scenarios"]![0]!["id"]!.GetValue<Guid>(), pinned["scenarios"]![0]!["version"]!.GetValue<long>()));
        Assert.Equal(new[] { orders.OrderCreatedSchema, orders.OrderRequestSchema }.ToHashSet(), pinned["schemas"]!.AsArray().Select(p => p!["id"]!.GetValue<Guid>()).ToHashSet());
        var promisePin = Assert.Single(pinned["promises"]!.AsArray())!;
        Assert.Equal(s.Ord014, promisePin["id"]!.GetValue<Guid>());
        var runPin = Assert.Single(pinned["runs"]!.AsArray())!;
        Assert.Equal((s.Ord014, 1, "ORD-014/O1"), (runPin["promiseId"]!.GetValue<Guid>(), runPin["promiseRevision"]!.GetValue<int>(), runPin["obligationId"]!.GetValue<string>()));
        Assert.Empty(pinned["sources"]!.AsArray());

        // Everything the export read changes afterwards.
        Assert.Equal(HttpStatusCode.OK, (await s.Owner.PatchAsJsonAsync($"/api/projects/{orders.ProjectId}", new { name = "Renamed Project" })).StatusCode);
        var scenario = orders.WithOutbox();
        scenario["name"] = "Place an order, renamed";
        Assert.Equal(HttpStatusCode.OK, (await s.Owner.PutAsync(orders.Url($"scenarios/{orders.ScenarioId}"), OrdersReference.Json(scenario))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await s.Owner.PatchAsJsonAsync(orders.Url($"schemas/{orders.OrderRequestSchema}"), new { description = "The body of POST /orders.", setDescription = true })).StatusCode);
        await TestHost.Created(s.Owner.PostAsJsonAsync(orders.Url("elements"), new { id = Guid.NewGuid(), kind = "system", name = "Added later" }));
        await orders.RunAsync(s.Ord014, "O1");
        await Ok(s.Owner.PostAsync(s.Promises($"/{s.Ord014}/revise"), null));
        var later = (await Ok(s.Owner.PostAsync(s.Promises(), OrdersReference.Json(new JsonObject { ["title"] = "Written later", ["claim"] = "It holds." })), HttpStatusCode.Created))["id"]!.GetValue<Guid>();

        // The export of now is another bundle.
        Assert.NotEqual(zip, await ExportAsync(s, new { format = "bundle" }));

        // The old bundle is still exactly what its pinned versions generate: nothing was tampered with. The project moved on.
        var verified = await Ok(VerifyZipAsync(s.Owner, s.Build("verify"), zip));
        Assert.Equal((true, true, "match", "match", false), (
            verified["identical"]!.GetValue<bool>(), verified["matchesPinned"]!.GetValue<bool>(), verified["manifest"]!.GetValue<string>(),
            verified["archive"]!.GetValue<string>(), verified["current"]!.GetValue<bool>()));
        var moved = verified["moved"]!.AsArray().Select(m => (Kind: m!["kind"]!.GetValue<string>(), Id: m["id"]!.GetValue<string>(), Pinned: m["pinned"]?.GetValue<string>(), Current: m["current"]?.GetValue<string>())).ToList();
        Assert.Equal(["model", "project-name", "promise", "promise", "run", "scenario", "schema"], moved.Select(m => m.Kind).Order(StringComparer.Ordinal));
        Assert.Contains(("project-name", orders.ProjectId.ToString(), "Test Project", "Renamed Project"), moved);
        Assert.Contains(("scenario", orders.ScenarioId.ToString(), "2", "3"), moved);
        Assert.Contains(moved, m => m is { Kind: "promise", Pinned: null } && m.Id == later.ToString());
        Assert.Contains(moved, m => m.Kind == "run" && m.Id == $"{s.Ord014}:1:ORD-014/O1" && m.Pinned != m.Current);

        // An export of the pinned state gives the first zip again, byte for byte.
        Assert.Equal(zip, await ExportAsync(s, new { format = "bundle", pinned }));
        Assert.Contains("# Test Project: Software Design Document", Encoding.UTF8.GetString(await ExportAsync(s, new { format = "sdd", pinned })));

        // A scenario that was deleted later is still read at its pinned version.
        Assert.Equal(HttpStatusCode.NoContent, (await s.Owner.DeleteAsync(orders.Url($"scenarios/{orders.ScenarioId}"))).StatusCode);
        var afterDelete = await Ok(VerifyZipAsync(s.Owner, s.Build("verify"), zip));
        Assert.True(afterDelete["matchesPinned"]!.GetValue<bool>());
        Assert.Contains(afterDelete["moved"]!.AsArray(), m => m!["kind"]!.GetValue<string>() == "scenario" && m["current"] is null);
        Assert.Equal(zip, await ExportAsync(s, new { format = "bundle", pinned }));

        // A pinned state that names a version that never existed cannot be generated: it is not "different", it is unavailable.
        var never = manifest.DeepClone().AsObject();
        never["pinned"]!["scenarios"]![0]!["version"] = 999;
        var unavailable = await Ok(VerifyManifestAsync(s, never), HttpStatusCode.Conflict);
        Assert.Equal("pinned_input_unavailable", unavailable["type"]!.GetValue<string>());
        Assert.Contains($"scenario {orders.ScenarioId} at version 999", unavailable["errors"]![0]!["message"]!.GetValue<string>());

        // A manifest that does not pin every input (the format before this one) cannot be generated again, and says so.
        var old = manifest.DeepClone().AsObject();
        foreach (var name in new[] { "projectName", "scenarios", "schemas", "promises", "runs", "sources" })
            old["pinned"]!.AsObject().Remove(name);
        Assert.Equal("invalid_bundle", (await Ok(VerifyManifestAsync(s, old), HttpStatusCode.BadRequest))["type"]!.GetValue<string>());
        // A pinned state and a revision together are refused.
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Owner.PostAsJsonAsync(s.Build("export"), new { format = "bundle", pinned, branchId = Guid.NewGuid() })).StatusCode);
    }

    [Fact]
    public async Task Only_an_approved_promise_in_force_binds()
    {
        var factory = fixture.Factory;
        var s = await SeedAsync(factory, outbox: false);
        var orders = s.Orders;

        JsonObject Body(string title, string? origin = null) => new()
        {
            ["title"] = title, ["claim"] = "It holds.", ["origin"] = origin,
            ["preconditions"] = "None.", ["expectedOutcome"] = "It holds.", ["forbiddenOutcome"] = $"{title} does not hold.", ["evidenceCriteria"] = "A review.",
            ["forbiddenEvents"] = new JsonArray(new JsonObject { ["kind"] = "DataWriteFailed", ["target"] = OrdersReference.OrdersDb.ToString() }),
            ["links"] = new JsonObject { ["elementIds"] = new JsonArray(OrdersReference.OrdersApi.ToString()), ["scenarioIds"] = new JsonArray(orders.ScenarioId.ToString()) },
        };
        async Task<Guid> CreateAsync(string title, string? origin = null)
        {
            var id = (await Ok(s.Owner.PostAsync(s.Promises(), OrdersReference.Json(Body(title, origin))), HttpStatusCode.Created))["id"]!.GetValue<Guid>();
            await Ok(s.Owner.PostAsync(s.Promises($"/{id}/obligations"), OrdersReference.Json(new JsonObject
            {
                ["kind"] = "manual-review", ["title"] = $"Review of {title}", ["recipe"] = new JsonObject { ["text"] = "Read the code." },
            })), HttpStatusCode.Created);
            return id;
        }

        // ORD-015: suggested by an AI, never approved. ORD-016: approved, then retired. ORD-017: approved, with a newer draft beside it.
        var draft = await CreateAsync("A suggested draft", "ai-suggested");
        var retired = await CreateAsync("A retired promise");
        await orders.ApproveAsync(retired);
        await Ok(s.Owner.PostAsync(s.Promises($"/{retired}/revise"), OrdersReference.Json(new JsonObject { ["retire"] = true })));
        Assert.Equal("retired", (await orders.ApproveAsync(retired, 2))["state"]!.GetValue<string>());
        var revised = await CreateAsync("The text in force");
        await orders.ApproveAsync(revised);
        await Ok(s.Owner.PostAsync(s.Promises($"/{revised}/revise"), null));
        await Ok(s.Owner.PutAsync(s.Promises($"/{revised}"), OrdersReference.Json(Body("The text of the draft"))));

        var entries = Unzip(await ExportAsync(s, new { format = "bundle" })).ToDictionary(e => e.Path, e => e.Bytes);
        var sdd = Encoding.UTF8.GetString(entries["SDD.md"]);
        string Section(string title, string next) => sdd[sdd.IndexOf($"\n## {title}\n", StringComparison.Ordinal)..sdd.IndexOf($"\n## {next}\n", StringComparison.Ordinal)];

        // Requirements: a heading of its own for each approved promise in force, with the text in force.
        var promises = Section("8. Promises", "9. Failure invariants");
        Assert.Equal(
            ["### ORD-014: An accepted order must survive a broker outage", "### ORD-017: The text in force",
                "### Not approved: do not implement as requirements", "### Proposed revisions that are not in force", "### Retired"],
            promises.Split('\n').Where(l => l.StartsWith("### ", StringComparison.Ordinal)));
        Assert.DoesNotContain("The text of the draft", sdd);
        var apart = promises[promises.IndexOf("### Not approved", StringComparison.Ordinal)..];
        Assert.Contains("| `ORD-015` | A suggested draft | ai-suggested |", apart);
        Assert.Contains("| `ORD-017` | 2 | revision 1 |", apart);
        Assert.Contains("| `ORD-016` | A retired promise |", apart);
        // Nothing of the draft or the retired promise is anywhere before those lists, and nowhere after section 8 but in the blockers.
        Assert.DoesNotContain("ORD-015", sdd[..sdd.IndexOf("### Not approved", StringComparison.Ordinal)]);
        Assert.DoesNotContain("ORD-016", sdd[..sdd.IndexOf("### Not approved", StringComparison.Ordinal)]);
        var rest = Section("9. Failure invariants", "12. Unresolved blockers");
        Assert.DoesNotContain("ORD-015", rest);
        Assert.DoesNotContain("ORD-016", rest);

        // Slices, failure invariants and acceptance tests speak of the two requirements only.
        Assert.Contains("- Promises that bind it: `ORD-014`, `ORD-017`\n", Section("10. Implementation slices", "11. Acceptance tests"));
        var invariants = Section("9. Failure invariants", "10. Implementation slices");
        Assert.Equal(["ORD-014", "ORD-017"], Regex.Matches(invariants, @"^\| `(ORD-\d+)` \|", RegexOptions.Multiline).Select(m => m.Groups[1].Value).Distinct());
        var checklist = Section("11. Acceptance tests", "12. Unresolved blockers").Split('\n').Where(l => l.StartsWith("- [ ] `ORD-", StringComparison.Ordinal)).ToList();
        Assert.Equal(["ORD-014/O1", "ORD-014/O2", "ORD-014/O3", "ORD-014/O4", "ORD-017/O1"], checklist.Select(l => l.Split('`')[1]));

        // Files: a requirement is at promises/. What binds nothing is in a directory that says so.
        Assert.Equal(
            ["promises-not-approved/ORD-015.json", "promises-retired/ORD-016.json", "promises/ORD-014.json", "promises/ORD-017.json"],
            entries.Keys.Where(k => k.StartsWith("promises", StringComparison.Ordinal)).Order(StringComparer.Ordinal));
        var architecture = JsonNode.Parse(entries["architecture.json"])!;
        static string[] Ids(JsonNode list) => [.. list.AsArray().Select(p => p!["requirementId"]!.GetValue<string>())];
        Assert.Equal(["ORD-014", "ORD-017"], Ids(architecture["promises"]!));
        Assert.Equal(["ORD-015"], Ids(architecture["promisesNotApproved"]!));
        Assert.Equal(["ORD-016"], Ids(architecture["promisesRetired"]!));
        var requirement = architecture["promises"]![1]!;
        Assert.Equal((true, 1, "The text in force"), (requirement["binding"]!.GetValue<bool>(), requirement["requirement"]!["revision"]!.GetValue<int>(), requirement["requirement"]!["title"]!.GetValue<string>()));
        Assert.Equal((false, (JsonNode?)null), (architecture["promisesNotApproved"]![0]!["binding"]!.GetValue<bool>(), architecture["promisesNotApproved"]![0]!["requirement"]));

        // The brief of the element that all four link: two requirements, one promise named as not approved, and no retired one.
        var brief = await Ok(s.Owner.PostAsJsonAsync(s.Build("brief"), new { elementIds = new[] { OrdersReference.OrdersApi } }));
        Assert.Equal(["ORD-014", "ORD-017"], Text(brief["manifest"]!["included"]!["requirementIds"]!));
        Assert.Equal(["ORD-015"], Text(brief["manifest"]!["notApproved"]!));
        var text = brief["brief"]!.GetValue<string>();
        Assert.DoesNotContain("ORD-016", text);
        Assert.DoesNotContain("The text of the draft", text);
        Assert.DoesNotContain("ORD-015", text[..text.IndexOf("## 6. Not approved", StringComparison.Ordinal)]);

        // A retired promise that is selected by itself is named as retired. It is not expanded and binds nothing.
        var byRetired = await Ok(s.Owner.PostAsJsonAsync(s.Build("brief"), new { promiseIds = new[] { retired } }));
        Assert.Equal(["ORD-016"], Text(byRetired["manifest"]!["retired"]!));
        Assert.Empty(byRetired["manifest"]!["included"]!["requirementIds"]!.AsArray());
        Assert.Empty(byRetired["manifest"]!["notApproved"]!.AsArray());
        Assert.Empty(byRetired["manifest"]!["included"]!["elementIds"]!.AsArray());
        Assert.Contains("Selected promises that are retired. They bind nothing:", byRetired["brief"]!.GetValue<string>());

        // The model and the scenario agree about who produces OrderCreated here.
        var manifest = JsonNode.Parse(entries["manifest.json"])!;
        Assert.DoesNotContain(manifest["blockers"]!.AsArray(), b => b!["code"]!.GetValue<string>() == "producer-mismatch");
    }

    [Fact]
    public async Task The_sdd_states_failure_invariants_scenario_versions_and_every_contract_and_blocks_on_stale_evidence()
    {
        var factory = fixture.Factory;
        var s = await SeedAsync(factory);
        var orders = s.Orders;
        var entries = Unzip(await ExportAsync(s, new { format = "bundle" })).ToDictionary(e => e.Path, e => e.Bytes);
        var sdd = Encoding.UTF8.GetString(entries["SDD.md"]);
        var manifest = JsonNode.Parse(entries["manifest.json"])!;

        // Failure invariants, by requirement ID: the forbidden outcome, and the forbidden event with the step it constrains.
        Assert.Contains("\n## 9. Failure invariants\n", sdd);
        Assert.Contains($"| `ORD-014` | outcome (data): The request reports success and OrderCreated is lost. | Place an order (`{orders.ScenarioId}`) | not derived from text |", sdd);
        Assert.Contains($"| `ORD-014` | event Dropped anywhere | Place an order (`{orders.ScenarioId}`) | `{orders.ScenarioId}` / `{OrdersReference.PublishStep}` |", sdd);

        // Each scenario with the version and the content hash that the export read.
        Assert.Matches($@"### Place an order \(`{orders.ScenarioId}`\)\n\nFile: `scenarios/{orders.ScenarioId}\.json`\.\nScenario version 2, content hash `[0-9a-f]{{64}}`\.\n", sdd);

        // An operation that is not HTTP has no OpenAPI document. Its contract is in the SDD and in architecture.json.
        Assert.Contains("### Operations without an OpenAPI document", sdd);
        Assert.Contains("No OpenAPI document applies to these operations", sdd);
        Assert.Contains($"| `{OrdersReference.RelayOutbox}` | Outbox Relay (`{OrdersReference.Relay}`) | Relay outbox | command | none | none | none | 3 attempts, 1000 ms backoff | The transport is command. OpenAPI describes HTTP operations only. | `{orders.ScenarioId}` / `relay` |", sdd);
        var contract = Assert.Single(JsonNode.Parse(entries["architecture.json"])!["operationContracts"]!.AsArray())!;
        Assert.Equal((OrdersReference.RelayOutbox, "command", 3, (JsonNode?)null, "relay"), (
            contract["id"]!.GetValue<Guid>(), contract["transport"]!.GetValue<string>(), contract["retry"]!["maxAttempts"]!.GetValue<int>(),
            contract["openApi"], contract["calledBy"]![0]!["stepId"]!.GetValue<string>()));
        Assert.Contains("OpenAPI describes HTTP operations only", contract["noOpenApiBecause"]!.GetValue<string>());

        // OpenAPI: the response class a scenario shows, and default. The scenario format states no status number; none is invented.
        var openApi = Encoding.UTF8.GetString(entries[$"openapi/{OrdersReference.OrdersApi}.yaml"]);
        var yaml = new YamlDotNet.Serialization.DeserializerBuilder().Build().Deserialize<Dictionary<object, object>>(openApi);
        var responses = (Dictionary<object, object>)((Dictionary<object, object>)((Dictionary<object, object>)((Dictionary<object, object>)yaml["paths"])["/orders"])["post"])["responses"];
        Assert.Equal(["2XX", "default"], responses.Keys.Select(k => k.ToString()));
        Assert.Equal([$"{orders.ScenarioId}/place"], ((List<object>)((Dictionary<object, object>)responses["2XX"])["x-vellum-shown-by"]).Select(v => v.ToString()));
        Assert.DoesNotMatch(@"(?m)^\s+['""]?[1-5]\d\d['""]?:", openApi);

        // The model says the Orders API produces OrderCreated; the scenario publishes it from the relay.
        var mismatch = manifest["blockers"]!.AsArray().Single(b => b!["code"]!.GetValue<string>() == "producer-mismatch")!;
        Assert.Equal(OrdersReference.OrderCreated.ToString(), mismatch["subject"]!.GetValue<string>());
        Assert.Contains($"publishes it from 'Outbox Relay' ({OrdersReference.Relay})", mismatch["message"]!.GetValue<string>());

        // The check ran on this model version: its result is current, and the missing results of the plans block.
        Assert.Contains("Obligations: 1 passed, 0 failed, 0 stale, 3 not run, 0 unsupported.", sdd);
        var missing = manifest["blockers"]!.AsArray().Single(b => b!["code"]!.GetValue<string>() == "promise-evidence-stale")!;
        Assert.DoesNotContain("(stale)", missing["message"]!.GetValue<string>());

        // The model moves on. The result of the check is for the old version: stale, not passed, and it blocks.
        await TestHost.Created(s.Owner.PostAsJsonAsync(orders.Url("elements"), new { id = Guid.NewGuid(), kind = "system", name = "Added later" }));
        var after = (await s.Owner.GetFromJsonAsync<JsonObject>(s.Build("manifest")))!;
        var stale = after["blockers"]!.AsArray().Single(b => b!["code"]!.GetValue<string>() == "promise-evidence-stale")!;
        Assert.Equal("ORD-014", stale["subject"]!.GetValue<string>());
        Assert.Contains("the newest result of ORD-014/O1 is for inputs that are not the pinned ones (stale)", stale["message"]!.GetValue<string>());
        Assert.False(after["buildReady"]!.GetValue<bool>());
        var later = Encoding.UTF8.GetString(await ExportAsync(s, new { format = "sdd" }));
        Assert.Contains("Obligations: 0 passed, 0 failed, 1 stale, 3 not run, 0 unsupported.", later);
        Assert.Contains("| `ORD-014/O1` | scenario-check | The order flow holds while the broker is down | passed (stale) |", later);
    }
}
