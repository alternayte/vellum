using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Deedbox;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vellum.Modules.Import;
using Vellum.Modules.Import.Proposals;
using Vellum.Modules.Import.Scans;
using Vellum.Modules.Modelling;
using Vellum.Shared;

namespace Vellum.Tests.Modules.Import;

/// <summary>
/// What two Sources of one project may and may not do to each other's facts, and what a
/// bulk accept, a "same as?" answer and a re-plan may never do.
/// </summary>
public class SourceReviewTests(ImportHost import) : IClassFixture<ImportHost>
{
    private const string Mirror = "acme/orders-mirror";

    [Fact]
    public async Task A_removal_of_an_entity_that_another_Source_holds_unlinks_only_and_bulk_accept_skips_it()
    {
        var (s, a, b) = await TwoSourcesAsync();
        var billing = (await s.ElementsAsync()).Single("name", "Billing").Text("id")!;
        await LinkFromBAsync(s, b, "Billing");

        // A's next commit has no billing service; B still has it.
        s.GitHub.Commit(ImportScenario.Commit3, Without("billing/"));
        var rescan = await s.ScanAsync();
        var removal = (await s.ProposalsAsync(rescan.Id())).Single(p => p.Text("action") == "remove" && p.Text("entityKind") == "element");
        Assert.Equal(b.Text("id"), removal["otherSources"]![0]!["sourceId"]!.GetValue<string>());
        Assert.Null((await s.ElementsAsync()).Single("name", "Billing")["stale"] as JsonObject);

        var bulk = await AcceptAllAsync(s, a.Id());
        Assert.DoesNotContain(removal.Id(), Ids(bulk["accepted"]));

        // Accepted by a person, the removal drops A's fact and leaves the element B holds.
        var decided = await s.DecideAsync(rescan.Id(), accept: [removal.Id()], acknowledgeImpact: true);
        Assert.Empty(decided["failed"]!.AsArray());
        Assert.Contains(await s.ElementsAsync(), e => e.Text("id") == billing);
        Assert.False(await DbAsync(db => db.FactLinks.AnyAsync(l => l.ProjectId == s.ProjectId && l.Repository == "acme/orders" && l.EntityId == Guid.Parse(billing))));
        Assert.True(await DbAsync(db => db.FactLinks.AnyAsync(l => l.ProjectId == s.ProjectId && l.Repository == Mirror && l.EntityId == Guid.Parse(billing))));
    }

    [Fact]
    public async Task A_name_match_with_any_entity_of_another_Source_names_that_Source_and_bulk_accept_leaves_it_to_a_person()
    {
        var (s, _, b) = await TwoSourcesAsync();
        var scanB = b["review"]!["scanId"]!.GetValue<Guid>();
        var taken = (await s.AllAsync(s.Model("channels"))).Select(c => c.Text("id")!)
            .Concat((await s.AllAsync(s.Model("messages"))).Select(m => m.Text("id")!))
            .Concat((await s.AllAsync(s.Model("schemas"))).Select(m => m.Text("id")!))
            .ToHashSet();
        var onA = (await s.ProposalsAsync(scanB)).Where(p => taken.Contains(p.Text("entityId")!)).ToList();
        Assert.NotEmpty(onA);
        Assert.All(onA, p => Assert.NotEmpty(p["otherSources"]!.AsArray()));

        var source = (await SourcesAsync(s)).Single("id", b.Text("id")!);
        var bulk = await AcceptAllAsync(s, source.Id());
        Assert.DoesNotContain(onA, p => Ids(bulk["accepted"]).Contains(p.Id()));
    }

