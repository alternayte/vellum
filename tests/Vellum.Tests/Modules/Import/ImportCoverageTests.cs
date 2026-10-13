using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vellum.Modules.Import;
using Vellum.Modules.Import.Proposals;
using Vellum.Tests.Shared.Outbound;

namespace Vellum.Tests.Modules.Import;

/// <summary>
/// The governing rule of a rescan: absence of evidence counts only when this scan looked
/// where the evidence was, at the same line of history, with the same rules. Each test makes
/// a scan that did not, and shows that it concludes nothing: no removal, no stale mark, no
/// cleared mark, nothing superseded.
/// </summary>
[Collection("Integration")]
public class ImportCoverageTests(ImportHost import) : IClassFixture<ImportHost>
{
    private static SortedDictionary<string, byte[]> WithoutBilling()
    {
        var files = FixtureGitHub.Load("orders");
        foreach (var path in files.Keys.Where(p => p.StartsWith("billing/", StringComparison.Ordinal)).ToList())
            files.Remove(path);
        return files;
    }

    private static Task SettingsAsync(ImportScenario s, JsonObject? importSettings = null, JsonObject? usage = null)
    {
        var overrides = new JsonObject { ["github"] = new JsonObject { ["apiBaseUrl"] = $"https://{s.GitHubHost}" } };
        if (importSettings is not null)
            overrides["import"] = importSettings;
        if (usage is not null)
            overrides["usage"] = usage;
        return OutboundHost.PutSettingsAsync(s.Owner, s.WorkspaceId, overrides);
    }

    private static async Task<string> ModelAsync(ImportScenario s)
    {
        var parts = new List<string>();
        foreach (var path in new[] { "elements", "relationships", "messages", "operations", "channels", "subscriptions", "schemas" })
            parts.Add(await s.Owner.GetStringAsync(s.Model(path)));
        return string.Join("\n", parts);
    }

    private async Task<T> DbAsync<T>(Func<ImportDbContext, Task<T>> read)
    {
        using var scope = import.Host.Factory.Services.CreateScope();
        return await read(scope.ServiceProvider.GetRequiredService<ImportDbContext>());
    }

    private static int Linked(JsonObject scan, string field) => scan["coverage"]!["linkedFacts"]![field]!.GetValue<int>();

    [Fact]
    public async Task A_scan_of_a_part_of_the_repository_concludes_nothing_about_the_rest()
    {
        var s = await ImportScenario.CreateAsync(import);
        await s.ScanAndAcceptAllAsync();
        var accepted = await ModelAsync(s);

        // Three ways to look at less than everything. Each scan is complete, and none of
        // them looked at the files of most accepted facts.
        var included = await s.ScanAsync(body: new { repository = "acme/orders", includePaths = new[] { "orders-api" } });
        var excluded = await s.ScanAsync(body: new { repository = "acme/orders", excludePaths = new[] { "billing", "inventory-worker" } });
        await SettingsAsync(s, new JsonObject { ["excludeGlobs"] = new JsonArray("billing/**", "contracts/**") });
        var globbed = await s.ScanAsync();
        await SettingsAsync(s);

        foreach (var scan in new[] { included, excluded, globbed })
        {
            Assert.Equal(("completed", true), (scan.Text("status"), scan["isComplete"]!.GetValue<bool>()));
            Assert.True(Linked(scan, "scope") > 0);
            Assert.Equal(Linked(scan, "scope"), Linked(scan, "notCovered"));
            Assert.True(scan["coverage"]!["filesExtracted"]!.GetValue<int>() < scan["coverage"]!["filesListed"]!.GetValue<int>());
            Assert.DoesNotContain(await s.ProposalsAsync(scan.Id()), p => p.Text("action") == "remove");

            // The proposal list says how many accepted facts were not judged, and why.
            var page = (await s.Owner.GetFromJsonAsync<JsonObject>(s.Url($"/{scan.Id()}/proposals")))!;
            Assert.Equal(Linked(scan, "scope"), page["notCovered"]!["scope"]!.GetValue<int>());
        }
        Assert.Equal(accepted, await ModelAsync(s));
        Assert.DoesNotContain("\"stale\":{", accepted);

        // The billing service is deleted, and a scan of everything says so: stale marks.
        s.GitHub.Commit(ImportScenario.Commit2, WithoutBilling());
        var full = await s.ScanAsync();
        Assert.Equal(0, Linked(full, "notCovered"));
        Assert.Contains(await s.ProposalsAsync(full.Id()), p => p.Text("action") == "remove");
        var marked = await ModelAsync(s);
        Assert.Contains("\"stale\":{", marked);

        // The service is back, and the next scan does not look at it. The marks stay: a
        // scan that did not look cannot say the evidence is there either.
        s.GitHub.Head = ImportScenario.Commit1;
        var elsewhere = await s.ScanAsync(body: new { repository = "acme/orders", includePaths = new[] { "orders-api" } });
        Assert.Equal("completed", elsewhere.Text("status"));
        Assert.Equal(marked, await ModelAsync(s));

        // A scan that looks finds the evidence and clears them.
        await s.ScanAsync();
        Assert.Equal(accepted, await ModelAsync(s));
    }

