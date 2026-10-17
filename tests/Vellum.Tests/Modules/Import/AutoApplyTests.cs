using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace Vellum.Tests.Modules.Import;

/// <summary>
/// A tracking scan applies what it observed, as the person who started it: every addition, and
/// a change of a fact that only this Source wrote and nobody edited since. A change of a fact a
/// person edited waits in "Needs you"; a fact the latest Snapshot no longer holds is marked
/// "not seen in latest scan" and stays until a person removes or keeps it.
/// </summary>
public class AutoApplyTests(AutoApplyImportHost import) : IClassFixture<AutoApplyImportHost>
{
    private static SortedDictionary<string, byte[]> WithoutBilling()
    {
        var files = FixtureGitHub.Load("orders");
        foreach (var path in files.Keys.Where(p => p.StartsWith("billing/", StringComparison.Ordinal)).ToList())
            files.Remove(path);
        return files;
    }

    private static async Task<List<JsonObject>> NeedsYouAsync(ImportScenario s)
    {
        var page = (await s.Owner.GetFromJsonAsync<JsonObject>(s.Model("needs-you")))!;
        return [.. page["sources"]!.AsArray().SelectMany(source => source!["items"]!.AsArray()).Select(i => i!.AsObject())];
    }

    private static JsonObject? Stale(JsonObject entity) => entity["stale"] as JsonObject;

    [Fact]
    public async Task The_first_scan_applies_every_addition_with_the_starter_as_acceptor_and_file_provenance()
    {
        var s = await ImportScenario.CreateAsync(import);
        var me = (await s.Owner.GetFromJsonAsync<JsonObject>("/api/auth/me"))!.Text("id");

        var scan = await s.ScanAsync();
        Assert.Equal("completed", scan.Text("status"));

        // Nothing waits: every proposal of the scan was an addition, and each is accepted by the person who asked for the scan.
        var proposals = await s.ProposalsAsync(scan.Id());
        Assert.NotEmpty(proposals);
        Assert.All(proposals, p => Assert.Equal(("add", "accepted"), (p.Text("action"), p.Text("status"))));
        Assert.Empty(await NeedsYouAsync(s));

        // Each fact on the model is observed in source, with the Snapshot, file and line it came from, and its acceptor.
        var elements = await s.ElementsAsync();
        var api = elements.Single("name", "Orders.Api");
        Assert.Equal("observed-in-source", api.Text("factStatus"));
        Assert.Equal((me, ImportScenario.Commit1),
            (api["provenance"]!["acceptedBy"]!.GetValue<string>(), api["provenance"]!["revision"]!.GetValue<string>()));
        Assert.Equal($"{s.GitHubHost}/acme/orders@{ImportScenario.Commit1}:orders-api/Orders.Api.csproj:1-1",
            api["provenance"]!["source"]!.GetValue<string>());
        Assert.All(elements, e => Assert.Equal(me, e["provenance"]!["acceptedBy"]!.GetValue<string>()));
    }

    [Fact]
    public async Task A_rescan_applies_a_change_of_a_fact_that_nobody_edited()
    {
        var s = await ImportScenario.CreateAsync(import);
        await s.ScanAsync();
        Assert.Equal("postgres", (await s.ElementsAsync()).Single("name", "postgres").Text("technology"));

        s.GitHub.Commit(ImportScenario.Commit2, FixtureGitHub.OrdersWithMySql());
        var rescan = await s.ScanAsync();

        var store = (await s.ElementsAsync()).Single("name", "postgres");
        Assert.Equal(("mysql", ImportScenario.Commit2), (store.Text("technology"), store["provenance"]!["revision"]!.GetValue<string>()));
        Assert.Equal("accepted", (await s.ProposalsAsync(rescan.Id())).Proposal("element", "postgres").Text("status"));
        Assert.Empty(await NeedsYouAsync(s));
    }

    [Fact]
    public async Task A_rescan_puts_a_change_of_a_hand_edited_fact_in_needs_you_and_keeps_the_edit()
    {
        var s = await ImportScenario.CreateAsync(import);
        await s.ScanAsync();
        var store = (await s.ElementsAsync()).Single("name", "postgres");
        var edited = await s.Owner.PatchAsJsonAsync(s.Model($"elements/{store.Id()}"), new { technology = "PostgreSQL 16" });
        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);

        // The source says what it said before: nothing to decide, and the edit stands.
        await s.ScanAsync();
        Assert.Empty(await NeedsYouAsync(s));

        // The source changes the edited fact: the change waits for the person.
        s.GitHub.Commit(ImportScenario.Commit2, FixtureGitHub.OrdersWithMySql());
        var rescan = await s.ScanAsync();
        var item = Assert.Single(await NeedsYouAsync(s));
        Assert.Equal(("conflict", "element", store.Text("id"), "pending"),
            (item.Text("action"), item.Text("entityKind"), item.Text("entityId"), item.Text("status")));
        Assert.Equal(("PostgreSQL 16", "mysql"),
            (item["current"]!["technology"]!.GetValue<string>(), item["proposed"]!["technology"]!.GetValue<string>()));
        Assert.Equal("PostgreSQL 16", (await s.ElementsAsync()).Single("id", store.Text("id")!).Text("technology"));

        // Accepting it is the person's act.
        Assert.Empty((await s.DecideAsync(rescan.Id(), accept: [item.Id()]))["failed"]!.AsArray());
        Assert.Equal("mysql", (await s.ElementsAsync()).Single("id", store.Text("id")!).Text("technology"));
        Assert.Empty(await NeedsYouAsync(s));
    }

    [Fact]
    public async Task A_rescan_deletes_nothing_and_marks_what_it_no_longer_sees_until_a_person_keeps_it()
    {
        var s = await ImportScenario.CreateAsync(import);
        await s.ScanAsync();
        var before = (await s.ElementsAsync()).Values("name");

        s.GitHub.Commit(ImportScenario.Commit2, WithoutBilling());
        await s.ScanAsync();

        // Every element stays; the one whose files are gone is marked, and its removal waits.
        var elements = await s.ElementsAsync();
        Assert.Equal(before, elements.Values("name"));
        var billing = elements.Single("name", "Billing");
        Assert.Equal((ImportScenario.Commit2, "evidence-not-found"), (Stale(billing)!.Text("sinceRevision"), Stale(billing)!.Text("reason")));
        Assert.All(elements.Where(e => e.Text("name") != "Billing"), e => Assert.Null(Stale(e)));
        var removal = (await NeedsYouAsync(s)).Single(i => i.Text("entityId") == billing.Text("id"));
        Assert.Equal("remove", removal.Text("action"));

        // Keep: the mark goes, the removal leaves "Needs you", and the next scan of the same Snapshot does not bring either back.
        var kept = await s.Owner.PostAsJsonAsync(s.Model("needs-you/keep"), new { entityKind = "element", entityId = billing.Id() });
        Assert.True(kept.StatusCode == HttpStatusCode.NoContent, await kept.Content.ReadAsStringAsync());
        Assert.Null(Stale((await s.ElementsAsync()).Single("name", "Billing")));
        Assert.DoesNotContain(await NeedsYouAsync(s), i => i.Text("entityId") == billing.Text("id"));

        await s.ScanAsync();
        elements = await s.ElementsAsync();
        Assert.Equal(before, elements.Values("name"));
        Assert.Null(Stale(elements.Single("name", "Billing")));
        Assert.DoesNotContain(await NeedsYouAsync(s), i => i.Text("entityId") == billing.Text("id"));
    }
}
