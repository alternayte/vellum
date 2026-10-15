using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Vellum.Modules.Promises;
using Vellum.Shared;
using Vellum.Tests.Modules.Import;
using Vellum.Tests.Shared.Outbound;

namespace Vellum.Tests.Modules.Promises;

/// <summary>
/// The compact impact report for pull request CI (a deterministic
/// failed check can block only by opt-in policy) and the tokens that read it. Vellum writes
/// nothing to GitHub; a CI job reads the report with a token.
/// </summary>
public class ImpactReportTests(ImportHost import) : IClassFixture<ImportHost>
{
    private const string Title = "Remove the outbox <img src=x onerror=alert(1)> [see](https://evil.example) | x";

    private static async Task<JsonObject> Json(Task<HttpResponseMessage> request, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = await request;
        Assert.True(response.StatusCode == expected, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<JsonObject>())!;
    }

    private static string ReportUrl(ImportScenario s, Guid scan) => s.Url($"/{scan}/impact/report");

    private static async Task<string> MarkdownAsync(HttpClient client, string url)
    {
        using var response = await client.GetAsync(url);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        Assert.Equal("text/markdown", response.Content.Headers.ContentType?.MediaType);
        return await response.Content.ReadAsStringAsync();
    }

    private static async Task<JsonObject> ReportJsonAsync(HttpClient client, string url)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return await Json(client.SendAsync(request));
    }

    private static Task<JsonObject> SetPolicyAsync(HttpClient client, ImportScenario s, string policy, HttpStatusCode expected = HttpStatusCode.OK) =>
        Json(client.PutAsJsonAsync(s.Model("impact-gate"), new { policy }), expected);

    /// <summary>The arranged project with pull request #1 from <c>remove-outbox</c> into <c>main</c>.</summary>
    private async Task<ChangeImpactTests.Arranged> WithPullRequestAsync()
    {
        var arranged = await ChangeImpactTests.ArrangeAsync(import);
        ChangeImpactTests.Branch(arranged.S, FixtureGitHub.Load("orders-outbox-removed"));
        var pull = JsonNode.Parse(Fixtures.Read("GitHub/pull-request.json"))!.AsObject();
        pull["number"] = 1;
        pull["title"] = Title;
        pull["head"]!["repo"] = new JsonObject { ["full_name"] = "acme/orders" };
        arranged.S.GitHub.Pulls[1] = pull;
        return arranged;
    }

    [Fact]
    public async Task The_report_of_a_pull_request_is_the_same_bytes_twice_names_the_pull_request_and_marks_only_what_it_can_show_as_definite()
    {
        var a = await WithPullRequestAsync();
        var s = a.S;
        var scan = (await s.ScanAsync(body: new { repository = "acme/orders", pullRequest = 1 })).Id();

        var first = await MarkdownAsync(s.Owner, ReportUrl(s, scan));
        var second = await MarkdownAsync(s.Owner, ReportUrl(s, scan));
        Assert.Equal(first, second);
        var json = await ReportJsonAsync(s.Owner, ReportUrl(s, scan));
        Assert.Equal(json.ToJsonString(), (await ReportJsonAsync(s.Owner, ReportUrl(s, scan))).ToJsonString());

        // The pull request with both commits, and exact source references.
        Assert.Equal((1, ImportScenario.Commit1, ImportScenario.Commit2), (
            json["pullRequest"]!["number"]!.GetValue<int>(), json["pullRequest"]!["baseSha"]!.GetValue<string>(), json["pullRequest"]!["headSha"]!.GetValue<string>()));
        Assert.Contains("pull request #1", first.Replace("\\#", "#", StringComparison.Ordinal));
        Assert.Contains($"`{ImportScenario.Commit2}`", first);
        Assert.Contains($"`{ImportScenario.Commit1}`", first);
        var outbox = json["changes"]!.AsArray().Select(c => c!.AsObject()).Single(c => c.Text("kind") == "source-pattern");
        Assert.Equal(("remove", "definite"), (outbox.Text("action"), outbox.Text("certainty")));
        var reference = outbox["references"]![0]!["reference"]!.GetValue<string>();
        Assert.Matches($@"^orders-api/.+:\d+(-\d+)?@{ImportScenario.Commit1}$", reference);
        Assert.Contains($"`{reference}`", first);

        // The title of the pull request is repository text: no HTML, no link, no table cell break.
        Assert.DoesNotMatch(@"(?<!\\)<img", first);
        Assert.DoesNotMatch(@"(?<!\\)\]\(https", first);
        Assert.Contains("\\<img", first);

        // Nothing of Billing changed. ORD-015 links only Billing and is not in the report; no step of Billing is definite.
        Assert.DoesNotContain(json["promises"]!.AsArray(), p => p!.AsObject().Id() == a.Ord015);
        Assert.DoesNotContain(json["changes"]!.AsArray(), c => c!["name"]!.GetValue<string>() == "Billing");
        Assert.DoesNotContain("ORD-015", first);
        var steps = json["flows"]!.AsArray().SelectMany(f => f!["steps"]!.AsArray()).Select(step => step!.AsObject()).ToList();
        Assert.Equal(["publish-order-created"], steps.Where(step => step.Text("certainty") == "definite").Select(step => step.Text("stepId")));

        // Evidence-scoped check results, the scan's own time and nothing else, and no score.
        var ord014 = json["promises"]!.AsArray().Select(p => p!.AsObject()).Single(p => p.Id() == a.Ord014);
        Assert.Equal(("at-risk", "not-run"), (ord014.Text("standing"), ord014["checks"]![0]!["result"]!.GetValue<string>()));
        Assert.NotNull(json["scanCompletedAt"]);
        Assert.DoesNotContain("score\"", json.ToJsonString());
        Assert.Equal(("off", "off"), (json["gate"]!["policy"]!.GetValue<string>(), json["gate"]!["verdict"]!.GetValue<string>()));
        Assert.Contains("**Gate: off**", first);
    }

    [Fact]
    public async Task At_risk_possible_and_unknown_never_fail_the_gate()
    {
        var a = await WithPullRequestAsync();
        var s = a.S;
        await SetPolicyAsync(s.Owner, s, EnumText.Of(ImpactGatePolicy.FailOnBroken));

        // A scan that read only billing: the removed outbox is unknown.
        var narrow = (await s.ScanAsync(body: new { repository = "acme/orders", @ref = "remove-outbox", includePaths = new[] { "billing" } })).Id();
        var unknown = await ReportJsonAsync(s.Owner, ReportUrl(s, narrow));
        Assert.Contains(unknown["changes"]!.AsArray(), c => c!["certainty"]!.GetValue<string>() == "unknown");
        Assert.NotEmpty(unknown["unknowns"]!.AsArray());
        Assert.Equal("pass", unknown["gate"]!["verdict"]!.GetValue<string>());

        // The check against a scan that did not look everywhere cannot decide: unknown, never a failure.
        var checks = await Json(s.Owner.PostAsync(s.Url($"/{narrow}/impact/checks"), null));
        Assert.Equal("unsupported", Assert.Single(checks["runs"]!.AsArray())!["result"]!.GetValue<string>());
        var undecided = await ReportJsonAsync(s.Owner, ReportUrl(s, narrow));
        Assert.Equal("unknown", undecided["promises"]!.AsArray().Single(p => p!.AsObject().Id() == a.Ord014)!["checks"]![0]!["result"]!.GetValue<string>());
        Assert.Equal("pass", undecided["gate"]!["verdict"]!.GetValue<string>());
        Assert.Contains(undecided["gate"]!["reasons"]!.AsArray(), r => r!.GetValue<string>().Contains("could not decide"));

        // The pull request with no check run: ORD-014 at risk on a definite reason, ORD-016 possible.
        var pull = (await s.ScanAsync(body: new { repository = "acme/orders", pullRequest = 1 })).Id();
        var report = await ReportJsonAsync(s.Owner, ReportUrl(s, pull));
        var standings = report["promises"]!.AsArray().ToDictionary(p => p!.AsObject().Id(), p => p!["standing"]!.GetValue<string>());
        Assert.Equal(("at-risk", "possible"), (standings[a.Ord014], standings[a.Ord016]));
        var gate = report["gate"]!.AsObject();
        Assert.Equal(("fail-on-broken", "pass"), (gate.Text("policy"), gate.Text("verdict")));
        Assert.Contains(gate["reasons"]!.AsArray(), r => r!.GetValue<string>().Contains("never fail the gate"));
        Assert.Contains(gate["reasons"]!.AsArray(), r => r!.GetValue<string>().Contains("not run against this scan"));
    }

    [Fact]
    public async Task A_pull_request_that_removes_the_outbox_breaks_the_promise_in_its_report_and_fails_the_gate_only_with_the_policy_on()
    {
        var a = await WithPullRequestAsync();
        var s = a.S;
        var factory = import.Host.Factory;
        // The promise passes against the followed ref.
        Assert.Equal("passed", (await Json(s.Owner.PostAsJsonAsync(s.Model($"promises/{a.Ord014}/obligations/{a.SourceCheck}/run"), new { scanId = a.MainScan }))).Text("result"));
        async Task<JsonObject> StatusAsync() => (await Json(s.Owner.GetAsync(s.Model($"promises/{a.Ord014}"))))["current"]!["evidenceStatus"]!.AsObject();
        var before = await StatusAsync();

        // The CI token starts the scan, runs the checks against it and reads the report.
        var ci = await TestHost.CreateAgentAsync(factory, s.Owner, s.WorkspaceId, ["read", "scan-pull-request"], projectId: s.ProjectId);
        var scan = (await Json(ci.Client.PostAsJsonAsync(s.Url(), new { repository = "acme/orders", pullRequest = 1 }), HttpStatusCode.Accepted)).Id();
        await s.DrainAsync();
        var checks = await Json(ci.Client.PostAsync(s.Url($"/{scan}/impact/checks"), null));
        var run = Assert.Single(checks["runs"]!.AsArray())!.AsObject();
        Assert.Equal(("ORD-014", a.SourceCheck, "failed"), (run.Text("requirementId"), run.Text("key"), run.Text("result")));

        // The run is for the head commit of the pull request, and names the scan.
        var recorded = (await Json(s.Owner.GetAsync(s.Model($"promises/{a.Ord014}/runs"))))["items"]![0]!.AsObject();
        Assert.Equal((scan, ImportScenario.Commit2, "comparison"), (recorded.Id("scanId"), recorded.Text("commitSha"), recorded["inputs"]!["scanKind"]!.GetValue<string>()));

        var off = await ReportJsonAsync(ci.Client, ReportUrl(s, scan));
        var ord014 = off["promises"]!.AsArray().Single(p => p!.AsObject().Id() == a.Ord014)!.AsObject();
        Assert.Equal(("broken", "failed"), (ord014.Text("standing"), ord014["checks"]![0]!["result"]!.GetValue<string>()));
        Assert.Equal("off", off["gate"]!["verdict"]!.GetValue<string>());

        await SetPolicyAsync(s.Owner, s, EnumText.Of(ImpactGatePolicy.FailOnBroken));
        var on = await ReportJsonAsync(ci.Client, ReportUrl(s, scan));
        Assert.Equal("fail", on["gate"]!["verdict"]!.GetValue<string>());
        Assert.StartsWith("ORD-014 ", Assert.Single(on["gate"]!["reasons"]!.AsArray())!.GetValue<string>());

        // Against the followed ref, the promise is as it was.
        var after = await StatusAsync();
        Assert.Equal(before["counts"]!.ToJsonString(), after["counts"]!.ToJsonString());
        Assert.Equal(1, after["counts"]!["passed"]!.GetValue<int>());
        Assert.Equal(a.MainScan, after["obligations"]!.AsArray().Single(o => o!["obligationId"]!.GetValue<string>() == run.Text("obligationId"))!.AsObject().Id("scanId"));
        // The followed ref has no failed run, so its own report does not fail the gate.
        Assert.Equal("pass", (await ReportJsonAsync(s.Owner, ReportUrl(s, a.MainScan)))["gate"]!["verdict"]!.GetValue<string>());
    }

    [Fact]
    public async Task Only_the_token_that_started_a_scan_or_a_propose_token_runs_its_checks()
    {
        var a = await WithPullRequestAsync();
        var s = a.S;
        var factory = import.Host.Factory;
        var reader = await TestHost.CreateAgentAsync(factory, s.Owner, s.WorkspaceId, ["read"], projectId: s.ProjectId);
        var scanner = await TestHost.CreateAgentAsync(factory, s.Owner, s.WorkspaceId, ["scan-pull-request"], projectId: s.ProjectId);
        var other = await TestHost.CreateAgentAsync(factory, s.Owner, s.WorkspaceId, ["scan-pull-request"], projectId: s.ProjectId);
        var proposer = await TestHost.CreateAgentAsync(factory, s.Owner, s.WorkspaceId, ["propose"], projectId: s.ProjectId);

        var byPerson = (await s.ScanAsync(body: new { repository = "acme/orders", pullRequest = 1 })).Id();
        var byToken = (await Json(scanner.Client.PostAsJsonAsync(s.Url(), new { repository = "acme/orders", pullRequest = 1 }), HttpStatusCode.Accepted)).Id();
        await s.DrainAsync();

        Assert.Equal("agent_not_allowed", (await Json(reader.Client.PostAsync(s.Url($"/{byToken}/impact/checks"), null), HttpStatusCode.Forbidden)).Text("type"));
        Assert.Equal("agent_not_allowed", (await Json(scanner.Client.PostAsync(s.Url($"/{byPerson}/impact/checks"), null), HttpStatusCode.Forbidden)).Text("type"));
        Assert.Equal("agent_not_allowed", (await Json(other.Client.PostAsync(s.Url($"/{byToken}/impact/checks"), null), HttpStatusCode.Forbidden)).Text("type"));
        await Json(scanner.Client.PostAsync(s.Url($"/{byToken}/impact/checks"), null));
        await Json(proposer.Client.PostAsync(s.Url($"/{byPerson}/impact/checks"), null));
        // The single-check endpoint stays closed to the scan token.
        Assert.Equal(HttpStatusCode.Forbidden, (await scanner.Client.PostAsJsonAsync(s.Model($"promises/{a.Ord014}/obligations/{a.SourceCheck}/run"), new { scanId = byToken })).StatusCode);

        // A viewer cannot run checks, and another workspace reaches nothing.
        using var viewer = await TestHost.SignInMemberAsync(factory, s.Owner, s.WorkspaceId, "Viewer");
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsync(s.Url($"/{byPerson}/impact/checks"), null)).StatusCode);
        using var stranger = await TestHost.SignInAsync(factory);
        var (strangerWorkspace, mine) = await TestHost.CreateProjectAsync(stranger);
        Assert.Equal(HttpStatusCode.Forbidden, (await stranger.PostAsync(s.Url($"/{byPerson}/impact/checks"), null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.PostAsync($"/api/projects/{mine}/imports/{byPerson}/impact/checks", null)).StatusCode);
        var strangerToken = await TestHost.CreateAgentAsync(factory, stranger, strangerWorkspace, ["propose", "scan-pull-request"]);
        Assert.Equal(HttpStatusCode.Forbidden, (await strangerToken.Client.PostAsync(s.Url($"/{byToken}/impact/checks"), null)).StatusCode);
    }

    [Fact]
    public async Task A_promise_broken_by_a_failed_check_fails_the_gate_only_when_the_policy_is_on()
    {
        var a = await WithPullRequestAsync();
        var s = a.S;
        var scan = (await s.ScanAsync(body: new { repository = "acme/orders", @ref = "remove-outbox" })).Id();
        await Json(s.Owner.PostAsync(s.Url($"/{scan}/track"), null));
        var run = await Json(s.Owner.PostAsJsonAsync(s.Model($"promises/{a.Ord014}/obligations/{a.SourceCheck}/run"), new { scanId = scan }));
        Assert.Equal("failed", run.Text("result"));

        var off = await ReportJsonAsync(s.Owner, ReportUrl(s, scan));
        Assert.Equal("broken", off["promises"]!.AsArray().Single(p => p!.AsObject().Id() == a.Ord014)!["standing"]!.GetValue<string>());
        Assert.Equal(("off", "off"), (off["gate"]!["policy"]!.GetValue<string>(), off["gate"]!["verdict"]!.GetValue<string>()));

        await SetPolicyAsync(s.Owner, s, EnumText.Of(ImpactGatePolicy.FailOnBroken));
        var on = await ReportJsonAsync(s.Owner, ReportUrl(s, scan));
        var gate = on["gate"]!.AsObject();
        Assert.Equal("fail", gate.Text("verdict"));
        var reason = Assert.Single(gate["reasons"]!.AsArray())!.GetValue<string>();
        Assert.StartsWith("ORD-014 ", reason);
        Assert.Contains($"the check '{a.SourceCheck}' failed against this scan", reason);
        Assert.Contains("**Gate: fail**", await MarkdownAsync(s.Owner, ReportUrl(s, scan)));

        await SetPolicyAsync(s.Owner, s, EnumText.Of(ImpactGatePolicy.Off));
        Assert.Equal("off", (await ReportJsonAsync(s.Owner, ReportUrl(s, scan)))["gate"]!["verdict"]!.GetValue<string>());
    }

    [Fact]
    public async Task Only_an_owner_in_person_changes_the_policy_and_the_audit_log_records_each_change()
    {
        var s = await ImportScenario.CreateAsync(import);
        var factory = import.Host.Factory;
        using var editor = await TestHost.SignInMemberAsync(factory, s.Owner, s.WorkspaceId, "Editor");
        var agent = await TestHost.CreateAgentAsync(factory, s.Owner, s.WorkspaceId, ["read", "propose", "export", "scan-pull-request"]);

        var initial = await Json(s.Owner.GetAsync(s.Model("impact-gate")));
        Assert.Equal(("off", true), (initial.Text("policy"), initial["canChange"]!.GetValue<bool>()));
        Assert.False((await Json(editor.GetAsync(s.Model("impact-gate"))))["canChange"]!.GetValue<bool>());
        Assert.False((await Json(agent.Client.GetAsync(s.Model("impact-gate"))))["canChange"]!.GetValue<bool>());

        Assert.Equal(HttpStatusCode.Forbidden, (await editor.PutAsJsonAsync(s.Model("impact-gate"), new { policy = "fail-on-broken" })).StatusCode);
        var refused = await Json(agent.Client.PutAsJsonAsync(s.Model("impact-gate"), new { policy = "fail-on-broken" }), HttpStatusCode.Forbidden);
        Assert.Equal("human_only", refused.Text("type"));
        await SetPolicyAsync(s.Owner, s, "fail-at-risk", HttpStatusCode.BadRequest);
        Assert.Equal("off", (await Json(s.Owner.GetAsync(s.Model("impact-gate")))).Text("policy"));

        var set = await SetPolicyAsync(s.Owner, s, EnumText.Of(ImpactGatePolicy.FailOnBroken));
        Assert.Equal("fail-on-broken", set.Text("policy"));
        await SetPolicyAsync(s.Owner, s, EnumText.Of(ImpactGatePolicy.Off));

        var audit = JsonNode.Parse(await s.Owner.GetStringAsync($"/api/workspaces/{s.WorkspaceId}/audit"))!.AsArray()
            .Where(e => e!["action"]!.GetValue<string>() == ImpactGate.AuditAction)
            .Select(e => e!["target"]!.GetValue<string>())
            .ToList();
        Assert.Equal([$"project:{s.ProjectId} impact-gate:fail-on-broken->off", $"project:{s.ProjectId} impact-gate:off->fail-on-broken"], audit);
    }

    [Fact]
    public async Task A_read_token_reads_the_report_and_a_scan_token_starts_only_a_pull_request_scan()
    {
        var a = await WithPullRequestAsync();
        var s = a.S;
        var factory = import.Host.Factory;
        var reader = await TestHost.CreateAgentAsync(factory, s.Owner, s.WorkspaceId, ["read"], projectId: s.ProjectId);
        var scanner = await TestHost.CreateAgentAsync(factory, s.Owner, s.WorkspaceId, ["scan-pull-request"], projectId: s.ProjectId);

        // A read token cannot start a scan.
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.Client.PostAsJsonAsync(s.Url(), new { repository = "acme/orders", pullRequest = 1 })).StatusCode);
        // A scan token starts a pull request scan, and nothing else.
        foreach (var body in new object[] { new { repository = "acme/orders" }, new { repository = "acme/orders", @ref = "remove-outbox" } })
        {
            var refused = await Json(scanner.Client.PostAsJsonAsync(s.Url(), body), HttpStatusCode.Forbidden);
            Assert.Equal("agent_not_allowed", refused.Text("type"));
        }
        var started = await Json(scanner.Client.PostAsJsonAsync(s.Url(), new { repository = "acme/orders", pullRequest = 1 }), HttpStatusCode.Accepted);
        await s.DrainAsync();
        var scan = started.Id();

        // The scan names the token. Only a read token reads it and its report.
        var polled = await Json(reader.Client.GetAsync(s.Url($"/{scan}")));
        Assert.Equal(("completed", "comparison", $"agent-token:{scanner.TokenId}"), (polled.Text("status"), polled.Text("kind"), polled.Text("requestedBy")));
        Assert.Equal(HttpStatusCode.Forbidden, (await scanner.Client.GetAsync(s.Url($"/{scan}"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await scanner.Client.GetAsync(ReportUrl(s, scan))).StatusCode);
        Assert.Equal(await MarkdownAsync(s.Owner, ReportUrl(s, scan)), await MarkdownAsync(reader.Client, ReportUrl(s, scan)));
        Assert.Equal("off", (await ReportJsonAsync(reader.Client, ReportUrl(s, scan)))["gate"]!["verdict"]!.GetValue<string>());

        // Neither token reaches anything a person decides.
        foreach (var token in new[] { reader, scanner })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await token.Client.PostAsync(s.Url($"/{scan}/track"), null)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await token.Client.PostAsync(s.Url($"/{scan}/cancel"), null)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await token.Client.PutAsJsonAsync(s.Model("impact-gate"), new { policy = "off" })).StatusCode);
        }

        // A token of this workspace restricted to another project reads nothing here.
        var other = Guid.NewGuid();
        await TestHost.Created(s.Owner.PostAsJsonAsync($"/api/workspaces/{s.WorkspaceId}/projects", new { id = other, name = "Other" }));
        var elsewhere = await TestHost.CreateAgentAsync(factory, s.Owner, s.WorkspaceId, ["read", "scan-pull-request"], projectId: other);
        Assert.Equal(HttpStatusCode.Forbidden, (await elsewhere.Client.GetAsync(ReportUrl(s, scan))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await elsewhere.Client.PostAsJsonAsync(s.Url(), new { repository = "acme/orders", pullRequest = 1 })).StatusCode);
    }

    [Fact]
    public async Task The_report_is_closed_to_another_workspace_and_to_another_project()
    {
        var s = await ImportScenario.CreateAsync(import);
        var scan = (await s.ScanAsync()).Id();
        Assert.Equal(HttpStatusCode.OK, (await s.Owner.GetAsync(ReportUrl(s, scan))).StatusCode);

        var factory = import.Host.Factory;
        using var stranger = await TestHost.SignInAsync(factory);
        var (strangerWorkspace, mine) = await TestHost.CreateProjectAsync(stranger);
        Assert.Equal(HttpStatusCode.Forbidden, (await stranger.GetAsync(ReportUrl(s, scan))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync($"/api/projects/{mine}/imports/{scan}/impact/report")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await stranger.GetAsync(s.Model("impact-gate"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await stranger.PutAsJsonAsync(s.Model("impact-gate"), new { policy = "fail-on-broken" })).StatusCode);

        var strangerToken = await TestHost.CreateAgentAsync(factory, stranger, strangerWorkspace, ["read", "scan-pull-request"]);
        Assert.Equal(HttpStatusCode.Forbidden, (await strangerToken.Client.GetAsync(ReportUrl(s, scan))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await strangerToken.Client.GetAsync($"/api/projects/{mine}/imports/{scan}/impact/report")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await strangerToken.Client.PostAsJsonAsync(s.Url(), new { repository = "acme/orders", pullRequest = 1 })).StatusCode);
    }

    [Fact]
    public void The_state_of_a_pull_request_is_one_of_four_words_and_never_the_text_of_GitHub()
    {
        static Vellum.Modules.Import.Scans.ScanPullRequest Pull(string state, bool merged) =>
            new(1, "t", state, merged, "main", ImportScenario.Commit1, "b", ImportScenario.Commit2, "acme/orders", false);
        Assert.Equal(["open", "closed", "merged", "unknown"],
            new[] { Pull("open", false), Pull("closed", false), Pull("closed", true), Pull("**open** [x](https://evil.example)", false) }.Select(ImpactReport.PullState));
    }

    [Fact]
    public void The_markdown_is_cut_at_a_line_end_with_a_line_that_says_so()
    {
        var line = new string('x', 99) + "\n";
        var text = string.Concat(Enumerable.Repeat(line, ImpactReport.MaxMarkdownChars / 50));
        var cut = ImpactReport.Bounded(text);
        Assert.True(cut.Length <= ImpactReport.MaxMarkdownChars);
        Assert.EndsWith("The full report lists everything.\n", cut);
        Assert.Equal(ImpactReport.Bounded(text), cut);
        Assert.Equal(line, ImpactReport.Bounded(line));
    }
}