    [Fact]
    public async Task A_file_that_became_withheld_or_failed_its_check_is_not_covered()
    {
        var s = await ImportScenario.CreateAsync(import);
        await s.ScanAndAcceptAllAsync();
        var accepted = await ModelAsync(s);

        // The project file of the billing service now holds a private key. Its content is
        // withheld, so the scan cannot say that the service is gone.
        var files = FixtureGitHub.Load("orders");
        files["billing/Billing.csproj"] = Encoding.UTF8.GetBytes("-----BEGIN RSA PRIVATE KEY-----\nMIIB\n-----END RSA PRIVATE KEY-----\n");
        s.GitHub.Commit(ImportScenario.Commit2, files);
        var withheld = await s.ScanAsync();
        Assert.Equal("completed", withheld.Text("status"));
        Assert.True(Linked(withheld, "withheld") > 0);
        Assert.Contains(withheld["notes"]!.AsArray(), n => n!["code"]!.GetValue<string>() == "secret-file" && n["path"]!.GetValue<string>() == "billing/Billing.csproj");
        Assert.DoesNotContain(await s.ProposalsAsync(withheld.Id()), p => p.Text("action") == "remove");
        Assert.Equal(accepted, await ModelAsync(s));
        Assert.Equal(0, await DbAsync(db => db.Blobs.CountAsync(b => b.WorkspaceId == s.WorkspaceId && b.WithheldReason == "secret-file" && b.Content != null)));

        // GitHub returns other bytes than the file of the commit. They are not content.
        s.GitHub.Commit(ImportScenario.Commit3, FixtureGitHub.Load("orders"));
        s.GitHub.Corrupt = path => path == "inventory-worker/go.mod" ? Encoding.UTF8.GetBytes("module evil\n") : null;
        // The stored blob of that file would be used again; only a fetch can go wrong.
        await s.SqlAsync($"DELETE FROM import.blobs WHERE workspace_id = {s.WorkspaceId}");
        var mismatch = await s.ScanAsync();
        s.GitHub.Corrupt = null;
        Assert.Equal(("completed", false), (mismatch.Text("status"), mismatch["isComplete"]!.GetValue<bool>()));
        Assert.Contains(mismatch["notes"]!.AsArray(), n => n!["code"]!.GetValue<string>() == "blob-sha-mismatch");
        Assert.True(Linked(mismatch, "failed") > 0);
        Assert.DoesNotContain(await s.ProposalsAsync(mismatch.Id()), p => p.Text("action") == "remove");
        Assert.Equal(accepted, await ModelAsync(s));
        Assert.Equal(0, await DbAsync(db => db.Blobs.CountAsync(b => b.WorkspaceId == s.WorkspaceId && b.Content == Encoding.UTF8.GetBytes("module evil\n"))));
    }