    [Fact]
    public async Task A_re_plan_of_a_scan_that_a_newer_scan_superseded_writes_nothing()
    {
        var (s, _, b) = await TwoSourcesAsync();
        var older = b["review"]!["scanId"]!.GetValue<Guid>();
        var newer = await StartSourceScanAsync(s, b.Id(), new { @ref = "mirror" });
        await s.DrainAsync();
        var before = await PendingAsync(s, newer);
        Assert.NotEmpty(before);
        Assert.Empty(await PendingAsync(s, older));

        Assert.Equal(SnapshotOutcome.LeaseLost, await PlanAgainAsync(older));
        Assert.Equal(before.Select(p => p.Text("id")), (await PendingAsync(s, newer)).Select(p => p.Text("id")));
        Assert.Empty(await PendingAsync(s, older));
    }

    [Fact]
    public async Task Two_re_plans_of_one_scan_at_once_leave_one_proposal_per_fact()
    {
        var (_, _, b) = await TwoSourcesAsync();
        var scanB = b["review"]!["scanId"]!.GetValue<Guid>();
        for (var round = 0; round < 4; round++)
        {
            var outcomes = await Task.WhenAll(PlanAgainAsync(scanB), PlanAgainAsync(scanB), PlanAgainAsync(scanB));
            Assert.Contains(SnapshotOutcome.Written, outcomes);
            var keys = await DbAsync(db => db.ImportProposals.Where(p => p.ScanId == scanB && p.Status == ProposalStatus.Pending)
                .Select(p => p.Subject + "|" + p.FactKey + "|" + p.EntityKind).ToListAsync());
            Assert.Equal(keys.Count, keys.Distinct().Count());
        }
    }

    [Fact]
    public async Task A_scan_by_the_old_name_of_a_renamed_repository_uses_its_Source()
    {
        var s = await ImportScenario.CreateAsync(import);
        await s.ScanAndAcceptAllAsync();
        s.GitHub.RepositoryIds["acme/orders"] = 4242;
        await s.SqlAsync($"UPDATE import.fact_links SET repository_id = 4242 WHERE project_id = {s.ProjectId}");
        s.GitHub.FullNames["acme/orders"] = "Acme/Orders-Service";
        var renamed = await s.ScanAsync();
        Assert.Equal(("completed", "acme/orders-service"), (renamed.Text("status"), renamed.Text("repository")));

        var again = await s.ScanAsync("acme/orders");
        Assert.True(again.Text("status") == "completed", again.ToJsonString());
        var source = Assert.Single(await SourcesAsync(s));
        Assert.Equal((source.Text("id"), "acme/orders-service"), (again.Text("sourceId"), source.Text("locator")));
    }

    [Fact]
    public async Task Bulk_accept_leaves_out_what_depends_on_a_rejected_proposal_and_accepts_the_rest()
    {
        var s = await ImportScenario.CreateAsync(import);
        var scan = await s.ScanAsync();
        var service = (await s.ProposalsAsync(scan.Id())).Proposal("element", "Orders.Api");
        await s.DecideAsync(scan.Id(), reject: [service.Id()]);
        var source = Assert.Single(await SourcesAsync(s));

        var bulk = await AcceptAllAsync(s, source.Id());
        Assert.Empty(bulk["failed"]!.AsArray());
        Assert.NotEmpty(bulk["accepted"]!.AsArray());
        var left = await PendingAsync(s, scan.Id());
        Assert.All(left, p => Assert.Contains(service.Id(), p["dependsOn"]!.AsArray().Select(d => d!.GetValue<Guid>())));
    }

