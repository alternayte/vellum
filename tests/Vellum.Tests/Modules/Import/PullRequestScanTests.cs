using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Vellum.Tests.Shared.Outbound;

namespace Vellum.Tests.Modules.Import;

/// <summary>
/// A scan of a pull request (SDD §5, "Change scope": a read-only GitHub PR). The scan reads the
/// head commit that GitHub names for the pull request, records the pull request with both
/// SHAs, and gives the same impact report as a scan of that commit by ref. The GitHub answers
/// are the recorded <c>Fixtures/GitHub/pull-request.json</c> with the SHAs of the fixture commits.
/// </summary>
public class PullRequestScanTests(ImportHost import) : IClassFixture<ImportHost>
{
    private const string Token = "ghp_pullRequestScanTestToken000000000000";

    /// <summary>The recorded answer for pull request 7, with the head in <paramref name="headRepository"/> at <paramref name="headSha"/>.</summary>
    private static JsonObject Pull(string headSha, string? headRepository = "acme/orders", string state = "open", bool merged = false)
    {
        var pull = JsonNode.Parse(Fixtures.Read("GitHub/pull-request.json"))!.AsObject();
        pull["state"] = state;
        pull["merged"] = merged;
        pull["base"]!["sha"] = ImportScenario.Commit1;
        pull["head"]!["sha"] = headSha;
        pull["head"]!["repo"] = headRepository is null ? null : new JsonObject { ["full_name"] = headRepository };
        return pull;
    }

    private static async Task<ImportScenario> WithOutboxRemovedAsync(ImportHost import)
    {
        var s = await ImportScenario.CreateAsync(import);
        s.GitHub.Commit(ImportScenario.Commit2, FixtureGitHub.Load("orders-outbox-removed"));
        s.GitHub.Head = ImportScenario.Commit1;
        s.GitHub.Branches["remove-outbox"] = ImportScenario.Commit2;
        return s;
    }

    [Fact]
    public async Task A_pull_request_scan_reads_the_head_commit_records_the_pull_request_and_reports_the_same_impact_as_its_ref()
    {
        var s = await WithOutboxRemovedAsync(import);
        await s.ScanAndAcceptAllAsync();
        s.GitHub.Pulls[7] = Pull(ImportScenario.Commit2);

        var byRef = await s.ScanAsync(body: new { repository = "acme/orders", @ref = "remove-outbox" });
        var byPull = await s.ScanAsync(body: new { repository = "acme/orders", pullRequest = 7 });

        Assert.Equal(("completed", "comparison", ImportScenario.Commit2), (byPull.Text("status"), byPull.Text("kind"), byPull.Text("commitSha")));
        Assert.Null(byPull["ref"]);
        Assert.Null(byPull["resolvedRef"]);
        Assert.False(byPull["canDecide"]!.GetValue<bool>());
        Assert.Contains("pull request", byPull.Text("canDecideReason"));
        var pull = byPull["pullRequest"]!.AsObject();
        Assert.Equal((7, "Remove the outbox", "open", false), (pull["number"]!.GetValue<int>(), pull.Text("title"), pull.Text("state"), pull["merged"]!.GetValue<bool>()));
        Assert.Equal(("main", ImportScenario.Commit1, "remove-outbox", ImportScenario.Commit2, false),
            (pull.Text("baseRef"), pull.Text("baseSha"), pull.Text("headRef"), pull.Text("headSha"), pull["fromFork"]!.GetValue<bool>()));
        Assert.Equal(7, byPull["pullRequestNumber"]!.GetValue<int>());

        // The impact is that of the same commit read by ref: the same changes, flows and promises.
        var refImpact = (await s.Owner.GetFromJsonAsync<JsonObject>(s.Url($"/{byRef.Id()}/impact")))!;
        var pullImpact = (await s.Owner.GetFromJsonAsync<JsonObject>(s.Url($"/{byPull.Id()}/impact")))!;
        static string[] Changes(JsonObject impact) =>
            [.. impact["changes"]!.AsArray().Select(c => $"{c!["action"]} {c["entityKind"]} {c["name"]} {c["certainty"]}").Order(StringComparer.Ordinal)];
        Assert.Equal(Changes(refImpact), Changes(pullImpact));
        Assert.Contains("remove pattern Transactional outbox definite", Changes(pullImpact));
        Assert.Equal(refImpact["base"]!.ToJsonString(), pullImpact["base"]!.ToJsonString());
        Assert.Equal(7, pullImpact["pullRequest"]!["number"]!.GetValue<int>());
        Assert.Null(refImpact["pullRequest"]);

        // The base of the pull request is the commit the model follows, so no limit is added for it.
        Assert.DoesNotContain(pullImpact["limits"]!.AsArray(), l => l!.GetValue<string>().Contains("base commit of pull request", StringComparison.Ordinal));

        // A pull request scan is no line of history: it cannot become the tracked ref.
        var track = await s.Owner.PostAsync(s.Url($"/{byPull.Id()}/track"), null);
        Assert.Equal(HttpStatusCode.Conflict, track.StatusCode);
        Assert.Contains("ref_not_trackable", await track.Content.ReadAsStringAsync());

        // Read-only: every request was a GET, and the pull request was read once.
        Assert.DoesNotContain(s.GitHub.Requests, r => r.StartsWith("write:", StringComparison.Ordinal));
        Assert.Single(s.GitHub.Requests, r => r == "pull:7");
    }