    [Fact]
    public async Task A_scan_of_another_ref_is_a_preview_until_a_person_tracks_it()
    {
        var s = await ImportScenario.CreateAsync(import);
        var first = await s.ScanAndAcceptAllAsync();
        var tracked = await s.GetAsync(first.Id());
        Assert.Equal(("tracking", "main", "main"), (tracked.Text("kind"), tracked.Text("trackedRef"), tracked.Text("resolvedRef")));
        var accepted = await ModelAsync(s);
        var links = await DbAsync(db => db.FactLinks.CountAsync(l => l.ProjectId == s.ProjectId));

        // A pending proposal on the tracked ref: a person rejected nothing and accepted nothing yet.
        var files = FixtureGitHub.Load("orders-outbox-removed");
        s.GitHub.Commit(ImportScenario.Commit2, files);
        var pendingOnMain = await s.ScanAsync();
        Assert.NotEmpty(await s.ProposalsAsync(pendingOnMain.Id()));

        // A feature branch without the billing service.
        s.GitHub.Commit(ImportScenario.Commit3, WithoutBilling());
        s.GitHub.Head = ImportScenario.Commit2;
        s.GitHub.Branches["feature/no-billing"] = ImportScenario.Commit3;
        var preview = await s.ScanAsync(body: new { repository = "acme/orders", @ref = "feature/no-billing" });
        Assert.Equal(("completed", "comparison", "main", false),
            (preview.Text("status"), preview.Text("kind"), preview.Text("trackedRef"), preview["canDecide"]!.GetValue<bool>()));
        Assert.Contains("main", preview.Text("canDecideReason"));

        // It shows what the branch would mean, and changes nothing: no mark, no link, and
        // the proposals of the tracked ref are still pending.
        var proposals = await s.ProposalsAsync(preview.Id());
        Assert.Contains(proposals, p => p.Text("action") == "remove");
        Assert.All(proposals, p => Assert.False(p["canDecide"]!.GetValue<bool>()));
        Assert.All(proposals, p => Assert.False(string.IsNullOrEmpty(p.Text("canDecideReason"))));
        Assert.Equal(accepted, await ModelAsync(s));
        Assert.Equal(links, await DbAsync(db => db.FactLinks.CountAsync(l => l.ProjectId == s.ProjectId)));
        Assert.All(await s.ProposalsAsync(pendingOnMain.Id()), p => Assert.Equal("pending", p.Text("status")));

        var refused = await s.Owner.PostAsJsonAsync(s.Url($"/{preview.Id()}/proposals/decide"), new { accept = proposals.Select(p => p.Id()) });
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("comparison_scan", (await refused.Content.ReadFromJsonAsync<JsonObject>())!.Text("type"));
        Assert.Equal(accepted, await ModelAsync(s));

        // A scan of a bare commit follows no ref and cannot be tracked.
        var bare = await s.ScanAsync(body: new { repository = "acme/orders", @ref = ImportScenario.Commit3 });
        Assert.Equal(("comparison", false), (bare.Text("kind"), bare["canDecide"]!.GetValue<bool>()));
        Assert.Equal(accepted, await ModelAsync(s));
        var untrackable = await s.Owner.PostAsJsonAsync(s.Url($"/{bare.Id()}/track"), new { });
        Assert.Equal("ref_not_trackable", (await untrackable.Content.ReadFromJsonAsync<JsonObject>())!.Text("type"));

        // A person makes the branch the tracked ref. The scan is planned again as a tracking scan.
        var track = await s.Owner.PostAsJsonAsync(s.Url($"/{preview.Id()}/track"), new { });
        Assert.True(track.StatusCode == HttpStatusCode.OK, await track.Content.ReadAsStringAsync());
        var now = (await track.Content.ReadFromJsonAsync<JsonObject>())!;
        var me = (await s.Owner.GetFromJsonAsync<JsonObject>("/api/auth/me"))!.Text("id");
        Assert.Equal(("tracking", "feature/no-billing", true, me),
            (now.Text("kind"), now.Text("trackedRef"), now["canDecide"]!.GetValue<bool>(), now.Text("trackedBy")));
        Assert.NotNull(now["trackedAt"]);
        Assert.Contains("\"stale\":{", await ModelAsync(s));
        var decidable = await s.ProposalsAsync(preview.Id());
        Assert.All(decidable, p => Assert.True(p["canDecide"]!.GetValue<bool>()));
        Assert.Empty((await s.DecideAsync(preview.Id(), accept: decidable.Select(p => p.Id())))["failed"]!.AsArray());

        // The scan of main is now the preview.
        Assert.Equal("comparison", (await s.GetAsync(pendingOnMain.Id())).Text("kind"));
        var again = await s.Owner.PostAsJsonAsync(s.Url($"/{preview.Id()}/track"), new { });
        Assert.Equal("already_tracked", (await again.Content.ReadFromJsonAsync<JsonObject>())!.Text("type"));
    }

