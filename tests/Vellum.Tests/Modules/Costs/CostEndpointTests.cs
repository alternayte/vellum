using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vellum.Modules.Costs;
using Vellum.Modules.Drafts;
using Vellum.Shared;

namespace Vellum.Tests.Modules.Costs;

/// <summary>
/// Cost sheets, the price book, saved estimates and comparison through the HTTP API on real
/// Postgres: who may do what, what one workspace sees of another, and what a deleted project leaves.
/// </summary>
public class CostEndpointTests(IntegrationFixture fixture) : IClassFixture<IntegrationFixture>
{
    private const string AsOf = "2026-10-13";

    private static string Costs(Guid projectId, string path = "", Guid? branchId = null) =>
        $"/api/projects/{projectId}/costs{path}" + (branchId is null ? "" : $"{(path.Contains('?') ? '&' : '?')}branchId={branchId}");

    private static string Prices(Guid workspaceId, string path = "") => $"/api/workspaces/{workspaceId}/prices{path}";

    private static async Task<JsonNode> Ok(Task<HttpResponseMessage> request, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = await request;
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode}: {text}");
        return JsonNode.Parse(text)!;
    }

    private static async Task Status(HttpStatusCode expected, Task<HttpResponseMessage> request, string what)
    {
        var response = await request;
        Assert.True(response.StatusCode == expected, $"{what}: expected {expected}, got {response.StatusCode} {await response.Content.ReadAsStringAsync()}");
    }

    private static async Task<Guid> ElementAsync(HttpClient client, Guid projectId, string name = "Orders API")
    {
        var id = Guid.NewGuid();
        await TestHost.Created(client.PostAsJsonAsync($"/api/projects/{projectId}/elements", new { id, kind = "system", name }));
        return id;
    }

    private static async Task<Guid> PriceAsync(
        HttpClient owner, Guid workspaceId, decimal unitPrice = 0.5m, string unit = "hour", string currency = "USD", string sku = "small")
    {
        var entry = await Ok(owner.PostAsJsonAsync(Prices(workspaceId),
            new { provider = "custom", region = "local", sku, unit, unitPrice, currency, effectiveDate = "2026-01-01" }), HttpStatusCode.Created);
        return entry["id"]!.GetValue<Guid>();
    }

    private static object Resource(Guid elementId, string name, decimal? quantity, Guid? priceRef, object[]? usage = null) => new
    {
        elementId, name, category = "compute", provider = "custom", region = "local", sku = "small",
        quantity, unit = quantity is null ? null : "hours", priceRef, usage,
    };

    private static object Assumption(string name = "Requests", decimal value = 1000m) => new
    {
        name, kind = "requests", value, unit = "requests/month", label = "declared", effectiveDate = "2026-01-01",
    };

    [Fact]
    public async Task A_sheet_gives_a_live_estimate_and_a_saved_estimate_says_what_changed_after_it()
    {
        var factory = fixture.Factory;
        using var owner = await TestHost.SignInAsync(factory);
        var (workspaceId, projectId) = await TestHost.CreateProjectAsync(owner);
        var element = await ElementAsync(owner, projectId);
        var hourly = await PriceAsync(owner, workspaceId);
        var perRequest = await PriceAsync(owner, workspaceId, 2m, "1000 requests", sku: "requests");

        // A project with no sheet has an empty estimate, not an error.
        var empty = await Ok(owner.GetAsync(Costs(projectId, $"/estimate?asOf={AsOf}")));
        Assert.Empty(empty["estimate"]!["lines"]!.AsArray());
        Assert.Equal(0, (await Ok(owner.GetAsync(Costs(projectId))))["version"]!.GetValue<long>());

        var withAssumption = await Ok(owner.PostAsJsonAsync(Costs(projectId, "/assumptions"), Assumption()), HttpStatusCode.Created);
        var assumptionId = withAssumption["assumptions"]![0]!["id"]!.GetValue<Guid>();
        var sheet = await Ok(owner.PostAsJsonAsync(Costs(projectId, "/resources"),
            Resource(element, "API", 100, hourly, [new { name = "Requests", assumptionId, priceRef = perRequest }])), HttpStatusCode.Created);
        await Ok(owner.PostAsJsonAsync(Costs(projectId, "/resources"), Resource(element, "Database", 10, null)), HttpStatusCode.Created);
        var api = sheet["resources"]![0]!;
        Assert.Equal(("Orders API", "declared"), (api["elementName"]!.GetValue<string>(), api["factStatus"]!.GetValue<string>()));

        // 100 hours at 0.5 and 1000 requests at 2 for a thousand. The database has no price.
        var live = await Ok(owner.GetAsync(Costs(projectId, $"/estimate?asOf={AsOf}")));
        var estimate = live["estimate"]!;
        var total = Assert.Single(estimate["totals"]!.AsArray())!;
        Assert.Equal(("USD", 52m), (total["currency"]!.GetValue<string>(), total["amount"]!.GetValue<decimal>()));
        var unknown = Assert.Single(estimate["unknown"]!.AsArray())!;
        Assert.Equal(("Database", "no-price"), (unknown["resourceName"]!.GetValue<string>(), unknown["reason"]!.GetValue<string>()));
        Assert.Equal(64, live["hash"]!.GetValue<string>().Length);
        Assert.Equal(live.ToJsonString(), (await Ok(owner.GetAsync(Costs(projectId, $"/estimate?asOf={AsOf}")))).ToJsonString());

        // A save stores the estimate as it is. The stored result keeps the hash of the live one.
        var saved = await Ok(owner.PostAsJsonAsync(Costs(projectId, "/estimates"), new { name = "Baseline", asOf = AsOf }), HttpStatusCode.Created);
        Assert.Equal((live["hash"]!.GetValue<string>(), live["inputsFingerprint"]!.GetValue<string>(), false),
            (saved["hash"]!.GetValue<string>(), saved["inputsFingerprint"]!.GetValue<string>(), saved["outdated"]!.GetValue<bool>()));
        Assert.Equal(live["modelVersion"]!.GetValue<long>(), saved["modelVersion"]!.GetValue<long>());
        var estimateUrl = Costs(projectId, $"/estimates/{saved["id"]}");
        var stored = CostJson.Read<Estimate>((await Ok(owner.GetAsync(estimateUrl)))["result"]!.ToJsonString());
        Assert.Equal(saved["hash"]!.GetValue<string>(), CostJson.Hash(stored));

        // The inputs change: a quantity, a new resource, a removed assumption.
        var apiId = api["id"]!.GetValue<Guid>();
        await Ok(owner.PutAsJsonAsync(Costs(projectId, $"/resources/{apiId}"),
            Resource(element, "API", 200, hourly, [new { name = "Requests", assumptionId, priceRef = perRequest }])));
        await Ok(owner.PostAsJsonAsync(Costs(projectId, "/resources"), Resource(element, "Cache", 1, hourly)), HttpStatusCode.Created);
        await Ok(owner.DeleteAsync(Costs(projectId, $"/assumptions/{assumptionId}")));

        var outdated = await Ok(owner.GetAsync(estimateUrl));
        Assert.True(outdated["outdated"]!.GetValue<bool>());
        Assert.Equal(
            [("assumption", "Requests", "removed"), ("resource", "API", "changed"), ("resource", "Cache", "added")],
            outdated["changes"]!.AsArray()
                .Select(c => (c!["kind"]!.GetValue<string>(), c["name"]!.GetValue<string>(), c["change"]!.GetValue<string>()))
                .OrderBy(c => c.Item1, StringComparer.Ordinal).ThenBy(c => c.Item2, StringComparer.Ordinal));
        // The stored result is what it was.
        Assert.Equal(saved["hash"]!.GetValue<string>(), outdated["hash"]!.GetValue<string>());
        Assert.Equal(saved["result"]!.ToJsonString(), outdated["result"]!.ToJsonString());
        var listed = Assert.Single((await Ok(owner.GetAsync(Costs(projectId, "/estimates")))).AsArray())!;
        Assert.True(listed["outdated"]!.GetValue<bool>());
        Assert.Null(listed["result"]);

        // The usage line that named the removed assumption is now unknown.
        var after = (await Ok(owner.GetAsync(Costs(projectId, $"/estimate?asOf={AsOf}"))))["estimate"]!;
        Assert.Contains(after["unknown"]!.AsArray(), u => u!["reason"]!.GetValue<string>() == "missing-assumption");
    }

    [Fact]
    public async Task A_resource_points_only_at_things_of_its_own_branch_and_workspace()
    {
        var factory = fixture.Factory;
        using var owner = await TestHost.SignInAsync(factory);
        var (workspaceId, projectId) = await TestHost.CreateProjectAsync(owner);
        var element = await ElementAsync(owner, projectId);
        var price = await PriceAsync(owner, workspaceId);

        using var other = await TestHost.SignInAsync(factory);
        var (otherWorkspace, otherProject) = await TestHost.CreateProjectAsync(other);
        var foreignPrice = await PriceAsync(other, otherWorkspace);
        var foreignElement = await ElementAsync(other, otherProject);

        async Task Refused(object body, string field)
        {
            var error = await Ok(owner.PostAsJsonAsync(Costs(projectId, "/resources"), body), HttpStatusCode.BadRequest);
            Assert.Contains(error["errors"]!.AsArray(), e => e!["field"]!.GetValue<string>() == field);
        }

        await Refused(Resource(foreignElement, "API", 1, price), "elementId");
        await Refused(Resource(element, "API", 1, foreignPrice), "priceRef");
        await Refused(Resource(element, "API", 1, price, [new { name = "Requests", assumptionId = Guid.NewGuid(), priceRef = price }]), "usage[0].assumptionId");
        await Refused(Resource(element, "", 1, price), "name");
        await Refused(Resource(element, new string('n', 201), 1, price), "name");
        await Refused(new { elementId = element, name = "API", category = "people", provider = "custom", quantity = 1, unit = "hours" }, "category");
        await Refused(new { elementId = element, name = "API", category = "compute", provider = "custom", quantity = 1, unit = "10K" }, "unit");
        await Refused(new { elementId = element, name = "API", category = "compute", provider = "custom", quantity = -1, unit = "hours" }, "quantity");
        await Refused(new { elementId = element, name = "API", category = "compute", provider = "custom", quantity = 1, unit = "hours", factStatus = "inferred" }, "provenance");

        await Ok(owner.PostAsJsonAsync(Costs(projectId, "/resources"), Resource(element, "API", 1, price)), HttpStatusCode.Created);
        await Status(HttpStatusCode.Conflict, owner.PostAsJsonAsync(Costs(projectId, "/resources"), Resource(element, "API", 2, price)), "same name on one element");

        var badAssumption = await Ok(owner.PostAsJsonAsync(Costs(projectId, "/assumptions"),
            new { name = "Traffic", kind = "requests", value = 5, unit = "requests/month", label = "measured", effectiveDate = "2026-01-01" }), HttpStatusCode.BadRequest);
        Assert.Contains(badAssumption["errors"]!.AsArray(), e => e!["field"]!.GetValue<string>() == "source");

    }

    [Fact]
    public async Task Two_variants_are_compared_and_an_unknown_line_blocks_the_verdict()
    {
        var factory = fixture.Factory;
        using var owner = await TestHost.SignInAsync(factory);
        var (workspaceId, projectId) = await TestHost.CreateProjectAsync(owner);
        var element = await ElementAsync(owner, projectId);
        var price = await PriceAsync(owner, workspaceId);
        await Ok(owner.PostAsJsonAsync(Costs(projectId, "/resources"), Resource(element, "API", 100, price)), HttpStatusCode.Created);

        var variant = (await Ok(owner.PostAsJsonAsync($"/api/projects/{projectId}/variants", new { id = Guid.NewGuid(), name = "smaller" }), HttpStatusCode.Created))["streamId"]!.GetValue<Guid>();

        // A new variant starts with a copy of the sheet of main, made in the transaction that makes the variant.
        var api = Assert.Single((await Ok(owner.GetAsync(Costs(projectId, "", variant))))["resources"]!.AsArray())!;
        await Ok(owner.PutAsJsonAsync(Costs(projectId, $"/resources/{api["id"]}", variant), Resource(element, "API", 60, price)));

        var compare = Costs(projectId, $"/compare?left=main&right={variant}&asOf={AsOf}");
        var comparison = (await Ok(owner.GetAsync(compare)))["comparison"]!;
        Assert.Equal((true, "right", 20m),
            (comparison["comparable"]!.GetValue<bool>(), comparison["cheaper"]!.GetValue<string>(), comparison["difference"]!["amount"]!.GetValue<decimal>()));
        Assert.Equal(("API", "changed", -20m), (
            comparison["resources"]![0]!["name"]!.GetValue<string>(), comparison["resources"]![0]!["change"]!.GetValue<string>(),
            comparison["resources"]![0]!["amounts"]![0]!["delta"]!.GetValue<decimal>()));

        // The variant gets a resource with no price. By its known lines it is still the cheaper side; Vellum does not say so.
        await Ok(owner.PostAsJsonAsync(Costs(projectId, "/resources", variant), Resource(element, "Queue", 5, null)), HttpStatusCode.Created);
        var blocked = (await Ok(owner.GetAsync(compare)))["comparison"]!;
        Assert.Equal((false, "right-has-unknown-lines"), (blocked["comparable"]!.GetValue<bool>(), blocked["reason"]!.GetValue<string>()));
        Assert.Null(blocked["cheaper"]);
        Assert.Null(blocked["difference"]);
        Assert.Null(blocked["totals"]![0]!["delta"]);
        Assert.Equal("Queue", Assert.Single(blocked["rightUnknown"]!.AsArray())!["resourceName"]!.GetValue<string>());
        Assert.Empty(blocked["leftUnknown"]!.AsArray());

        // main is untouched by the variant's changes.
        Assert.Equal(100m, (await Ok(owner.GetAsync(Costs(projectId))))["resources"]![0]!["quantity"]!.GetValue<decimal>());

        await Status(HttpStatusCode.BadRequest, owner.GetAsync(Costs(projectId, $"/compare?left=main&right={Guid.NewGuid()}")), "unknown branch");
        await Status(HttpStatusCode.BadRequest, owner.GetAsync(Costs(projectId, "/compare?left=main&right=nonsense")), "not a branch");
    }

    [Fact]
    public async Task A_request_without_its_number_is_refused_with_the_field_and_is_not_read_as_zero()
    {
        var factory = fixture.Factory;
        using var owner = await TestHost.SignInAsync(factory);
        var (workspaceId, projectId) = await TestHost.CreateProjectAsync(owner);

        var assumption = await Ok(owner.PostAsJsonAsync(Costs(projectId, "/assumptions"),
            new { name = "Requests", kind = "requests", unit = "requests/month", label = "declared", effectiveDate = "2026-01-01" }), HttpStatusCode.BadRequest);
        Assert.Equal("value", Assert.Single(assumption["errors"]!.AsArray())!["field"]!.GetValue<string>());
        Assert.Empty((await Ok(owner.GetAsync(Costs(projectId))))["assumptions"]!.AsArray());

        var price = await Ok(owner.PostAsJsonAsync(Prices(workspaceId),
            new { provider = "custom", sku = "small", unit = "hour", currency = "USD", effectiveDate = "2026-01-01" }), HttpStatusCode.BadRequest);
        Assert.Equal("unitPrice", Assert.Single(price["errors"]!.AsArray())!["field"]!.GetValue<string>());
        Assert.Empty((await Ok(owner.GetAsync(Prices(workspaceId))))["items"]!.AsArray());
    }

    [Fact]
    public async Task Adding_and_deleting_a_price_writes_an_audit_entry_with_the_actor_and_the_entry()
    {
        var factory = fixture.Factory;
        using var owner = await TestHost.SignInAsync(factory);
        var (workspaceId, _) = await TestHost.CreateProjectAsync(owner);

        var price = await PriceAsync(owner, workspaceId, 0.25m);
        await Status(HttpStatusCode.NoContent, owner.DeleteAsync(Prices(workspaceId, $"/{price}")), "delete price");

        var audit = (await Ok(owner.GetAsync($"/api/workspaces/{workspaceId}/audit"))).AsArray()
            .Where(a => a!["action"]!.GetValue<string>().StartsWith("price.", StringComparison.Ordinal)).ToList();
        // Newest first.
        Assert.Equal(["price.deleted", "price.added"], audit.Select(a => a!["action"]!.GetValue<string>()));
        Assert.All(audit, a => Assert.Equal($"price:{price} custom sku:small region:local 0.25 USD per hour", a!["target"]!.GetValue<string>()));
        Assert.All(audit, a => Assert.True(Guid.TryParse(a!["actorId"]!.GetValue<string>(), out _)));
    }

    [Fact]
    public async Task A_merge_takes_the_cost_sheet_of_the_draft_to_main_or_is_refused_and_a_closed_draft_keeps_no_cost_data()
    {
        var factory = fixture.Factory;
        using var owner = await TestHost.SignInAsync(factory);
        var (workspaceId, projectId) = await TestHost.CreateProjectAsync(owner);
        var element = await ElementAsync(owner, projectId);
        var price = await PriceAsync(owner, workspaceId);
        var sheet = await Ok(owner.PostAsJsonAsync(Costs(projectId, "/resources"), Resource(element, "API", 100, price)), HttpStatusCode.Created);
        var api = sheet["resources"]![0]!["id"]!.GetValue<Guid>();
        sheet = await Ok(owner.PostAsJsonAsync(Costs(projectId, "/resources"), Resource(element, "Worker", 10, price)), HttpStatusCode.Created);
        var worker = sheet["resources"]!.AsArray().Single(r => r!["name"]!.GetValue<string>() == "Worker")!["id"]!.GetValue<Guid>();

        async Task<DraftDto> DraftAsync(string name)
        {
            var draft = (await (await TestHost.Created(owner.PostAsJsonAsync($"/api/projects/{projectId}/drafts", new { id = Guid.NewGuid(), name })))
                .Content.ReadFromJsonAsync<DraftDto>())!;
            await Ok(owner.PostAsJsonAsync(Costs(projectId, "/copy", draft.StreamId), new { from = "main" }));
            return draft;
        }

        Task<JsonNode> PreviewAsync(DraftDto draft) => Ok(owner.PostAsync($"/api/projects/{projectId}/drafts/{draft.Id}/merge/preview", null));

        async Task<HttpResponseMessage> MergeAsync(DraftDto draft) => await owner.PostAsJsonAsync(
            $"/api/projects/{projectId}/drafts/{draft.Id}/merge/execute",
            new { resolutions = Array.Empty<object>(), expectedMainVersion = (await PreviewAsync(draft))["mainVersion"]!.GetValue<int>() });

        // Sheet: the row of the draft's sheet. Readable: the sheet that the app reads for the draft holds anything.
        async Task<(bool Sheet, bool Estimates, bool Readable)> CostDataAsync(DraftDto draft)
        {
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var (read, version) = await CostAccess.LoadSheetAsync(db, draft.StreamId, default);
            return (await db.Sheets.AnyAsync(s => s.BranchId == draft.StreamId), await db.Estimates.AnyAsync(e => e.BranchId == draft.StreamId),
                version > 0 || read.Resources.Count > 0 || read.Assumptions.Count > 0);
        }

        // The draft adds an element with a resource and changes the API. Main changes the worker.
        var first = await DraftAsync("Add auth");
        var auth = Guid.NewGuid();
        await TestHost.Created(owner.PostAsJsonAsync($"/api/projects/{projectId}/elements?branchId={first.StreamId}", new { id = auth, kind = "system", name = "Auth" }));
        await Ok(owner.PostAsJsonAsync(Costs(projectId, "/resources", first.StreamId), Resource(auth, "Auth instances", 5, price)), HttpStatusCode.Created);
        await Ok(owner.PutAsJsonAsync(Costs(projectId, $"/resources/{api}", first.StreamId), Resource(element, "API", 60, price)));
        await Ok(owner.PostAsJsonAsync(Costs(projectId, "/estimates", first.StreamId), new { name = "On the draft" }), HttpStatusCode.Created);
        await Ok(owner.PutAsJsonAsync(Costs(projectId, $"/resources/{worker}"), Resource(element, "Worker", 20, price)));
        Assert.Equal((true, true, true), await CostDataAsync(first));

        var costSheet = (await PreviewAsync(first))["costSheet"]!;
        Assert.Equal((2, 0), (costSheet["changes"]!.GetValue<int>(), costSheet["conflicts"]!.AsArray().Count));
        await Status(HttpStatusCode.OK, MergeAsync(first), "merge");

        // Main holds the draft's changes and its own. Nothing of the draft's sheet was dropped.
        var merged = (await Ok(owner.GetAsync(Costs(projectId))))["resources"]!.AsArray();
        Assert.Equal(
            [("API", 60m, "Orders API"), ("Auth instances", 5m, "Auth"), ("Worker", 20m, "Orders API")],
            merged.Select(r => (r!["name"]!.GetValue<string>(), r["quantity"]!.GetValue<decimal>(), r["elementName"]!.GetValue<string>()))
                .OrderBy(r => r.Item1, StringComparer.Ordinal));
        // The closed draft keeps no sheet, no stream and no saved estimate.
        Assert.Equal((false, false, false), await CostDataAsync(first));

        // Both sides change the API in different ways. The merge is refused, with the reason, and writes nothing.
        var second = await DraftAsync("Shrink");
        await Ok(owner.PutAsJsonAsync(Costs(projectId, $"/resources/{api}", second.StreamId), Resource(element, "API", 50, price)));
        await Ok(owner.PutAsJsonAsync(Costs(projectId, $"/resources/{api}"), Resource(element, "API", 70, price)));
        Assert.Contains("'API' is changed on both sides", Assert.Single((await PreviewAsync(second))["costSheet"]!["conflicts"]!.AsArray())!.GetValue<string>());
        var refused = await Ok(MergeAsync(second), HttpStatusCode.Conflict);
        Assert.Equal("cost_sheet_conflict", refused["type"]!.GetValue<string>());
        Assert.Contains("'API' is changed on both sides", refused["detail"]!.GetValue<string>());
        Assert.Equal(70m, (await Ok(owner.GetAsync(Costs(projectId))))["resources"]!.AsArray().Single(r => r!["name"]!.GetValue<string>() == "API")!["quantity"]!.GetValue<decimal>());
        Assert.Equal(50m, (await Ok(owner.GetAsync(Costs(projectId, "", second.StreamId))))["resources"]!.AsArray().Single(r => r!["name"]!.GetValue<string>() == "API")!["quantity"]!.GetValue<decimal>());

        // An abandoned draft keeps no cost data either.
        await Status(HttpStatusCode.OK, owner.PostAsync($"/api/projects/{projectId}/drafts/{second.Id}/abandon", null), "abandon");
        Assert.Equal((false, false, false), await CostDataAsync(second));
    }

    [Fact]
    public async Task A_viewer_reads_an_editor_writes_the_sheet_and_only_an_owner_changes_the_price_book()
    {
        var factory = fixture.Factory;
        using var owner = await TestHost.SignInAsync(factory);
        var (workspaceId, projectId) = await TestHost.CreateProjectAsync(owner);
        var element = await ElementAsync(owner, projectId);
        var price = await PriceAsync(owner, workspaceId);
        using var editor = await TestHost.SignInMemberAsync(factory, owner, workspaceId, "Editor");
        using var viewer = await TestHost.SignInMemberAsync(factory, owner, workspaceId, "Viewer");

        var sheet = await Ok(editor.PostAsJsonAsync(Costs(projectId, "/resources"), Resource(element, "API", 1, price)), HttpStatusCode.Created);
        var resourceId = sheet["resources"]![0]!["id"]!.GetValue<Guid>();
        var saved = await Ok(editor.PostAsJsonAsync(Costs(projectId, "/estimates"), new { }), HttpStatusCode.Created);

        foreach (var path in new[] { "", "/estimate", "/estimates", $"/estimates/{saved["id"]}", "/prices", "/compare?left=main&right=main" })
            await Status(HttpStatusCode.OK, viewer.GetAsync(Costs(projectId, path)), $"viewer GET {path}");
        await Status(HttpStatusCode.OK, viewer.GetAsync(Prices(workspaceId)), "viewer GET prices");

        await Status(HttpStatusCode.Forbidden, viewer.PostAsJsonAsync(Costs(projectId, "/resources"), Resource(element, "Planted", 1, price)), "viewer POST resource");
        await Status(HttpStatusCode.Forbidden, viewer.PutAsJsonAsync(Costs(projectId, $"/resources/{resourceId}"), Resource(element, "API", 9, price)), "viewer PUT resource");
        await Status(HttpStatusCode.Forbidden, viewer.DeleteAsync(Costs(projectId, $"/resources/{resourceId}")), "viewer DELETE resource");
        await Status(HttpStatusCode.Forbidden, viewer.PostAsJsonAsync(Costs(projectId, "/assumptions"), Assumption()), "viewer POST assumption");
        await Status(HttpStatusCode.Forbidden, viewer.PostAsJsonAsync(Costs(projectId, "/copy"), new { from = "main" }), "viewer POST copy");
        await Status(HttpStatusCode.Forbidden, viewer.PostAsJsonAsync(Costs(projectId, "/estimates"), new { }), "viewer POST estimate");
        await Status(HttpStatusCode.Forbidden, viewer.DeleteAsync(Costs(projectId, $"/estimates/{saved["id"]}")), "viewer DELETE estimate");

        // The price book is a workspace setting: an editor does not change it and does not make Vellum call a provider.
        foreach (var member in new[] { editor, viewer })
        {
            await Status(HttpStatusCode.Forbidden, member.PostAsJsonAsync(Prices(workspaceId),
                new { provider = "custom", sku = "x", unit = "hour", unitPrice = 1, currency = "USD", effectiveDate = "2026-01-01" }), "member POST price");
            await Status(HttpStatusCode.Forbidden, member.DeleteAsync(Prices(workspaceId, $"/{price}")), "member DELETE price");
            await Status(HttpStatusCode.Forbidden, member.GetAsync(Prices(workspaceId, "/azure/meters?serviceName=Storage")), "member GET meters");
            await Status(HttpStatusCode.Forbidden, member.PostAsJsonAsync(Prices(workspaceId, "/azure"), new { meterId = "m", currency = "USD" }), "member POST azure");
        }

        // The resource and the saved estimate use the entry, so it stays in the book.
        var book = await Ok(viewer.GetAsync(Costs(projectId, "/prices")));
        Assert.Equal((workspaceId, 1), (book["workspaceId"]!.GetValue<Guid>(), book["entries"]!.AsArray().Count));
        Assert.Equal(projectId, Assert.Single(book["entries"]![0]!["usedBy"]!.AsArray())!["projectId"]!.GetValue<Guid>());
        await Status(HttpStatusCode.Conflict, owner.DeleteAsync(Prices(workspaceId, $"/{price}")), "delete a used price");
        await Status(HttpStatusCode.OK, editor.DeleteAsync(Costs(projectId, $"/resources/{resourceId}")), "editor DELETE resource");
        await Status(HttpStatusCode.Conflict, owner.DeleteAsync(Prices(workspaceId, $"/{price}")), "delete a price a saved estimate uses");
        await Status(HttpStatusCode.NoContent, editor.DeleteAsync(Costs(projectId, $"/estimates/{saved["id"]}")), "editor DELETE estimate");
        await Status(HttpStatusCode.NoContent, owner.DeleteAsync(Prices(workspaceId, $"/{price}")), "delete a price no project uses");

        var invalid = await Ok(owner.PostAsJsonAsync(Prices(workspaceId),
            new { provider = "ibm/cloud!", sku = "", unit = "10K", unitPrice = -1, currency = "usd" }), HttpStatusCode.BadRequest);
        Assert.Equal(["currency", "effectiveDate", "provider", "sku", "unit", "unitPrice"],
            invalid["errors"]!.AsArray().Select(e => e!["field"]!.GetValue<string>()).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Costs_and_prices_of_one_workspace_are_closed_to_a_user_of_another()
    {
        var factory = fixture.Factory;

        using var owner = await TestHost.SignInAsync(factory);
        var (theirWorkspace, theirs) = await TestHost.CreateProjectAsync(owner);
        var element = await ElementAsync(owner, theirs, "Secret system");
        var price = await PriceAsync(owner, theirWorkspace, 123.456m, sku: "secret-sku");
        var sheet = await Ok(owner.PostAsJsonAsync(Costs(theirs, "/resources"), Resource(element, "Secret resource", 7, price)), HttpStatusCode.Created);
        var resourceId = sheet["resources"]![0]!["id"]!.GetValue<Guid>();
        var saved = await Ok(owner.PostAsJsonAsync(Costs(theirs, "/estimates"), new { name = "Secret estimate" }), HttpStatusCode.Created);
        var draft = (await (await TestHost.Created(owner.PostAsJsonAsync($"/api/projects/{theirs}/drafts", new { id = Guid.NewGuid(), name = "Secret draft" })))
            .Content.ReadFromJsonAsync<DraftDto>())!;
        await Ok(owner.PostAsJsonAsync(Costs(theirs, "/copy", draft.StreamId), new { from = "main" }));
        var sheetBefore = await owner.GetStringAsync(Costs(theirs));
        var draftBefore = await owner.GetStringAsync(Costs(theirs, "", draft.StreamId));
        var bookBefore = await owner.GetStringAsync(Prices(theirWorkspace));

        using var stranger = await TestHost.SignInAsync(factory);
        var (myWorkspace, mine) = await TestHost.CreateProjectAsync(stranger);
        var myElement = await ElementAsync(stranger, mine);

        // 1. Their project and their workspace: everything is forbidden, with or without the branch.
        foreach (var branch in new Guid?[] { null, draft.StreamId })
        {
            foreach (var path in new[] { "", "/estimate", "/estimates", $"/estimates/{saved["id"]}", "/prices", "/compare?left=main&right=main" })
                await Status(HttpStatusCode.Forbidden, stranger.GetAsync(Costs(theirs, path, branch)), $"GET {path}");
            await Status(HttpStatusCode.Forbidden, stranger.PostAsJsonAsync(Costs(theirs, "/resources", branch), Resource(element, "Planted", 1, null)), "POST resource");
            await Status(HttpStatusCode.Forbidden, stranger.PutAsJsonAsync(Costs(theirs, $"/resources/{resourceId}", branch), Resource(element, "Renamed", 1, null)), "PUT resource");
            await Status(HttpStatusCode.Forbidden, stranger.DeleteAsync(Costs(theirs, $"/resources/{resourceId}", branch)), "DELETE resource");
            await Status(HttpStatusCode.Forbidden, stranger.PostAsJsonAsync(Costs(theirs, "/assumptions", branch), Assumption()), "POST assumption");
            await Status(HttpStatusCode.Forbidden, stranger.PostAsJsonAsync(Costs(theirs, "/copy", branch), new { from = "main" }), "POST copy");
            await Status(HttpStatusCode.Forbidden, stranger.PostAsJsonAsync(Costs(theirs, "/estimates", branch), new { }), "POST estimate");
            await Status(HttpStatusCode.Forbidden, stranger.DeleteAsync(Costs(theirs, $"/estimates/{saved["id"]}", branch)), "DELETE estimate");
        }
        await Status(HttpStatusCode.Forbidden, stranger.GetAsync(Prices(theirWorkspace)), "GET prices");
        await Status(HttpStatusCode.Forbidden, stranger.PostAsJsonAsync(Prices(theirWorkspace),
            new { provider = "custom", sku = "planted", unit = "hour", unitPrice = 1, currency = "USD", effectiveDate = "2026-01-01" }), "POST price");
        await Status(HttpStatusCode.Forbidden, stranger.DeleteAsync(Prices(theirWorkspace, $"/{price}")), "DELETE price");
        await Status(HttpStatusCode.Forbidden, stranger.GetAsync(Prices(theirWorkspace, "/azure/meters?serviceName=Storage")), "GET meters");
        await Status(HttpStatusCode.Forbidden, stranger.PostAsJsonAsync(Prices(theirWorkspace, "/azure"), new { meterId = "m", currency = "USD" }), "POST azure");

        // 2. Their IDs through the stranger's own project and workspace: nothing of theirs is found.
        await Status(HttpStatusCode.BadRequest, stranger.GetAsync(Costs(mine, "", draft.StreamId)), "their branch, own project");
        await Status(HttpStatusCode.BadRequest, stranger.PostAsJsonAsync(Costs(mine, "/copy"), new { from = draft.StreamId }), "copy from their branch");
        await Status(HttpStatusCode.BadRequest, stranger.GetAsync(Costs(mine, $"/compare?left=main&right={draft.StreamId}")), "compare with their branch");
        await Status(HttpStatusCode.NotFound, stranger.GetAsync(Costs(mine, $"/estimates/{saved["id"]}")), "their estimate, own project");
        await Status(HttpStatusCode.NotFound, stranger.DeleteAsync(Costs(mine, $"/estimates/{saved["id"]}")), "delete their estimate");
        await Status(HttpStatusCode.NotFound, stranger.PutAsJsonAsync(Costs(mine, $"/resources/{resourceId}"), Resource(myElement, "Theirs", 1, null)), "their resource, own project");
        await Status(HttpStatusCode.NotFound, stranger.DeleteAsync(Prices(myWorkspace, $"/{price}")), "delete their price through own workspace");
        await Status(HttpStatusCode.BadRequest, stranger.PostAsJsonAsync(Costs(mine, "/resources"), Resource(myElement, "Borrowed price", 1, price)), "their price in own resource");
        await Status(HttpStatusCode.BadRequest, stranger.PostAsJsonAsync(Costs(mine, "/resources"), Resource(element, "Their element", 1, null)), "their element in own resource");

        // 3. What the stranger can read holds nothing of theirs, and theirs is unchanged.
        var own = await stranger.GetStringAsync(Costs(mine)) + await stranger.GetStringAsync(Prices(myWorkspace))
                  + await stranger.GetStringAsync(Costs(mine, "/prices"))
                  + await stranger.GetStringAsync(Costs(mine, "/estimate")) + await stranger.GetStringAsync(Costs(mine, "/estimates"));
        Assert.DoesNotContain("Secret", own);
        Assert.DoesNotContain("secret-sku", own);
        Assert.DoesNotContain("123.456", own);
        Assert.Equal(sheetBefore, await owner.GetStringAsync(Costs(theirs)));
        Assert.Equal(draftBefore, await owner.GetStringAsync(Costs(theirs, "", draft.StreamId)));
        Assert.Equal(bookBefore, await owner.GetStringAsync(Prices(theirWorkspace)));
    }

    [Fact]
    public async Task Deleting_a_project_removes_its_sheets_and_saved_estimates_and_keeps_the_price_book()
    {
        var factory = fixture.Factory;
        using var owner = await TestHost.SignInAsync(factory);
        var (workspaceId, projectId) = await TestHost.CreateProjectAsync(owner);
        var element = await ElementAsync(owner, projectId);
        var price = await PriceAsync(owner, workspaceId);
        await Ok(owner.PostAsJsonAsync(Costs(projectId, "/resources"), Resource(element, "API", 1, price)), HttpStatusCode.Created);
        var draft = (await (await TestHost.Created(owner.PostAsJsonAsync($"/api/projects/{projectId}/drafts", new { id = Guid.NewGuid(), name = "Draft" })))
            .Content.ReadFromJsonAsync<DraftDto>())!;
        await Ok(owner.PostAsJsonAsync(Costs(projectId, "/copy", draft.StreamId), new { from = "main" }));
        await Ok(owner.PostAsJsonAsync(Costs(projectId, "/estimates"), new { }), HttpStatusCode.Created);

        // A second project of the workspace keeps what it has.
        var keptProject = Guid.NewGuid();
        await TestHost.Created(owner.PostAsJsonAsync($"/api/workspaces/{workspaceId}/projects", new { id = keptProject, name = "Kept" }));
        var keptElement = await ElementAsync(owner, keptProject);
        await Ok(owner.PostAsJsonAsync(Costs(keptProject, "/resources"), Resource(keptElement, "Kept resource", 1, price)), HttpStatusCode.Created);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var sheets = await db.Sheets.AsNoTracking().Where(s => s.ProjectId == projectId).Select(s => s.BranchId).ToListAsync();
        Assert.Equal(2, sheets.Count);

        await Status(HttpStatusCode.NoContent, owner.DeleteAsync($"/api/projects/{projectId}"), "delete project");

        Assert.False(await db.Sheets.AnyAsync(s => s.ProjectId == projectId));
        Assert.False(await db.Estimates.AnyAsync(e => e.ProjectId == projectId));
        foreach (var sheet in sheets)
        {
            var (read, version) = await CostAccess.LoadSheetAsync(db, sheet, default);
            Assert.Equal((0L, 0, 0), (version, read.Resources.Count, read.Assumptions.Count));
        }

        // The deleted project no longer uses the entry. The kept project still does, so it stays.
        var entry = Assert.Single((await Ok(owner.GetAsync(Prices(workspaceId))))["items"]!.AsArray())!;
        Assert.Equal(["Kept"], entry["usedBy"]!.AsArray().Select(u => u!["name"]!.GetValue<string>()));
        Assert.Single((await Ok(owner.GetAsync(Costs(keptProject))))["resources"]!.AsArray());
        await Status(HttpStatusCode.Conflict, owner.DeleteAsync(Prices(workspaceId, $"/{price}")), "delete the price the kept project uses");
    }

    [Fact]
    public async Task A_used_price_cannot_be_deleted_and_the_refusal_names_each_project_that_uses_it()
    {
        var factory = fixture.Factory;
        using var owner = await TestHost.SignInAsync(factory);
        var (workspaceId, _) = await TestHost.CreateProjectAsync(owner);
        var (first, second) = (Guid.NewGuid(), Guid.NewGuid());
        await TestHost.Created(owner.PostAsJsonAsync($"/api/workspaces/{workspaceId}/projects", new { id = first, name = "Orders" }));
        await TestHost.Created(owner.PostAsJsonAsync($"/api/workspaces/{workspaceId}/projects", new { id = second, name = "Billing" }));
        var price = await PriceAsync(owner, workspaceId);
        var unused = await PriceAsync(owner, workspaceId, sku: "unused");

        // The first project uses the entry in a usage line of a draft; the second in the baseline of main.
        await Ok(owner.PostAsJsonAsync(Costs(second, "/resources"), Resource(await ElementAsync(owner, second), "Billing API", 1, price)), HttpStatusCode.Created);
        var element = await ElementAsync(owner, first);
        var draft = (await (await TestHost.Created(owner.PostAsJsonAsync($"/api/projects/{first}/drafts", new { id = Guid.NewGuid(), name = "Draft" })))
            .Content.ReadFromJsonAsync<DraftDto>())!;
        var withAssumption = await Ok(owner.PostAsJsonAsync(Costs(first, "/assumptions", draft.StreamId), Assumption()), HttpStatusCode.Created);
        var assumption = withAssumption["assumptions"]![0]!["id"]!.GetValue<Guid>();
        await Ok(owner.PostAsJsonAsync(Costs(first, "/resources", draft.StreamId),
            Resource(element, "Orders API", null, null, [new { name = "Requests", assumptionId = assumption, priceRef = price }])), HttpStatusCode.Created);

        var refused = await Ok(owner.DeleteAsync(Prices(workspaceId, $"/{price}")), HttpStatusCode.Conflict);
        Assert.Equal(("price_in_use", "Used by 2 projects: Billing, Orders"), (refused["type"]!.GetValue<string>(), refused["title"]!.GetValue<string>()));
        var book = (await Ok(owner.GetAsync(Prices(workspaceId))))["items"]!.AsArray();
        Assert.Equal(["Billing", "Orders"],
            book.Single(e => e!["id"]!.GetValue<Guid>() == price)!["usedBy"]!.AsArray().Select(u => u!["name"]!.GetValue<string>()));
        Assert.Empty(book.Single(e => e!["id"]!.GetValue<Guid>() == unused)!["usedBy"]!.AsArray());
        Assert.Equal(2, book.Count);

        await Status(HttpStatusCode.NoContent, owner.DeleteAsync(Prices(workspaceId, $"/{unused}")), "delete the unused price");
        var audit = (await Ok(owner.GetAsync($"/api/workspaces/{workspaceId}/audit"))).AsArray();
        Assert.Single(audit, a => a!["action"]!.GetValue<string>() == "price.deleted");
    }

    [Fact]
    public async Task A_csv_file_is_checked_line_by_line_and_adds_every_price_or_none()
    {
        var factory = fixture.Factory;
        using var owner = await TestHost.SignInAsync(factory);
        var (workspaceId, projectId) = await TestHost.CreateProjectAsync(owner);
        using var editor = await TestHost.SignInMemberAsync(factory, owner, workspaceId, "Editor");
        Task<HttpResponseMessage> Send(HttpClient client, string csv) => client.PostAsJsonAsync(Prices(workspaceId, "/csv"), new { csv });

        var bad = await Ok(Send(owner, """
            provider,region,sku,unit,unitPrice,currency,effectiveDate
            hetzner,fsn1,CX22,hour,0.0066,EUR,2026-01-01
            hetzner,fsn1,,hour,1.5.0,eur,2026-13-01
            ibm/cloud!,eu-de,"bx2-2x8, profile",hour,-1,USD,2026-01-01
            only,three,values
            """), HttpStatusCode.BadRequest);
        Assert.Equal(
            ["line 3.currency", "line 3.effectiveDate", "line 3.sku", "line 3.unitPrice", "line 4.provider", "line 4.unitPrice", "line 5"],
            bad["errors"]!.AsArray().Select(e => e!["field"]!.GetValue<string>()).Order(StringComparer.Ordinal));
        // Nothing of the file was added, the good line neither.
        Assert.Empty((await Ok(owner.GetAsync(Prices(workspaceId))))["items"]!.AsArray());

        var header = await Ok(Send(owner, "provider,sku,unit,unitPrice,currency,effectiveDate,colour\nx,y,hour,1,USD,2026-01-01,red"), HttpStatusCode.BadRequest);
        Assert.Equal(2, header["errors"]!.AsArray().Count);
        Assert.All(header["errors"]!.AsArray(), e => Assert.Equal("line 1", e!["field"]!.GetValue<string>()));

        await Status(HttpStatusCode.Forbidden, Send(editor, "provider,region,sku,unit,unitPrice,currency,effectiveDate\nhetzner,fsn1,CX22,hour,0.0066,EUR,2026-01-01"), "editor CSV");

        // Columns in any order, a quoted value with a comma, a blank line and a byte order mark.
        var done = await Ok(Send(owner, "\uFEFFsku,provider,region,unit,unitPrice,currency,effectiveDate,description\r\n"
            + "CX22,Hetzner,fsn1,hour,0.0066,EUR,2026-01-01,\"2 vCPU, 4 GB\"\r\n\r\n"
            + "Rack,on-premises,,month,450,EUR,2026-02-01,\r\n"), HttpStatusCode.Created);
        Assert.Equal(2, done["imported"]!.GetValue<int>());
        var entries = (await Ok(owner.GetAsync(Prices(workspaceId))))["items"]!.AsArray();
        var cx22 = entries.Single(e => e!["sku"]!.GetValue<string>() == "CX22")!;
        Assert.Equal(("Hetzner", "fsn1", "hour", 0.0066m, "EUR", "2026-01-01", "custom", "2 vCPU, 4 GB"),
            (cx22["provider"]!.GetValue<string>(), cx22["region"]!.GetValue<string>(), cx22["unit"]!.GetValue<string>(), cx22["unitPrice"]!.GetValue<decimal>(),
                cx22["currency"]!.GetValue<string>(), cx22["effectiveDate"]!.GetValue<string>(), cx22["source"]!.GetValue<string>(), cx22["description"]!.GetValue<string>()));
        Assert.All(entries, e => Assert.True(Guid.TryParse(e!["acceptedBy"]!.GetValue<string>(), out _)));
        var audit = (await Ok(owner.GetAsync($"/api/workspaces/{workspaceId}/audit"))).AsArray();
        Assert.Equal(2, audit.Count(a => a!["action"]!.GetValue<string>() == "price.added"));

        // A provider is a free name and matches a resource in any case.
        var element = await ElementAsync(owner, projectId);
        await Ok(owner.PostAsJsonAsync(Costs(projectId, "/resources"), new
        {
            elementId = element, name = "Web", category = "compute", provider = "hetzner", region = "fsn1", sku = "CX22",
            quantity = 1, unit = "instances", priceRef = cx22["id"]!.GetValue<Guid>(),
        }), HttpStatusCode.Created);
        var line = Assert.Single((await Ok(owner.GetAsync(Costs(projectId, $"/estimate?asOf={AsOf}"))))["estimate"]!["lines"]!.AsArray())!;
        Assert.Equal(("known", 4.818m), (line["status"]!.GetValue<string>(), line["amount"]!.GetValue<decimal>()));
    }
}