    [Fact]
    public async Task A_pull_request_whose_base_is_not_the_followed_commit_says_what_the_report_compares_with()
    {
        var s = await WithOutboxRemovedAsync(import);
        await s.ScanAndAcceptAllAsync();
        var pull = Pull(ImportScenario.Commit2);
        pull["base"]!["sha"] = ImportScenario.Commit3;
        s.GitHub.Pulls[7] = pull;

        var scan = await s.ScanAsync(body: new { repository = "acme/orders", pullRequest = 7 });
        var impact = (await s.Owner.GetFromJsonAsync<JsonObject>(s.Url($"/{scan.Id()}/impact")))!;
        var limit = Assert.Single(impact["limits"]!.AsArray(), l => l!.GetValue<string>().Contains("base commit of pull request #7", StringComparison.Ordinal))!.GetValue<string>();
        Assert.Contains(ImportScenario.Commit3[..10], limit);
        Assert.Contains(ImportScenario.Commit1[..10], limit);
    }

    [Fact]
    public async Task A_closed_or_merged_pull_request_is_scanned_and_says_so()
    {
        var s = await WithOutboxRemovedAsync(import);
        s.GitHub.Pulls[7] = Pull(ImportScenario.Commit2, state: "closed", merged: true);
        s.GitHub.Pulls[8] = Pull(ImportScenario.Commit2, state: "closed");
        s.GitHub.Pulls[8]["number"] = 8;

        var merged = await s.ScanAsync(body: new { repository = "acme/orders", pullRequest = 7 });
        Assert.Equal(("completed", "closed", true), (merged.Text("status"), merged["pullRequest"]!.AsObject().Text("state"), merged["pullRequest"]!["merged"]!.GetValue<bool>()));
        Assert.Contains((await s.GetAsync(merged.Id()))["notes"]!.AsArray(), n => n!["code"]!.GetValue<string>() == "pull-request-merged");

        var closed = await s.ScanAsync(body: new { repository = "acme/orders", pullRequest = 8 });
        Assert.Equal("completed", closed.Text("status"));
        Assert.Contains((await s.GetAsync(closed.Id()))["notes"]!.AsArray(), n => n!["code"]!.GetValue<string>() == "pull-request-closed");
    }

    [Fact]
    public async Task A_pull_request_that_does_not_exist_fails_with_a_clear_reason_and_reads_no_file()
    {
        var s = await WithOutboxRemovedAsync(import);
        var scan = await s.ScanAsync(body: new { repository = "acme/orders", pullRequest = 41 });
        Assert.Equal(("failed", "pull_request_not_found"), (scan.Text("status"), scan["error"]!["code"]!.GetValue<string>()));
        Assert.Contains("no pull request #41", scan["error"]!["message"]!.GetValue<string>());
        Assert.DoesNotContain(s.GitHub.Requests, r => r.StartsWith("tree:", StringComparison.Ordinal));
        Assert.Empty(s.GitHub.BlobRequests);

        // A resume would ask for the same pull request again. The scan says so, and a resume is refused.
        Assert.False(scan["canResume"]!.GetValue<bool>());
        var resume = await s.Owner.PostAsync(s.Url($"/{scan.Id()}/resume"), null);
        Assert.Equal(HttpStatusCode.Conflict, resume.StatusCode);
        Assert.Equal("scan_not_resumable", (await resume.Content.ReadFromJsonAsync<JsonObject>())!.Text("type"));
        Assert.Equal("failed", (await s.GetAsync(scan.Id())).Text("status"));
    }

