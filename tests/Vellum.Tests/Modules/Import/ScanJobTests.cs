using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vellum.Modules.Import;
using Vellum.Modules.Import.Extraction;
using Vellum.Tests.Shared.Outbound;

namespace Vellum.Tests.Modules.Import;

/// <summary>
/// The scan as a durable job: it waits for GitHub's rate limit, survives the death of its
/// worker, runs on one worker at a time, stops when asked, respects the daily limit, and
/// reports each failure with what to do. Counting the stub's requests shows what was, and
/// what was never, asked of GitHub.
/// </summary>
public class ScanJobTests(ImportHost import) : IClassFixture<ImportHost>
{
    private const string Token = "ghp_fixtureTOKEN0123456789abcdefghijABCD";
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    private static int Count(ImportScenario s, string prefix) => s.GitHub.Requests.Count(r => r.StartsWith(prefix, StringComparison.Ordinal));

    /// <summary>Makes the stub hold the fourth blob until the gate opens or the caller gives up.</summary>
    private static (Task Reached, TaskCompletionSource Gate) HoldFourthBlob(ImportScenario s)
    {
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var served = 0;
        s.GitHub.OnBlob = async (_, ct) =>
        {
            if (Interlocked.Increment(ref served) == 4)
            {
                reached.TrySetResult();
                await gate.Task.WaitAsync(ct);
            }
            return null;
        };
        return (reached.Task, gate);
    }

    private async Task<string> ImportRowsAsync()
    {
        using var scope = import.Host.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ImportDbContext>();
        var text = new List<string>();
        foreach (var table in new[] { "scans", "scan_files", "snapshots", "snapshot_facts", "proposals", "fact_links", "rejections" })
        {
#pragma warning disable EF1002 // The table names are the literals above.
            text.AddRange(await db.Database.SqlQueryRaw<string>($"SELECT t::text AS \"Value\" FROM import.{table} t").ToListAsync());
#pragma warning restore EF1002
        }
        text.AddRange(await db.Database.SqlQueryRaw<string>("SELECT convert_from(content, 'UTF8') AS \"Value\" FROM import.blobs WHERE content IS NOT NULL AND position('\\000'::bytea in content) = 0").ToListAsync());
        return string.Join("\n", text);
    }

    [Fact]
    public async Task A_rate_limit_pauses_the_scan_and_it_goes_on_without_asking_for_a_stored_blob_again()
    {
        var s = await ImportScenario.CreateAsync(import);
        var reset = import.Time.GetUtcNow() + TimeSpan.FromMinutes(10);
        var served = 0;
        s.GitHub.OnBlob = (_, _) => Task.FromResult(Interlocked.Increment(ref served) == 4 ? FixtureGitHub.RateLimited(reset) : null);

        var id = await s.StartAsync();
        await s.DrainAsync();
        var waiting = await s.GetAsync(id);
        Assert.Equal(("waiting", false), (waiting.Text("status"), waiting["isComplete"]!.GetValue<bool>()));
        Assert.InRange(waiting["retryAt"]!.GetValue<DateTimeOffset>(), reset - TimeSpan.FromSeconds(2), reset + TimeSpan.FromSeconds(2));
        Assert.Equal(3, waiting["progress"]!["filesRead"]!.GetValue<int>());
        Assert.Null(waiting["error"]);
        Assert.Empty(await s.ProposalsAsync(id));

        // Before the reset no worker takes it. After the reset it resumes by itself.
        Assert.False(await s.Worker.RunOnceAsync("worker-b", CancellationToken.None));
        import.Time.Advance(TimeSpan.FromMinutes(11));
        await s.DrainAsync("worker-b");

        var done = await s.GetAsync(id);
        Assert.Equal(("completed", true, ImportScenario.Commit1), (done.Text("status"), done["isComplete"]!.GetValue<bool>(), done.Text("commitSha")));
        Assert.Null(done["retryAt"]);
        Assert.Equal(21, (await s.ProposalsAsync(id)).Count);

        // Only the refused blob was asked for twice; the ref and the file list once.
        var blobs = s.GitHub.BlobRequests;
        Assert.Equal(blobs.Distinct().Count() + 1, blobs.Count);
        Assert.Equal(done["progress"]!["filesRead"]!.GetValue<int>(), blobs.Distinct().Count());
        Assert.Equal((1, 1, 1), (Count(s, "repository"), Count(s, "ref:"), Count(s, "tree:")));
    }