    [Fact]
    public async Task A_resumed_scan_whose_stored_files_are_gone_fetches_them_again_and_proposes_no_removal()
    {
        var s = await ImportScenario.CreateAsync(import);
        await s.ScanAndAcceptAllAsync();
        var accepted = await ModelAsync(s);

        // The second scan fails in the middle, after it stored some files.
        s.GitHub.Commit(ImportScenario.Commit2, FixtureGitHub.Load("orders"));
        var served = 0;
        s.GitHub.OnBlob = (_, _) => Task.FromResult(Interlocked.Increment(ref served) == 6
            ? new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("{}") }
            : null);
        await s.SqlAsync($"DELETE FROM import.blobs WHERE workspace_id = {s.WorkspaceId}");
        var id = await s.StartAsync();
        await s.DrainAsync();
        var failed = await s.GetAsync(id);
        Assert.Equal("failed", failed.Text("status"));
        var readBefore = failed["progress"]!["filesRead"]!.GetValue<int>();
        Assert.True(readBefore > 0);
        s.GitHub.OnBlob = null;

        // Something deletes the stored files of the scan.
        await s.SqlAsync($"DELETE FROM import.blobs WHERE workspace_id = {s.WorkspaceId}");
        var before = s.GitHub.BlobRequests.Count;
        Assert.Equal(HttpStatusCode.Accepted, (await s.Owner.PostAsJsonAsync(s.Url($"/{id}/resume"), new { })).StatusCode);
        await s.DrainAsync();

        // Every file is fetched again at the same commit, and the result is that of a whole scan.
        var done = await s.GetAsync(id);
        Assert.Equal(("completed", true, ImportScenario.Commit2), (done.Text("status"), done["isComplete"]!.GetValue<bool>(), done.Text("commitSha")));
        Assert.Equal(done["progress"]!["filesRead"]!.GetValue<int>(), s.GitHub.BlobRequests.Count - before);
        Assert.Equal(done["progress"]!["filesRead"]!.GetValue<int>(), done["coverage"]!["filesExtracted"]!.GetValue<int>());
        Assert.Empty(await s.ProposalsAsync(id));
        Assert.Equal(0, Linked(done, "notCovered"));
        Assert.Equal(accepted, await ModelAsync(s));

        // A scan that stopped longer ago than its files are kept is not resumed.
        var old = await s.StartAsync();
        Assert.Equal(HttpStatusCode.Accepted, (await s.Owner.PostAsJsonAsync(s.Url($"/{old}/cancel"), new { })).StatusCode);
        var longAgo = import.Time.GetUtcNow() - TimeSpan.FromDays(8);
        await s.SqlAsync($"UPDATE import.scans SET updated_at = {longAgo} WHERE id = {old}");
        var expired = await s.Owner.PostAsJsonAsync(s.Url($"/{old}/resume"), new { });
        Assert.Equal(HttpStatusCode.Conflict, expired.StatusCode);
        var body = (await expired.Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.Equal("scan_expired", body.Text("type"));
        Assert.Contains("Start a new scan", body.Text("title"));
    }