    [Fact]
    public async Task A_private_repository_without_a_token_asks_for_one_and_with_the_token_the_scan_completes()
    {
        var s = await WithOutboxRemovedAsync(import);
        s.GitHub.Private = true;
        s.GitHub.Pulls[7] = Pull(ImportScenario.Commit2);

        var refused = await s.ScanAsync(body: new { repository = "acme/orders", pullRequest = 7 });
        Assert.Equal(("failed", "not_found"), (refused.Text("status"), refused["error"]!["code"]!.GetValue<string>()));
        Assert.Contains("github:token", refused["error"]!["message"]!.GetValue<string>());
        Assert.DoesNotContain(s.GitHub.Requests, r => r == "pull:7");

        await OutboundHost.PutSecretAsync(s.Owner, s.WorkspaceId, "github:token", Token);
        var scan = await s.ScanAsync(body: new { repository = "acme/orders", pullRequest = 7 });
        Assert.Equal(("completed", ImportScenario.Commit2), (scan.Text("status"), scan.Text("commitSha")));
    }

    [Fact]
    public async Task A_pull_request_from_a_fork_is_read_only_when_this_repository_serves_its_head_commit()
    {
        var s = await WithOutboxRemovedAsync(import);

        // GitHub serves the head commit of a fork's pull request through the base repository.
        s.GitHub.Pulls[7] = Pull(ImportScenario.Commit2, headRepository: "contributor/orders");
        var served = await s.ScanAsync(body: new { repository = "acme/orders", pullRequest = 7 });
        Assert.Equal(("completed", ImportScenario.Commit2), (served.Text("status"), served.Text("commitSha")));
        Assert.Equal(("contributor/orders", true), (served["pullRequest"]!.AsObject().Text("headRepository"), served["pullRequest"]!["fromFork"]!.GetValue<bool>()));
        Assert.Contains((await s.GetAsync(served.Id()))["notes"]!.AsArray(), n => n!["code"]!.GetValue<string>() == "pull-request-fork");
        // Nothing of the fork itself was asked for.
        Assert.DoesNotContain(s.GitHub.Requests, r => r.Contains("contributor", StringComparison.Ordinal));

        // A head commit that this repository does not serve, from a fork that was deleted: refused, nothing read.
        s.GitHub.Pulls[9] = Pull(ImportScenario.Commit3, headRepository: null);
        s.GitHub.Pulls[9]["number"] = 9;
        var trees = s.GitHub.Requests.Count(r => r.StartsWith("tree:", StringComparison.Ordinal));
        var refused = await s.ScanAsync(body: new { repository = "acme/orders", pullRequest = 9 });
        Assert.Equal(("failed", "pull_request_head_unreadable"), (refused.Text("status"), refused["error"]!["code"]!.GetValue<string>()));
        Assert.Contains("reads only the repository of the scan", refused["error"]!["message"]!.GetValue<string>());
        Assert.Null(refused.Text("commitSha"));
        Assert.Equal(trees, s.GitHub.Requests.Count(r => r.StartsWith("tree:", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task A_pull_request_number_is_one_of_ref_or_number_and_is_a_positive_whole_number()
    {
        var s = await ImportScenario.CreateAsync(import);
        foreach (var body in new object[]
                 {
                     new { repository = "acme/orders", @ref = "main", pullRequest = 7 },
                     new { repository = "acme/orders", pullRequest = 0 },
                     new { repository = "acme/orders", pullRequest = -3 },
                 })
        {
            var response = await s.Owner.PostAsJsonAsync(s.Url(), body);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains("pullRequest", await response.Content.ReadAsStringAsync());
        }
    }
}