    [Fact]
    public async Task A_scan_whose_worker_died_is_taken_over_from_its_checkpoint_at_the_same_commit()
    {
        var s = await ImportScenario.CreateAsync(import);
        var (reached, _) = HoldFourthBlob(s);
        var id = await s.StartAsync();

        // The first worker dies in the middle of a request: its task is cancelled, as when
        // the process ends, and it writes nothing more.
        using var death = new CancellationTokenSource();
        var first = s.Worker.RunOnceAsync("worker-a", death.Token);
        await reached.WaitAsync(Patience);
        await death.CancelAsync();
        Assert.True(await first.WaitAsync(Patience));

        var orphan = await s.GetAsync(id);
        Assert.Equal(("running", ImportScenario.Commit1, 3), (orphan.Text("status"), orphan.Text("commitSha"), orphan["progress"]!["filesRead"]!.GetValue<int>()));

        // The branch moves on. The lease has not run out, so nobody takes the scan yet.
        s.GitHub.OnBlob = null;
        s.GitHub.Commit(ImportScenario.Commit2, FixtureGitHub.Load("orders-outbox-removed"));
        Assert.False(await s.Worker.RunOnceAsync("worker-b", CancellationToken.None));

        Assert.Equal(1, await s.ExpireLeaseAsync(id));
        Assert.True(await s.Worker.RunOnceAsync("worker-b", CancellationToken.None));

        // The second worker finished the scan of the first commit, with the three blobs the
        // first one stored, and resolved nothing again.
        var done = await s.GetAsync(id);
        Assert.Equal(("completed", true, ImportScenario.Commit1), (done.Text("status"), done["isComplete"]!.GetValue<bool>(), done.Text("commitSha")));
        var blobs = s.GitHub.BlobRequests;
        Assert.Equal(blobs.Distinct().Count() + 1, blobs.Count);
        Assert.Equal((1, 1, 1), (Count(s, "repository"), Count(s, "ref:"), Count(s, "tree:")));
        Assert.Equal($"tree:{ImportScenario.Commit1}", s.GitHub.Requests.Single(r => r.StartsWith("tree:", StringComparison.Ordinal)));
        Assert.Contains("Total:decimal", (await s.ProposalsAsync(id)).Proposal("message", "OrderCreated")["proposed"]!["observed"]!["fields"]!.GetValue<string>());
    }

    [Fact]
    public async Task Two_workers_do_not_run_the_same_scan()
    {
        var s = await ImportScenario.CreateAsync(import);
        var id = await s.StartAsync();

        var ran = await Task.WhenAll(
            s.Worker.RunOnceAsync("worker-a", CancellationToken.None),
            s.Worker.RunOnceAsync("worker-b", CancellationToken.None));

        Assert.Single(ran, took => took);
        Assert.Equal("completed", (await s.GetAsync(id)).Text("status"));
        Assert.Equal(s.GitHub.BlobRequests.Distinct().Count(), s.GitHub.BlobRequests.Count);
        Assert.Equal(1, Count(s, "tree:"));
        Assert.Equal(21, (await s.ProposalsAsync(id)).Count);
    }