    [Fact]
    public async Task A_scan_of_one_Source_does_not_clear_a_stale_mark_that_another_Source_set()
    {
        var (s, a, b) = await TwoSourcesAsync();
        await LinkFromBAsync(s, b, "Billing");
        var billing = Guid.Parse((await s.ElementsAsync()).Single("name", "Billing").Text("id")!);

        // Source A marked the element stale.
        using (var scope = import.Host.Factory.Services.CreateScope())
        {
            var streamId = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Projects
                .Where(p => p.Id == s.ProjectId).Select(p => p.StreamId).FirstAsync();
            var store = scope.ServiceProvider.GetRequiredService<IEventStore>().ForProject(s.ProjectId);
            var (state, version) = await store.Load<ModelState>(StreamId.From(streamId));
            await store.Append(StreamId.From(streamId), ExpectedVersion.Exact(version),
                [.. StaleDecider.Mark(state, billing, new StaleMark(ImportScenario.Commit3, StaleDecider.EvidenceNotFound, a.Id()))]);
        }
        Assert.NotNull((await s.ElementsAsync()).Single("id", billing.ToString())["stale"] as JsonObject);

        // B's scan finds the element: the mark is A's to clear.
        await StartSourceScanAsync(s, b.Id(), new { @ref = "mirror" });
        await s.DrainAsync();
        Assert.NotNull((await s.ElementsAsync()).Single("id", billing.ToString())["stale"] as JsonObject);

        // A's scan finds it again: the mark goes.
        await s.ScanAsync();
        Assert.Null((await s.ElementsAsync()).Single("id", billing.ToString())["stale"] as JsonObject);
    }

    [Fact]
    public async Task A_name_match_with_a_hand_made_element_asks_and_bulk_accept_never_takes_a_label_change()
    {
        var s = await ImportScenario.CreateAsync(import);
        var system = Guid.NewGuid();
        await TestHost.Created(s.Owner.PostAsJsonAsync(s.Model("elements"), new { id = system, kind = "system", name = "orders" }));
        var scan = await s.ScanAsync();
        var question = (await s.ProposalsAsync(scan.Id())).Proposal("element", "orders");
        Assert.Equal(("bind", system.ToString()), (question.Text("action"), question.Text("entityId")));

        var source = Assert.Single(await SourcesAsync(s));
        var bulk = await AcceptAllAsync(s, source.Id());
        Assert.DoesNotContain(bulk["accepted"]!.AsArray(), id => id!.GetValue<Guid>() == question.Id());
        Assert.DoesNotContain((await s.Owner.GetFromJsonAsync<JsonArray>($"/api/projects/{s.ProjectId}/sources/{source.Id()}/bindings"))!,
            x => x!["elementId"]!.GetValue<string>() == system.ToString());

        // Yes binds it; the label change that follows is for a person to accept.
        await AnswerAsync(s, scan.Id(), question.Id(), same: true);
        var relabel = (await PendingAsync(s, scan.Id())).Single(p => p.Text("entityId") == system.ToString());
        Assert.Contains(relabel["changes"]!.AsArray(), c => c!["field"]!.GetValue<string>() == "factStatus");
        var second = await AcceptAllAsync(s, source.Id());
        Assert.DoesNotContain(relabel.Id(), Ids(second["accepted"]));
        Assert.Equal("declared", (await s.ElementsAsync()).Single("id", system.ToString()).Text("factStatus"));
    }

    // Source A is acme/orders, accepted whole. Source B is a mirror of the same files at the
    // branch "mirror", scanned once and not decided.
    private async Task<(ImportScenario S, JsonObject A, JsonObject B)> TwoSourcesAsync()
    {
        var s = await ImportScenario.CreateAsync(import);
        await s.ScanAndAcceptAllAsync();
        s.GitHub.Commit(ImportScenario.Commit2, FixtureGitHub.Load("orders"));
        s.GitHub.Head = ImportScenario.Commit1;
        s.GitHub.Branches["mirror"] = ImportScenario.Commit2;
        var created = await s.Owner.PostAsJsonAsync($"/api/projects/{s.ProjectId}/sources", new { kind = "github", repository = Mirror });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var b = (await created.Content.ReadFromJsonAsync<JsonObject>())!;
        await StartSourceScanAsync(s, b.Id(), new { @ref = "mirror" });
        await s.DrainAsync();
        var sources = await SourcesAsync(s);
        return (s, sources.Single("locator", "acme/orders"), sources.Single("locator", Mirror));
    }

