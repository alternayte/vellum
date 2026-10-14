using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vellum.Modules.Import;
using Vellum.Modules.Import.Extraction;
using Vellum.Modules.Import.Proposals;
using Vellum.Modules.Import.Scans;
using Vellum.Tests.Shared.Outbound;
using Vellum.Shared;

namespace Vellum.Tests.Modules.Import;

/// <summary>
/// What the importer keeps, serves and spends: repository text is served only after it was
/// cleared, it does not outlive its project, a resume is not free, the queue is fair, a
/// scan that keeps failing stops, and a removal says what it breaks.
/// </summary>
public class ImportSafetyTests(ImportHost import) : IClassFixture<ImportHost>
{
    private static Task SettingsAsync(ImportScenario s, JsonObject? usage = null)
    {
        var overrides = new JsonObject { ["github"] = new JsonObject { ["apiBaseUrl"] = $"https://{s.GitHubHost}" } };
        if (usage is not null)
            overrides["usage"] = usage;
        return OutboundHost.PutSettingsAsync(s.Owner, s.WorkspaceId, overrides);
    }

    private async Task<T> DbAsync<T>(Func<AppDbContext, Task<T>> read)
    {
        using var scope = import.Host.Factory.Services.CreateScope();
        return await read(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    private static async Task<string?> ErrorAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonObject>())!.Text("type");

    private static HttpResponseMessage ServerError() => new(HttpStatusCode.InternalServerError) { Content = new StringContent("{}") };

    [Fact]
    public async Task Source_text_is_served_only_from_a_completed_scan_for_evidence_lines_and_never_for_a_secret()
    {
        var s = await ImportScenario.CreateAsync(import);
        var files = FixtureGitHub.Load("orders");
        // A Secret manifest whose name does not say so. Only its content gives it away.
        const string planted = "c3VwZXItcGxhbnRlZC12YWx1ZQ==";
        files["deploy/k8s/database.yaml"] = Encoding.UTF8.GetBytes($"apiVersion: v1\nkind: Secret\nmetadata:\n  name: db\ndata:\n  password: {planted}\n");
        s.GitHub.Commit(ImportScenario.Commit1, files);

        // The scan fails after it fetched the manifest and some source files.
        var last = files.Keys.Where(k => k.EndsWith(".cs", StringComparison.Ordinal) || k.EndsWith(".go", StringComparison.Ordinal)).Max(StringComparer.Ordinal)!;
        s.GitHub.OnBlob = (path, _) => Task.FromResult(path == last ? ServerError() : null);
        var id = await s.StartAsync();
        await s.DrainAsync();
        Assert.Equal("failed", (await s.GetAsync(id)).Text("status"));
        Assert.Contains("deploy/k8s/database.yaml", s.GitHub.BlobRequests);

        // The content of the manifest was never stored, in any form.
        Assert.Equal(0, await DbAsync(db => db.Database.SqlQuery<int>(
            $"SELECT count(*)::int AS \"Value\" FROM import.blobs WHERE workspace_id = {s.WorkspaceId} AND content IS NOT NULL AND position(convert_to({planted}, 'UTF8') in content) > 0").SingleAsync()));
        Assert.Equal("secret-file", await DbAsync(db => db.ScanFiles.Where(f => f.ScanId == id && f.Path == "deploy/k8s/database.yaml").Select(f => f.Reason).SingleAsync()));

        // A failed scan serves no text at all.
        foreach (var path in s.GitHub.BlobRequests.Distinct())
        {
            var response = await s.Owner.GetAsync(s.Url($"/{id}/source?path={Uri.EscapeDataString(path)}&startLine=1&endLine=5"));
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Equal("source_not_cleared", await ErrorAsync(response));
        }

        s.GitHub.OnBlob = null;
        Assert.Equal(HttpStatusCode.Accepted, (await s.Owner.PostAsJsonAsync(s.Url($"/{id}/resume"), new { })).StatusCode);
        await s.DrainAsync();
        Assert.Equal("completed", (await s.GetAsync(id)).Text("status"));

        // A completed scan serves the lines of its evidence, and no other window.
        var windows = await s.EvidenceWindowsAsync(id);
        var (evidencePath, start, end) = windows.First(w => w.Path == "orders-api/Program.cs");
        var served = await s.Owner.GetFromJsonAsync<JsonObject>(s.Url($"/{id}/source?path={Uri.EscapeDataString(evidencePath)}&startLine={start}&endLine={end}"));
        Assert.Equal(end - start + 1, served!["lines"]!.AsArray().Count);
        Assert.Equal("not_evidence", await ErrorAsync(await s.Owner.GetAsync(s.Url($"/{id}/source?path={Uri.EscapeDataString(evidencePath)}&startLine=1&endLine=200"))));
        Assert.Equal("not_evidence", await ErrorAsync(await s.Owner.GetAsync(s.Url($"/{id}/source?path={Uri.EscapeDataString(evidencePath)}&startLine={start}&endLine={end + 1}"))));
        Assert.Equal(HttpStatusCode.NotFound, (await s.Owner.GetAsync(s.Url($"/{id}/source?path=deploy/k8s/database.yaml&startLine=1&endLine=6"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await s.Owner.GetAsync(s.Url($"/{id}/source?path=README.md&startLine=1&endLine=2"))).StatusCode);

        // A Viewer reads the source of a public repository. For a private one, or one whose
        // visibility is not known, the Editor role is needed; the evidence itself stays readable.
        using var viewer = await TestHost.SignInMemberAsync(import.Host.Factory, s.Owner, s.WorkspaceId, "Viewer");
        using var editor = await TestHost.SignInMemberAsync(import.Host.Factory, s.Owner, s.WorkspaceId, "Editor");
        var url = s.Url($"/{id}/source?path={Uri.EscapeDataString(evidencePath)}&startLine={start}&endLine={end}");
        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync(url)).StatusCode);
        foreach (var visibility in new bool?[] { true, null })
        {
            await s.SqlAsync($"UPDATE import.scans SET repository_is_private = {visibility} WHERE id = {id}");
            Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync(url)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await editor.GetAsync(url)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync(s.Url($"/{id}/proposals"))).StatusCode);
        }
    }

    [Fact]
    public void A_line_that_holds_a_credential_shows_its_key_and_not_its_value()
    {
        string[] json =
        [
            "{",
            "  \"Logging\": { \"Level\": \"Information\" },",
            "  \"ConnectionStrings\": {",
            "    \"Orders\": \"Host=db;Username=orders;Pwd=planted-one\",",
            "    \"Cache\": \"redis:6379\"",
            "  },",
            "  \"ApiKey\": \"planted-two\",",
            "  \"Name\": \"orders\"",
            "}",
        ];
        var masked = SourceMask.Apply("orders-api/appsettings.json", json);
        Assert.Equal(json[1], masked[1]);
        Assert.Equal(json[2], masked[2]);
        Assert.Equal("    \"Orders\": " + SourceMask.Value, masked[3]);
        Assert.Equal("    \"Cache\": " + SourceMask.Value, masked[4]);
        Assert.Equal("  \"ApiKey\": " + SourceMask.Value, masked[6]);
        Assert.Equal(json[7], masked[7]);
        Assert.DoesNotContain("planted", string.Join("\n", masked));

        string[] yaml =
        [
            "service:",
            "  name: orders",
            "  credentials:",
            "    user: orders",
            "    value: planted-three",
            "  replicas: 2",
            "clientSecret: planted-four",
        ];
        masked = SourceMask.Apply("deploy/values.yaml", yaml);
        Assert.Equal(["service:", "  name: orders", "  credentials:", "    user: " + SourceMask.Value, "    value: " + SourceMask.Value, "  replicas: 2", "clientSecret: " + SourceMask.Value], masked);

        // In a source file the rule for one line applies: a credential-like key loses its value.
        masked = SourceMask.Apply("worker/main.go", ["token = \"planted-five\"", "name := \"orders\""]);
        Assert.Equal(["token = " + SourceMask.Value, "name := \"orders\""], masked);

        // Content is cleared before it is stored: a Secret manifest, a private key, bytes
        // that are not text, and text that is mostly credentials are withheld.
        Assert.Equal("secret-file", ContentClearance.Withhold(Encoding.UTF8.GetBytes("apiVersion: v1\nkind: Secret\n")));
        Assert.Equal("secret-file", ContentClearance.Withhold(Encoding.UTF8.GetBytes("{\n  \"kind\": \"Secret\",\n  \"data\": {}\n}")));
        Assert.Equal("secret-file", ContentClearance.Withhold(Encoding.UTF8.GetBytes("x\n-----BEGIN OPENSSH PRIVATE KEY-----\n")));
        Assert.Equal("binary-content", ContentClearance.Withhold([1, 0, 2]));
        Assert.Equal("binary-content", ContentClearance.Withhold([0xC3, 0x28]));
        Assert.Equal("credential-dense", ContentClearance.Withhold(Encoding.UTF8.GetBytes(string.Join('\n', Enumerable.Range(0, 6).Select(i => $"password{i}=hunter2-planted-{i}")))));
        Assert.Null(ContentClearance.Withhold(Encoding.UTF8.GetBytes("kind: Deployment\nmetadata:\n  name: orders\n")));

        // The bytes must be the blob that the tree names.
        var bytes = Encoding.UTF8.GetBytes("module orders\n");
        Assert.True(ContentClearance.IsBlob(bytes, FixtureGitHub.BlobSha(bytes)));
        Assert.False(ContentClearance.IsBlob(Encoding.UTF8.GetBytes("module evil\n"), FixtureGitHub.BlobSha(bytes)));
    }

    [Fact]
    public async Task Deleting_a_project_deletes_what_was_imported_for_it_and_its_queued_scan_fetches_nothing()
    {
        var s = await ImportScenario.CreateAsync(import);
        await s.ScanAndAcceptAllAsync();
        var queued = await s.StartAsync();
        var requests = s.GitHub.Requests.Count;
        Assert.True(await DbAsync(db => db.Blobs.AnyAsync(b => b.WorkspaceId == s.WorkspaceId && b.Content != null)));

        // The token of the workspace is not needed for any of this: none was ever saved.
        Assert.Equal(HttpStatusCode.NoContent, (await s.Owner.DeleteAsync($"/api/projects/{s.ProjectId}")).StatusCode);

        Assert.Equal((0, 0, 0, 0, 0, 0), await DbAsync(async db => (
            await db.Scans.CountAsync(x => x.ProjectId == s.ProjectId),
            await db.ImportProposals.CountAsync(x => x.ProjectId == s.ProjectId),
            await db.ImportSnapshots.CountAsync(x => x.ProjectId == s.ProjectId),
            await db.FactLinks.CountAsync(x => x.ProjectId == s.ProjectId),
            await db.TrackedRefs.CountAsync(x => x.ProjectId == s.ProjectId),
            await db.Blobs.CountAsync(x => x.WorkspaceId == s.WorkspaceId))));
        Assert.Equal(0, await DbAsync(db => db.ScanFiles.CountAsync(f => f.ScanId == queued)));
        await s.DrainAsync();
        Assert.Equal(requests, s.GitHub.Requests.Count);

        // A project that disappears without the cleanup: the worker's retention run does it.
        var other = await ImportScenario.CreateAsync(import);
        await other.ScanAndAcceptAllAsync();
        var waiting = await other.StartAsync();
        var otherRequests = other.GitHub.Requests.Count;
        await other.SqlAsync($"DELETE FROM workspaces.projects WHERE id = {other.ProjectId}");

        // Its queued scan is taken, finds no project, and asks GitHub for nothing.
        await other.DrainAsync();
        Assert.Equal(otherRequests, other.GitHub.Requests.Count);
        Assert.Equal(0, await DbAsync(db => db.Scans.CountAsync(x => x.Id == waiting)));

        var third = await ImportScenario.CreateAsync(import);
        await third.ScanAndAcceptAllAsync();
        await third.SqlAsync($"DELETE FROM workspaces.projects WHERE id = {third.ProjectId}");
        await third.Worker.RunRetentionAsync(CancellationToken.None);
        await third.Worker.RunRetentionAsync(CancellationToken.None);
        Assert.Equal((0, 0, 0), await DbAsync(async db => (
            await db.Scans.CountAsync(x => x.ProjectId == third.ProjectId),
            await db.FactLinks.CountAsync(x => x.ProjectId == third.ProjectId),
            await db.Blobs.CountAsync(x => x.WorkspaceId == third.WorkspaceId))));
    }

    [Fact]
    public async Task A_resume_that_asks_GitHub_again_counts_as_a_scan_of_the_day_and_resumes_are_bounded()
    {
        var s = await ImportScenario.CreateAsync(import);
        await SettingsAsync(s, new JsonObject { ["maxScansPerDay"] = 1 });
        var served = 0;
        s.GitHub.OnBlob = (_, _) => Task.FromResult(Interlocked.Increment(ref served) == 3 ? ServerError() : null);
        var id = await s.StartAsync();
        await s.DrainAsync();
        Assert.Equal("failed", (await s.GetAsync(id)).Text("status"));
        s.GitHub.OnBlob = null;

        // The scan has files left to fetch, and the day's one scan is used.
        var refused = await s.Owner.PostAsJsonAsync(s.Url($"/{id}/resume"), new { });
        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        Assert.Equal("scan_limit_reached", await ErrorAsync(refused));
        Assert.Equal("failed", (await s.GetAsync(id)).Text("status"));

        await SettingsAsync(s, new JsonObject { ["maxScansPerDay"] = 2 });
        Assert.Equal(HttpStatusCode.Accepted, (await s.Owner.PostAsJsonAsync(s.Url($"/{id}/resume"), new { })).StatusCode);
        Assert.Equal(2, await DbAsync(db => db.ScanCharges.CountAsync(c => c.WorkspaceId == s.WorkspaceId)));
        // The resume was the second unit of the day.
        var third = await s.Owner.PostAsJsonAsync(s.Url(), new { repository = "acme/other" });
        Assert.Equal(HttpStatusCode.TooManyRequests, third.StatusCode);
        await s.DrainAsync();
        Assert.Equal("completed", (await s.GetAsync(id)).Text("status"));

        // A scan that has everything stored resumes without a unit. A person resumes one scan a few times only.
        await SettingsAsync(s, new JsonObject { ["maxScansPerDay"] = 3 });
        var stuck = await s.StartAsync();
        await s.SqlAsync($"UPDATE import.scans SET limits = 'null'::jsonb WHERE id = {stuck}");
        Assert.Equal(3, await DbAsync(db => db.ScanCharges.CountAsync(c => c.WorkspaceId == s.WorkspaceId)));
        Assert.Equal(HttpStatusCode.Accepted, (await s.Owner.PostAsJsonAsync(s.Url($"/{stuck}/cancel"), new { })).StatusCode);
        await s.SqlAsync($"UPDATE import.scans SET manual_resumes = {ScanRequests.MaxManualResumes} WHERE id = {stuck}");
        var bounded = await s.Owner.PostAsJsonAsync(s.Url($"/{stuck}/resume"), new { });
        Assert.Equal(HttpStatusCode.Conflict, bounded.StatusCode);
        Assert.Equal("resume_limit_reached", await ErrorAsync(bounded));
    }

    [Fact]
    public async Task The_queue_serves_workspaces_in_turn_and_a_scan_that_keeps_failing_stops()
    {
        // Nothing of an earlier test is left in the queue.
        var busy = await ImportScenario.CreateAsync(import);
        await busy.DrainAsync();

        // One workspace queues three scans, then another queues one.
        var quiet = await ImportScenario.CreateAsync(import);
        var first = await busy.StartAsync("acme/one");
        var second = await busy.StartAsync("acme/two");
        var third = await busy.StartAsync("acme/three");
        var only = await quiet.StartAsync();

        Assert.True(await busy.Worker.RunOnceAsync("worker-a", CancellationToken.None));
        Assert.True(await busy.Worker.RunOnceAsync("worker-a", CancellationToken.None));
        Assert.Equal("completed", (await busy.GetAsync(first)).Text("status"));
        Assert.Equal("completed", (await quiet.GetAsync(only)).Text("status"));
        Assert.Equal(("queued", "queued"), ((await busy.GetAsync(second)).Text("status"), (await busy.GetAsync(third)).Text("status")));
        await busy.DrainAsync();

        // A scan that an unexpected error stops is tried again later, with a longer wait
        // each time, and fails for good after five such runs.
        var poison = await quiet.StartAsync("acme/poison");
        await quiet.SqlAsync($"UPDATE import.scans SET limits = 'null'::jsonb WHERE id = {poison}");
        var waits = new List<TimeSpan>();
        for (var run = 1; run <= 4; run++)
        {
            Assert.True(await quiet.Worker.RunOnceAsync("worker-a", CancellationToken.None));
            var waiting = await quiet.GetAsync(poison);
            Assert.Equal(("waiting", "internal_error"), (waiting.Text("status"), waiting["error"]!["code"]!.GetValue<string>()));
            waits.Add(waiting["retryAt"]!.GetValue<DateTimeOffset>() - import.Time.GetUtcNow());
            // Before the wait is over nobody takes it.
            Assert.False(await quiet.Worker.RunOnceAsync("worker-a", CancellationToken.None));
            import.Time.Advance(waits[^1] + TimeSpan.FromSeconds(1));
        }
        Assert.Equal([ScanWorker.Backoff(1), ScanWorker.Backoff(2), ScanWorker.Backoff(3), ScanWorker.Backoff(4)], waits.Select(w => TimeSpan.FromSeconds(Math.Round(w.TotalSeconds))));
        Assert.True(waits[3] > waits[0]);
        Assert.Equal(ScanWorker.MaxBackoff, ScanWorker.Backoff(30));

        Assert.True(await quiet.Worker.RunOnceAsync("worker-a", CancellationToken.None));
        var failed = await quiet.GetAsync(poison);
        Assert.Equal(("failed", "internal_error"), (failed.Text("status"), failed["error"]!["code"]!.GetValue<string>()));
        Assert.Null(failed["retryAt"]);
        Assert.False(await quiet.Worker.RunOnceAsync("worker-a", CancellationToken.None));

        // A scan whose worker stopped without a word twice in a row is not taken a third time.
        var crashing = await quiet.StartAsync("acme/crashing");
        var asked = quiet.GitHub.Requests.Count;
        await quiet.SqlAsync($"UPDATE import.scans SET status = 'running', lease_owner = 'gone', lease_token = 1, crashes = 1, lease_expires_at = now() - interval '1 minute' WHERE id = {crashing}");
        Assert.False(await quiet.Worker.RunOnceAsync("worker-a", CancellationToken.None));
        var crashed = await quiet.GetAsync(crashing);
        Assert.Equal(("failed", "worker_crashed"), (crashed.Text("status"), crashed["error"]!["code"]!.GetValue<string>()));
        Assert.Equal(asked, quiet.GitHub.Requests.Count);

        // A run that lost the scan writes nothing: its fencing token is the old one.
        await quiet.SqlAsync($"DELETE FROM import.blobs WHERE workspace_id = {quiet.WorkspaceId}");
        var fenced = await quiet.StartAsync("acme/fenced");
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        quiet.GitHub.OnBlob = async (_, ct) =>
        {
            reached.TrySetResult();
            await gate.Task.WaitAsync(ct);
            return null;
        };
        var slow = quiet.Worker.RunOnceAsync("worker-a", CancellationToken.None);
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(30));
        // Another worker takes the scan over: the token grows by one.
        await quiet.SqlAsync($"UPDATE import.scans SET lease_owner = 'worker-b', lease_token = lease_token + 1 WHERE id = {fenced}");
        gate.TrySetResult();
        Assert.True(await slow.WaitAsync(TimeSpan.FromSeconds(30)));
        quiet.GitHub.OnBlob = null;
        var held = await quiet.GetAsync(fenced);
        Assert.Equal(("running", 0), (held.Text("status"), held["progress"]!["filesRead"]!.GetValue<int>()));
        Assert.Equal("worker-b", await DbAsync(db => db.Scans.Where(x => x.Id == fenced).Select(x => x.LeaseOwner).SingleAsync()));
        await quiet.SqlAsync($"UPDATE import.scans SET status = 'cancelled', lease_owner = NULL WHERE id = {fenced}");
    }

    [Fact]
    public async Task A_removal_names_what_it_breaks_and_needs_an_acknowledgement()
    {
        var s = await ImportScenario.CreateAsync(import);
        await s.ScanAndAcceptAllAsync();
        var elements = await s.ElementsAsync();
        var billing = elements.Single("name", "Billing");
        var store = elements.Single("name", "postgres");

        // A stored scenario in which the billing service writes to the store.
        var scenarioId = Guid.NewGuid();
        var scenario = new JsonObject
        {
            ["id"] = scenarioId, ["name"] = "Billing records an invoice",
            ["steps"] = new JsonArray(new JsonObject
            {
                ["type"] = "write", ["id"] = "record", ["by"] = billing.Text("id"), ["store"] = store.Text("id"),
                ["dataset"] = "invoices", ["change"] = new JsonObject { ["status"] = "open" },
            }),
            ["faults"] = new JsonArray(), ["assertions"] = new JsonArray(),
        };
        var saved = await s.Owner.PostAsJsonAsync(s.Model("scenarios"), scenario);
        Assert.True(saved.StatusCode == HttpStatusCode.Created, await saved.Content.ReadAsStringAsync());

        // The next commit has no billing service.
        var files = FixtureGitHub.Load("orders");
        foreach (var path in files.Keys.Where(p => p.StartsWith("billing/", StringComparison.Ordinal)).ToList())
            files.Remove(path);
        s.GitHub.Commit(ImportScenario.Commit2, files);
        var scan = await s.ScanAsync();
        var proposals = await s.ProposalsAsync(scan.Id());

        var removal = proposals.Single(p => p.Text("action") == "remove" && p.Text("entityKind") == "element");
        var impact = removal["impact"]!;
        var broken = Assert.Single(impact["scenarios"]!.AsArray())!;
        Assert.Equal((scenarioId, "Billing records an invoice"), (broken["id"]!.GetValue<Guid>(), broken["name"]!.GetValue<string>()));
        Assert.Equal(["record"], broken["stepIds"]!.AsArray().Select(step => step!.GetValue<string>()));
        Assert.Contains(impact["entities"]!.AsArray(), e => e!["entityKind"]!.GetValue<string>() == "subscription");
        Assert.Contains(impact["entities"]!.AsArray(), e => e!["entityKind"]!.GetValue<string>() == "message" && e["name"]!.GetValue<string>() == "OrderCreated");
        // A removal that nothing depends on has no impact.
        Assert.Null(proposals.Single(p => p.Text("action") == "remove" && p.Text("entityKind") == "subscription")["impact"]);

        // Without the acknowledgement nothing is applied, and the scenario is named.
        var before = await s.Owner.GetStringAsync(s.Model("elements"));
        var refused = await s.DecideAsync(scan.Id(), accept: proposals.Select(p => p.Id()));
        var failure = Assert.Single(refused["failed"]!.AsArray())!;
        Assert.Equal(removal.Id(), failure["id"]!.GetValue<Guid>());
        Assert.Contains("Billing records an invoice", failure["reason"]!.GetValue<string>());
        Assert.Contains("record", failure["reason"]!.GetValue<string>());
        Assert.Contains("acknowledgeImpact", failure["reason"]!.GetValue<string>());
        Assert.Empty(refused["accepted"]!.AsArray());
        Assert.Equal(before, await s.Owner.GetStringAsync(s.Model("elements")));

        var accepted = await s.DecideAsync(scan.Id(), accept: proposals.Select(p => p.Id()), acknowledgeImpact: true);
        Assert.Empty(accepted["failed"]!.AsArray());
        Assert.DoesNotContain(await s.ElementsAsync(), e => e.Text("name") == "Billing");
    }

    [Fact]
    public async Task A_large_proposal_is_cut_in_the_list_and_whole_by_its_ID_and_a_renamed_repository_keeps_its_links()
    {
        var s = await ImportScenario.CreateAsync(import);
        var scan = await s.ScanAsync();
        var schema = (await s.ProposalsAsync(scan.Id())).First(p => p.Text("entityKind") == "schema");
        var schemaId = schema.Id();
        await s.SqlAsync($"UPDATE import.proposals SET proposed = jsonb_set(proposed, '{{content}}', to_jsonb(repeat('x', 60000))) WHERE id = {schemaId}");

        var listed = (await s.ProposalsAsync(scan.Id())).Single(p => p.Id() == schemaId);
        Assert.True(listed["truncated"]!.GetValue<bool>());
        Assert.True(listed["proposed"]!["content"]!.GetValue<string>().Length <= ProposalReads.MaxListedValue + 1);
        Assert.All((await s.ProposalsAsync(scan.Id())).Where(p => p.Id() != schemaId), p => Assert.False(p["truncated"]!.GetValue<bool>()));
        var whole = (await s.Owner.GetFromJsonAsync<JsonObject>(s.Url($"/{scan.Id()}/proposals/{schemaId}")))!;
        Assert.Equal((false, 60000), (whole["truncated"]!.GetValue<bool>(), whole["proposed"]!["content"]!.GetValue<string>().Length));
        Assert.Equal(HttpStatusCode.NotFound, (await s.Owner.GetAsync(s.Url($"/{scan.Id()}/proposals/{Guid.NewGuid()}"))).StatusCode);
        await s.SqlAsync($"UPDATE import.proposals SET proposed = jsonb_set(proposed, '{{content}}', to_jsonb('{{}}'::text)) WHERE id = {schemaId}");

        // The repository is accepted, then renamed on GitHub: the same ID, another name.
        var second = await ImportScenario.CreateAsync(import);
        await second.ScanAndAcceptAllAsync();
        var links = await DbAsync(db => db.FactLinks.CountAsync(l => l.ProjectId == second.ProjectId && l.Repository == "acme/orders" && l.RepositoryId != null));
        Assert.True(links > 0);
        second.GitHub.RepositoryIds["acme/orders"] = 4242;
        await second.SqlAsync($"UPDATE import.fact_links SET repository_id = 4242 WHERE project_id = {second.ProjectId}");
        second.GitHub.FullNames["acme/orders"] = "Acme/Orders-Service";

        var renamed = await second.ScanAsync();
        Assert.Equal(("completed", "acme/orders-service", "Acme/Orders-Service"), (renamed.Text("status"), renamed.Text("repository"), renamed.Text("repositoryName")));
        Assert.Equal(links, await DbAsync(db => db.FactLinks.CountAsync(l => l.ProjectId == second.ProjectId && l.Repository == "acme/orders-service")));
        Assert.Equal("tracking", renamed.Text("kind"));
        var proposals = await second.ProposalsAsync(renamed.Id());
        Assert.DoesNotContain(proposals, p => p.Text("action") is "add" or "remove");
        Assert.DoesNotContain("\"stale\":{", await second.Owner.GetStringAsync(second.Model("elements")));
    }

    [Fact]
    public void Planning_twenty_thousand_facts_against_five_thousand_entities_is_linear_and_refuses_text_that_is_not_text()
    {
        const string repository = "github.com/acme/big";
        var rule = EvidenceExtractor.Rules[0];
        ExtractedFact Schema(int i, string name) => new(
            FactKind.Schema, $"schema:big.yaml#/{i}", name,
            new Dictionary<string, string> { ["schema"] = $$"""{"title":"{{i}}"}""" },
            [new SourceEvidence($"schemas/{i % 200}.yaml", i + 1, i + 1, rule.Id, rule.Version)]);

        var facts = Enumerable.Range(0, 20_000).Select(i => Schema(i, $"Schema {i}")).ToList();
        // 5,000 entities: half of them linked to a fact, half hand-made with the name of one.
        var entities = Enumerable.Range(0, 5_000).Select(i => new SchemaRow(Guid.NewGuid(), $"Schema {i}", $$"""{"title":"{{i}}"}""")).ToList();
        var links = entities.Take(2_500).Select((e, i) => new LinkInfo(
            Guid.NewGuid(), facts[i].Key, EntityKinds.Schema, e.Id, new string('1', 40), RuleSet.CurrentFingerprint,
            new Dictionary<string, string>(), [], new Dictionary<string, string?> { ["name"] = e.Name, ["content"] = e.Content },
            [FactMapping.Ref(facts[i].Evidence[0])])).ToList();

        var model = ModelIndex.From(Vellum.Modules.Modelling.Model.ModelState.Initial, entities);
        PlanInput Input(IReadOnlyList<ExtractedFact> input, IReadOnlyList<LinkInfo> accepted, ScanCoverage coverage) => new(
            repository, new string('2', 40), RuleSet.CurrentFingerprint, input, model, accepted,
            accepted.Select(l => l.EntityId).ToHashSet(), new HashSet<(string, string)>(), coverage);

        var timer = Stopwatch.StartNew();
        var plan = ProposalPlanner.Compute(Input(facts, links, ScanCoverage.Everything), Guid.NewGuid);
        timer.Stop();
        // A linked schema that agrees has no proposal; a hand-made one with the same name is
        // linked by a change; the other 15,000 facts are additions.
        Assert.Equal(15_000, plan.Proposals.Count(p => p.Action == ProposalAction.Add));
        Assert.Equal(2_500, plan.Proposals.Count(p => p.Action == ProposalAction.Change));
        Assert.Equal(2_500, plan.Coverage.Covered);
        // The quadratic planner needed a minute for this. Ten seconds is far above what a
        // linear one needs on any machine, and far below what a quadratic one can do.
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(10), $"Planning took {timer.Elapsed}.");

        // All links with other rules, all facts with other keys: the relink is linear too.
        var old = links.Select(l => l with { FactKey = "v1:" + l.FactKey, RuleFingerprint = null }).ToList();
        timer.Restart();
        var moved = ProposalPlanner.Compute(Input(facts, old, ScanCoverage.Everything), Guid.NewGuid);
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(10), $"Relinking took {timer.Elapsed}.");
        Assert.Equal((2_500, 0), (moved.Relinked.Count, moved.NeedsReview));
        Assert.DoesNotContain(moved.Proposals, p => p.Action == ProposalAction.Remove);
        Assert.Empty(moved.MarkStale);

        // Two facts that both fit one old link: nothing moves, nothing is removed, no copy is added.
        var twin = new List<ExtractedFact> { Schema(0, "Schema 0"), Schema(0, "Schema 0") with { Key = "schema:other.yaml#/0" } };
        var ambiguous = ProposalPlanner.Compute(Input(twin, [old[0]], ScanCoverage.Everything), Guid.NewGuid);
        Assert.Equal((0, 1), (ambiguous.Relinked.Count, ambiguous.NeedsReview));
        var review = Assert.Single(ambiguous.Proposals);
        Assert.Equal(ProposalAction.Relink, review.Action);
        Assert.Equal(2, review.Proposed!["candidates"]!.AsArray().Count);
        Assert.Empty(ambiguous.MarkStale);
        Assert.All(ambiguous.Facts, f => Assert.Equal((FactDisposition.Blocked, ProposalPlanner.RelinkPending), (f.Disposition, f.Reason)));

        // A fact whose text is not text is not kept, and its link is not taken for deleted.
        var hostile = new ExtractedFact(FactKind.Schema, links[1].FactKey, "Evil\0name", facts[1].Attributes, facts[1].Evidence);
        Assert.Equal("name", FactMapping.Refusal(hostile));
        Assert.Equal("key", FactMapping.Refusal(hostile with { Name = "ok", Key = "a\u0001b" }));
        Assert.Equal("key", FactMapping.Refusal(hostile with { Name = "ok", Key = new string('k', FactMapping.MaxFactKey + 1) }));
        Assert.Equal("attribute", FactMapping.Refusal(hostile with { Name = "ok", Attributes = new Dictionary<string, string> { ["a"] = "x\0y" } }));
        Assert.Null(FactMapping.Refusal(facts[1]));
        var coverage = new ScanCoverage(new Dictionary<string, string?> { ["schemas/1.yaml"] = null }, _ => null, null);
        var refused = ProposalPlanner.Compute(Input([hostile], [links[1]], coverage), Guid.NewGuid);
        Assert.Single(refused.Refused);
        Assert.Empty(refused.Facts);
        Assert.Empty(refused.Proposals);
        Assert.Empty(refused.MarkStale);
        Assert.Equal((0, 1), (refused.Coverage.Covered, refused.Coverage.Failed));
    }

    // The plan has 20,000 facts, so that writing it takes long enough to overlap model writes.
    [Fact]
    [Trait("Category", "Slow")]
    public async Task Writing_a_large_plan_does_not_hold_up_a_write_to_the_model()
    {
        var s = await ImportScenario.CreateAsync(import);
        var id = await s.StartAsync();
        var commit = new string('7', 40);
        await s.SqlAsync($"UPDATE import.scans SET status = 'running', lease_token = 9, commit_sha = {commit}, resolved_ref = 'main', tree_listed = true WHERE id = {id}");

        var rule = EvidenceExtractor.Rules[0];
        var facts = Enumerable.Range(0, 20_000).Select(i => new ExtractedFact(
            FactKind.Schema, $"schema:big.yaml#/{i}", $"Schema {i}",
            new Dictionary<string, string> { ["schema"] = $$"""{"title":"{{i}}"}""" },
            [new SourceEvidence("big.yaml", i + 1, i + 1, rule.Id, rule.Version)])).ToList();

        using var scope = import.Host.Factory.Services.CreateScope();
        var scan = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Scans.AsNoTracking().SingleAsync(x => x.Id == id);
        scope.ServiceProvider.GetRequiredService<Deedbox.DeedboxContext>().Metadata = new Deedbox.EventMetadata { Actor = "system:import-scan" };
        var input = new SnapshotInput(facts, true, null, "[]", () => ScanCoverage.Everything, 1);

        // The plan is written while a person adds elements to the model.
        var writing = SnapshotWriter.WriteAsync(scope.ServiceProvider, scan, WriteMode.Complete(9), input, import.Time.GetUtcNow(), CancellationToken.None);
        var slowest = TimeSpan.Zero;
        var writes = 0;
        while (!writing.IsCompleted && writes < 200)
        {
            var timer = Stopwatch.StartNew();
            var created = await s.Owner.PostAsJsonAsync(s.Model("elements"), new { id = Guid.NewGuid(), name = $"During the scan {writes++}", kind = "system" });
            timer.Stop();
            Assert.True(created.StatusCode == HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
            slowest = timer.Elapsed > slowest ? timer.Elapsed : slowest;
            await Task.Delay(250);
        }
        var outcome = await writing;

        // No write waited for the planning or for the 40,000 rows of the plan.
        Assert.True(writes > 0);
        Assert.True(slowest < TimeSpan.FromSeconds(5), $"A model write took {slowest}.");
        // The scan either wrote a plan for the model as it is, or said that the model kept changing.
        Assert.Contains(outcome, new[] { SnapshotOutcome.Written, SnapshotOutcome.ModelBusy });
        var after = await s.GetAsync(id);
        if (outcome == SnapshotOutcome.Written)
            Assert.Equal(("completed", 20_000), (after.Text("status"), after["proposalCounts"]!["pending"]!.GetValue<int>()));
        else
            Assert.Equal(("running", 0), (after.Text("status"), after["proposalCounts"]!["pending"]!.GetValue<int>()));
    }
}