    [Fact]
    public async Task A_scan_can_be_cancelled_while_it_waits_and_while_it_runs_and_resumed_from_its_checkpoint()
    {
        var s = await ImportScenario.CreateAsync(import);

        // Queued: cancelled at once, and no worker takes it.
        var queued = await s.StartAsync();
        Assert.Equal(HttpStatusCode.Accepted, (await s.Owner.PostAsync(s.Url($"/{queued}/cancel"), null)).StatusCode);
        Assert.Equal("cancelled", (await s.GetAsync(queued)).Text("status"));
        await s.DrainAsync();
        Assert.Empty(s.GitHub.Requests);
        Assert.Equal(HttpStatusCode.Conflict, (await s.Owner.PostAsync(s.Url($"/{queued}/cancel"), null)).StatusCode);

        // Running: the request ends the call to GitHub that is in flight.
        var (reached, _) = HoldFourthBlob(s);
        var id = await s.StartAsync();
        var run = s.Worker.RunOnceAsync("worker-a", CancellationToken.None);
        await reached.WaitAsync(Patience);
        Assert.Equal(HttpStatusCode.Conflict, (await s.Owner.PostAsync(s.Url($"/{id}/resume"), null)).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await s.Owner.PostAsync(s.Url($"/{id}/cancel"), null)).StatusCode);
        Assert.True(await run.WaitAsync(Patience));

        var cancelled = await s.GetAsync(id);
        Assert.Equal(("cancelled", false, 3), (cancelled.Text("status"), cancelled["isComplete"]!.GetValue<bool>(), cancelled["progress"]!["filesRead"]!.GetValue<int>()));
        Assert.NotNull(cancelled["completedAt"]);
        Assert.Empty(await s.ProposalsAsync(id));

        // Resumed: it goes on with the blobs it has.
        s.GitHub.OnBlob = null;
        Assert.Equal(HttpStatusCode.Accepted, (await s.Owner.PostAsync(s.Url($"/{id}/resume"), null)).StatusCode);
        Assert.Equal("queued", (await s.GetAsync(id)).Text("status"));
        await s.DrainAsync();
        Assert.Equal("completed", (await s.GetAsync(id)).Text("status"));
        Assert.Equal(s.GitHub.BlobRequests.Distinct().Count() + 1, s.GitHub.BlobRequests.Count);