    /// <summary>B says yes to the element and accepts its fact, so both Sources link it.</summary>
    private static async Task LinkFromBAsync(ImportScenario s, JsonObject b, string name)
    {
        var scanB = b["review"]!["scanId"]!.GetValue<Guid>();
        var question = (await PendingAsync(s, scanB)).Single(p => p.Text("action") == "bind" && p.Text("name") == name);
        await AnswerAsync(s, scanB, question.Id(), same: true);
        var change = (await PendingAsync(s, scanB)).Single(p => p.Text("entityKind") == "element" && p.Text("name") == name);
        var decided = await s.DecideAsync(scanB, accept: [change.Id(), .. change["dependsOn"]!.AsArray().Select(d => d!.GetValue<Guid>())], acknowledgeImpact: true);
        Assert.True(decided["failed"]!.AsArray().Count == 0, decided.ToJsonString());
    }

    private static async Task AnswerAsync(ImportScenario s, Guid scanId, Guid questionId, bool same)
    {
        var response = await s.Owner.PostAsJsonAsync(s.Url($"/{scanId}/proposals/decide"), new { bind = new[] { new { id = questionId, same } } });
        var body = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.True(response.StatusCode == HttpStatusCode.OK && body["failed"]!.AsArray().Count == 0 && body["replanned"]!.GetValue<bool>(), body.ToJsonString());
    }

    private async Task<SnapshotOutcome> PlanAgainAsync(Guid scanId)
    {
        await using var scope = import.Host.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var scan = await db.Scans.AsNoTracking().FirstAsync(x => x.Id == scanId);
        var input = await ScanRequests.StoredInputAsync(db, scan, CancellationToken.None);
        return await SnapshotWriter.WriteAsync(scope.ServiceProvider, scan, WriteMode.PlanAgain(), input, DateTimeOffset.UtcNow, CancellationToken.None);
    }

    private static async Task<List<JsonObject>> PendingAsync(ImportScenario s, Guid scanId) =>
        [.. (await s.ProposalsAsync(scanId)).Where(p => p.Text("status") == "pending").OrderBy(p => p.Text("id"), StringComparer.Ordinal)];

    private static async Task<JsonObject> AcceptAllAsync(ImportScenario s, Guid sourceId)
    {
        var response = await s.Owner.PostAsJsonAsync($"/api/projects/{s.ProjectId}/sources/{sourceId}/accept", new { acknowledgeImpact = true });
        var body = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.True(response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Conflict, body.ToJsonString());
        return response.StatusCode == HttpStatusCode.OK ? body : new JsonObject { ["accepted"] = new JsonArray(), ["failed"] = new JsonArray() };
    }

    private static HashSet<Guid> Ids(JsonNode? array) => [.. array!.AsArray().Select(id => id!.GetValue<Guid>())];

    private static SortedDictionary<string, byte[]> Without(string prefix)
    {
        var files = FixtureGitHub.Load("orders");
        foreach (var path in files.Keys.Where(p => p.StartsWith(prefix, StringComparison.Ordinal)).ToList())
            files.Remove(path);
        return files;
    }

    private static async Task<List<JsonObject>> SourcesAsync(ImportScenario s) =>
        [.. (await s.Owner.GetFromJsonAsync<JsonArray>($"/api/projects/{s.ProjectId}/sources"))!.Select(x => x!.AsObject())];

    private static async Task<Guid> StartSourceScanAsync(ImportScenario s, Guid sourceId, object body)
    {
        s.Import.Time.Advance(TimeSpan.FromSeconds(1));
        var response = await s.Owner.PostAsJsonAsync($"/api/projects/{s.ProjectId}/sources/{sourceId}/scans", body);
        Assert.True(response.StatusCode == HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<JsonObject>())!.Id();
    }

    private async Task<T> DbAsync<T>(Func<AppDbContext, Task<T>> read)
    {
        using var scope = import.Host.Factory.Services.CreateScope();
        return await read(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }
}