    [Fact]
    public async Task Links_made_with_other_extraction_rules_are_moved_and_not_taken_for_deleted_facts()
    {
        var s = await ImportScenario.CreateAsync(import);
        await s.ScanAndAcceptAllAsync();
        var accepted = await ModelAsync(s);
        var keys = await DbAsync(db => db.FactLinks.Where(l => l.ProjectId == s.ProjectId).OrderBy(l => l.FactKey).Select(l => l.Subject + " " + l.FactKey).ToListAsync());

        // The links as an older rule set wrote them: other key shapes (message:OrderCreated,
        // channel:order-events, datastore:postgres), and no known fingerprint. The keys
        // of a message's consumers are in the old shape too.
        await s.SqlAsync($"""
            UPDATE import.fact_links
            SET rule_fingerprint = NULL,
                fact_key = CASE
                    WHEN fact_key LIKE 'message:%' THEN 'message:' || regexp_replace(fact_key, '^.*\.', '')
                    WHEN fact_key LIKE 'channel:amqp:%' THEN 'channel:' || substr(fact_key, 14)
                    WHEN fact_key LIKE 'datastore:.:%' THEN 'datastore:' || substr(fact_key, 13)
                    WHEN subject = 'fact' AND fact_key LIKE 'operation:%' THEN 'v1-' || fact_key
                    ELSE fact_key END
            WHERE project_id = {s.ProjectId}
            """);
        await s.SqlAsync($"UPDATE import.fact_links SET fact_key_hash = encode(sha256(convert_to(fact_key, 'UTF8')), 'hex') WHERE project_id = {s.ProjectId}");
        var rewritten = await DbAsync(db => db.FactLinks.Where(l => l.ProjectId == s.ProjectId).OrderBy(l => l.FactKey).Select(l => l.Subject + " " + l.FactKey).ToListAsync());
        Assert.NotEqual(keys, rewritten);
        Assert.Contains("fact message:OrderCreated", rewritten);
        Assert.Contains("fact channel:order-events", rewritten);

        // The same commit, scanned with the current rules.
        var scan = await s.ScanAsync();
        Assert.Equal(("completed", true), (scan.Text("status"), scan["isComplete"]!.GetValue<bool>()));
        Assert.Equal((keys.Count, 0), (scan["relinked"]!.GetValue<int>(), scan["needsReview"]!.GetValue<int>()));
        Assert.Empty(await s.ProposalsAsync(scan.Id()));
        Assert.Equal(accepted, await ModelAsync(s));
        Assert.DoesNotContain("\"stale\":{", accepted);

        // Every link has its current key again, and the fingerprint of the current rules.
        Assert.Equal(keys, await DbAsync(db => db.FactLinks.Where(l => l.ProjectId == s.ProjectId).OrderBy(l => l.FactKey).Select(l => l.Subject + " " + l.FactKey).ToListAsync()));
        Assert.Equal([RuleSet.CurrentFingerprint], await DbAsync(db => db.FactLinks.Where(l => l.ProjectId == s.ProjectId).Select(l => l.RuleFingerprint).Distinct().ToListAsync()));

        // A link that the exact rule cannot place: another key, evidence somewhere else, and
        // an entity that a person renamed. It is not taken for a deleted fact.
        var billing = (await s.ElementsAsync()).Single("name", "Billing");
        Assert.Equal(HttpStatusCode.OK, (await s.Owner.PatchAsJsonAsync(s.Model($"elements/{billing.Id()}"), new { name = "Invoicing" })).StatusCode);
        var entityId = billing.Id();
        const string elsewhere = """[{"path":"legacy/Billing.vbproj","startLine":1,"endLine":1,"rule":"vb.project","ruleVersion":1}]""";
        await s.SqlAsync($"""
            UPDATE import.fact_links
            SET rule_fingerprint = 'older', fact_key = 'service:legacy-billing',
                fact_key_hash = encode(sha256(convert_to('service:legacy-billing', 'UTF8')), 'hex'),
                evidence = {elsewhere}::jsonb
            WHERE project_id = {s.ProjectId} AND entity_id = {entityId}
            """);
        var before = await ModelAsync(s);
        var review = await s.ScanAsync();
        Assert.Equal(1, review["needsReview"]!.GetValue<int>());
        Assert.Contains(review["notes"]!.AsArray(), n => n!["code"]!.GetValue<string>() == "relink-needs-review");
        var proposals = await s.ProposalsAsync(review.Id());
        Assert.DoesNotContain(proposals, p => p.Text("action") == "remove");
        var relink = Assert.Single(proposals, p => p.Text("action") == "relink");
        Assert.Equal(("element", billing.Text("id"), "service:legacy-billing"), (relink.Text("entityKind"), relink.Text("entityId"), relink.Text("factKey")));
        Assert.Equal(before, await ModelAsync(s));

        // A relink is decided with a candidate or with none, never accepted or rejected as a change.
        var wrong = await s.DecideAsync(review.Id(), accept: [relink.Id()]);
        Assert.Single(wrong["failed"]!.AsArray());
        var decided = await s.DecideAsync(review.Id(), relink: [(relink.Id(), null)]);
        Assert.Empty(decided["failed"]!.AsArray());
        Assert.Equal([relink.Id()], decided["relinked"]!.AsArray().Select(r => r!.GetValue<Guid>()));
        Assert.Equal(before, await ModelAsync(s));
        Assert.Equal(RuleSet.CurrentFingerprint, await DbAsync(db => db.FactLinks.Where(l => l.ProjectId == s.ProjectId && l.EntityId == entityId).Select(l => l.RuleFingerprint).SingleAsync()));
    }
}