        // A completed scan is neither cancelled nor resumed. The first one stays cancelled.
        Assert.Equal(HttpStatusCode.Conflict, (await s.Owner.PostAsync(s.Url($"/{id}/cancel"), null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await s.Owner.PostAsync(s.Url($"/{id}/resume"), null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await s.Owner.PostAsync(s.Url($"/{Guid.NewGuid()}/cancel"), null)).StatusCode);
        Assert.Equal(["cancelled", "completed"], (await s.AllAsync(s.Url())).Values("status"));
    }

    [Fact]
    public async Task The_daily_limit_and_a_scan_in_progress_refuse_a_new_scan_and_say_why()
    {
        var s = await ImportScenario.CreateAsync(import);
        await OutboundHost.PutSettingsAsync(s.Owner, s.WorkspaceId, new JsonObject
        {
            ["github"] = new JsonObject { ["apiBaseUrl"] = $"https://{s.GitHubHost}" },
            ["usage"] = new JsonObject { ["maxScansPerDay"] = 2 },
        });

        var first = await s.StartAsync("acme/orders");

        // One scan of a repository per project at a time. The refusal does not count.
        var twice = await s.Owner.PostAsJsonAsync(s.Url(), new { repository = "ACME/orders" });
        Assert.Equal(HttpStatusCode.Conflict, twice.StatusCode);
        Assert.Equal("scan_in_progress", (await twice.Content.ReadFromJsonAsync<JsonObject>())!.Text("type"));

        var second = await s.StartAsync("acme/inventory");
        var third = await s.Owner.PostAsJsonAsync(s.Url(), new { repository = "acme/billing" });
        Assert.Equal(HttpStatusCode.TooManyRequests, third.StatusCode);
        var refusal = (await third.Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.Equal("scan_limit_reached", refusal.Text("type"));
        Assert.Contains("usage.maxScansPerDay is 2", refusal.Text("title"));

        // Requests that name no repository, or one on another host.
        foreach (var body in new object[]
                 {
                     new { repository = "orders" }, new { repository = "a/b/c" }, new { repository = "../orders" },
                     new { repository = "https://github.example/acme/orders" }, new { },
                     new { repository = "acme/orders", @ref = "bad ref" }, new { repository = "acme/orders", includePaths = new[] { "../etc" } },
                 })
            Assert.Equal(HttpStatusCode.BadRequest, (await s.Owner.PostAsJsonAsync(s.Url(), body)).StatusCode);

        foreach (var id in new[] { first, second })
            Assert.Equal(HttpStatusCode.Accepted, (await s.Owner.PostAsync(s.Url($"/{id}/cancel"), null)).StatusCode);
    }

    [Fact]
    public async Task A_private_repository_needs_the_workspace_token_and_the_token_reaches_no_log_and_no_row()
    {
        var s = await ImportScenario.CreateAsync(import);
        s.GitHub.Private = true;

        // Without a token GitHub says the repository does not exist. The report says what to do.
        var id = await s.StartAsync();
        await s.DrainAsync();
        var failed = await s.GetAsync(id);
        Assert.Equal(("failed", false, "not_found"), (failed.Text("status"), failed["isComplete"]!.GetValue<bool>(), failed["error"]!["code"]!.GetValue<string>()));
        Assert.Contains("github:token", failed["error"]!["message"]!.GetValue<string>());
        Assert.Contains("resume the scan", failed["error"]!["message"]!.GetValue<string>());
        Assert.Empty(s.GitHub.BlobRequests);

        // With the token the same scan completes.
        await OutboundHost.PutSecretAsync(s.Owner, s.WorkspaceId, "github:token", Token);
        Assert.Equal(HttpStatusCode.Accepted, (await s.Owner.PostAsync(s.Url($"/{id}/resume"), null)).StatusCode);
        await s.DrainAsync();
        var done = await s.GetAsync(id);
        Assert.Equal(("completed", true), (done.Text("status"), done["isComplete"]!.GetValue<bool>()));
        Assert.Null(done["error"]);
        Assert.Contains($"Bearer {Token}", s.GitHub.AuthorizationHeaders);
        await s.DecideAsync(id, accept: (await s.ProposalsAsync(id)).Select(p => p.Id()));

        // A token that GitHub refuses: another report, another fix.
        s.GitHub.Unauthorized = true;
        var expired = await s.ScanAsync();
        Assert.Equal(("failed", "unauthorized"), (expired.Text("status"), expired["error"]!["code"]!.GetValue<string>()));
        Assert.Contains("Save a new token", expired["error"]!["message"]!.GetValue<string>());

        Assert.DoesNotContain(import.Host.Logs.Lines, line => line.Contains(Token, StringComparison.Ordinal));
        Assert.DoesNotContain(Token, await ImportRowsAsync());
        Assert.DoesNotContain(Token, await s.Owner.GetStringAsync(s.Url($"/{id}")) + await s.Owner.GetStringAsync(s.Url($"/{id}/proposals")));
    }

    [Fact]
    public async Task A_hostile_repository_completes_and_no_secret_looking_or_binary_path_is_ever_requested()
    {
        var s = await ImportScenario.CreateAsync(import, "hostile");
        var files = s.GitHub.Files(ImportScenario.Commit1);
        var refused = files.Keys.Where(path => PathFilter.SkipReason(path, out _) is not null).ToList();
        Assert.Subset(refused.ToHashSet(), new HashSet<string>
        {
            ".env.production", "certs/server.pem", "keys/id_rsa", "infra/prod.tfvars", "config/secrets.yaml",
            "logo.png", "package-lock.json", "node_modules/x/openapi.json",
        });

        var scan = await s.ScanAsync("acme/hostile");
        Assert.Equal("completed", scan.Text("status"));
        Assert.Null(scan["error"]);
        Assert.DoesNotContain(import.Host.Logs.Lines, line => line.Contains("stopped on", StringComparison.Ordinal));

        // Nothing the path filter refuses was asked of GitHub, and the scan says what it left out.
        Assert.Empty(s.GitHub.BlobRequests.Intersect(refused));
        Assert.All(s.GitHub.BlobRequests, path => Assert.True(PathFilter.IsRelevant(path[(path.LastIndexOf('/') + 1)..]), path));
        var notes = scan["notes"]!.AsArray().Select(n => $"{n!["code"]!.GetValue<string>()} {n["path"]!.GetValue<string>()}").ToList();
        Assert.Contains("secret-file .env.production", notes);
        Assert.Contains("binary-extension logo.png", notes);
        Assert.All(scan["notes"]!.AsArray(), n => Assert.False(string.IsNullOrWhiteSpace(n!["message"]!.GetValue<string>())));

        // A refused path has no source view. A line of a read file that looks like a credential is withheld.
        foreach (var path in refused)
            Assert.Equal(HttpStatusCode.NotFound, (await s.Owner.GetAsync(s.Url($"/{scan.Id()}/source?path={Uri.EscapeDataString(path)}"))).StatusCode);
        // No window of a read file is served unless it is the evidence of a fact: a window
        // that somebody makes up is refused, whatever file it names.
        foreach (var path in s.GitHub.BlobRequests.Distinct())
            Assert.Equal(HttpStatusCode.NotFound, (await s.Owner.GetAsync(s.Url($"/{scan.Id()}/source?path={Uri.EscapeDataString(path)}&startLine=1&endLine=200"))).StatusCode);
        var shown = new List<string>();
        foreach (var (path, start, end) in await s.EvidenceWindowsAsync(scan.Id()))
        {
            var response = await s.Owner.GetAsync(s.Url($"/{scan.Id()}/source?path={Uri.EscapeDataString(path)}&startLine={start}&endLine={end}"));
            if (response.StatusCode == HttpStatusCode.OK)
                shown.Add(await response.Content.ReadAsStringAsync());
        }
        Assert.NotEmpty(shown);

        // The planted credentials are in no proposal, no evidence and no log, and the
        // planted token is in no source view.
        var derived = await s.Owner.GetStringAsync(s.Url($"/{scan.Id()}/proposals?limit=200"))
            + await s.Owner.GetStringAsync(s.Url($"/{scan.Id()}/evidence?limit=200"))
            + string.Join("\n", import.Host.Logs.Lines);
        Assert.DoesNotContain("ghp_PLANTED", derived + string.Join("\n", shown));
        Assert.DoesNotContain("hunter2-planted", derived);

        // Accepting what a hostile repository proposes goes through the model's own rules.
        var proposals = await s.ProposalsAsync(scan.Id());
        Assert.All(proposals, p => Assert.Equal(!scan["isComplete"]!.GetValue<bool>(), p["partial"]!.GetValue<bool>()));
        var decision = await s.DecideAsync(scan.Id(), accept: proposals.Select(p => p.Id()));
        Assert.True(decision["failed"]!.AsArray().Count == 0 || decision["accepted"]!.AsArray().Count == 0);
    }

    [Fact]
    public async Task The_worker_of_a_running_app_takes_a_queued_scan_by_itself()
    {
        using var live = ImportHost.WithWorker(import.Fixture);
        var s = await ImportScenario.CreateAsync(live);
        var id = await s.StartAsync();

        var deadline = DateTime.UtcNow + Patience;
        var scan = await s.GetAsync(id);
        while (scan.Text("status") is "queued" or "running" && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100);
            scan = await s.GetAsync(id);
        }

        Assert.Equal(("completed", true), (scan.Text("status"), scan["isComplete"]!.GetValue<bool>()));
        Assert.Equal(21, (await s.ProposalsAsync(id)).Count);
    }
}
