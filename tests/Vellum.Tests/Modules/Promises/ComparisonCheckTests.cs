using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Vellum.Modules.Promises;
using Vellum.Tests.Modules.Import;
using Vellum.Tests.Shared.Outbound;

namespace Vellum.Tests.Modules.Promises;

/// <summary>
/// A source check against a comparison scan (a pull request, another ref): it is bounded apart
/// from the runs against the followed ref, it runs once per scan, it charges the run rate per
/// check, it fails only where the scan could have found the pattern under the linked service,
/// and a comparison snapshot never stands for the newest state of the repository.
/// </summary>
public class ComparisonCheckTests(ImportHost import) : IClassFixture<ImportHost>
{
    private static async Task<JsonObject> Ok(Task<HttpResponseMessage> request, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = await request;
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode}: {text}");
        return JsonNode.Parse(text)!.AsObject();
    }

    private static string Promise(ImportScenario s, Guid id, string path = "") => s.Model($"promises/{id}{path}");

    private static Task<JsonObject> RunAsync(ImportScenario s, Guid id, Guid scanId, HttpStatusCode expected = HttpStatusCode.OK) =>
        Ok(s.Owner.PostAsJsonAsync(Promise(s, id, "/obligations/O1/run"), new { scanId }), expected);

    private static async Task<List<JsonObject>> RunsAsync(ImportScenario s, Guid id) =>
        [.. (await Ok(s.Owner.GetAsync(Promise(s, id, "/runs"))))["items"]!.AsArray().Select(r => r!.AsObject())];

    private static Guid Element(List<JsonObject> elements, string name) => elements.Single("name", name).Id();

    /// <summary>The fixture <c>orders</c> with every path under <c>orders-api/</c> moved to <c>services/orders-api/</c>. The outbox is still declared.</summary>
    private static Dictionary<string, byte[]> Moved() =>
        FixtureGitHub.Load("orders").ToDictionary(f => f.Key.StartsWith("orders-api/", StringComparison.Ordinal) ? "services/" + f.Key : f.Key, f => f.Value, StringComparer.Ordinal);

    /// <summary>The fixture <c>orders</c> with a second project file in <c>orders-api/</c>: the service key becomes <c>orders-api#orders-api/Orders.Api.csproj</c>. The outbox is still declared.</summary>
    private static Dictionary<string, byte[]> SecondProject() =>
        new(FixtureGitHub.Load("orders"), StringComparer.Ordinal)
        {
            ["orders-api/Tools.csproj"] = Encoding.UTF8.GetBytes("<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType></PropertyGroup></Project>\n"),
        };

    [Fact]
    public async Task A_service_whose_linked_key_is_gone_at_the_commit_gives_no_failure_for_a_moved_directory_or_a_second_project_file()
    {
        foreach (var (name, files) in new[] { ("moved", Moved()), ("second project", SecondProject()) })
        {
            var s = await PromiseBindingTests.WithGitHubAsync(import);
            await s.ScanAndAcceptAllAsync();
            var ofOrdersApi = await PromiseBindingTests.OutboxPromiseAsync(s, Element(await s.ElementsAsync(), "Orders.Api"));
            Assert.Equal("passed", (await RunAsync(s, ofOrdersApi, (await s.ScanAsync()).Id()))["result"]!.GetValue<string>());

            s.GitHub.Commit(ImportScenario.Commit2, files);
            s.GitHub.Branches["change"] = ImportScenario.Commit2;
            s.GitHub.Head = ImportScenario.Commit1;
            var comparison = await s.ScanAsync(body: new { repository = "acme/orders", @ref = "change" });
            Assert.Equal("completed", comparison.Text("status"));

            var run = await RunAsync(s, ofOrdersApi, comparison.Id());
            Assert.True(run["result"]!.GetValue<string>() == "unsupported", $"{name}: {run}");
            Assert.Contains("the service is not found under its linked key at this commit", run["detail"]!.GetValue<string>());
            Assert.Equal("service-not-found", run["inputs"]!["coverage"]!.GetValue<string>());

            // The same holds for a scan of the followed ref: the rule is for every scan.
            if (name == "moved")
            {
                s.GitHub.Head = ImportScenario.Commit2;
                var tracking = await s.ScanAsync();
                Assert.Equal("tracking", tracking.Text("kind"));
                Assert.Equal("unsupported", (await RunAsync(s, ofOrdersApi, tracking.Id()))["result"]!.GetValue<string>());
            }
        }
    }

    [Fact]
    public async Task A_check_runs_once_against_a_comparison_scan_and_never_uses_up_the_runs_against_the_followed_ref()
    {
        using var limited = ImportHost.With(import.Fixture,
            services => services.AddSingleton(new PromiseLimits(MaxRunsPerObligation: 1, RunsPerMinute: 100, MaxComparisonRunsPerObligation: 1)));
        var s = await PromiseBindingTests.WithGitHubAsync(limited);
        await s.ScanAndAcceptAllAsync();
        var whole = await PromiseBindingTests.OutboxPromiseAsync(s);
        s.GitHub.Commit(ImportScenario.Commit2, FixtureGitHub.Load("orders-outbox-removed"));
        s.GitHub.Branches["remove-outbox"] = ImportScenario.Commit2;
        s.GitHub.Commit(ImportScenario.Commit3, Moved());
        s.GitHub.Branches["move"] = ImportScenario.Commit3;
        s.GitHub.Head = ImportScenario.Commit1;
        var first = (await s.ScanAsync(body: new { repository = "acme/orders", @ref = "remove-outbox" })).Id();
        var second = (await s.ScanAsync(body: new { repository = "acme/orders", @ref = "move" })).Id();

        // One run against the first comparison; the same call again, by either endpoint, returns it.
        var run = await RunAsync(s, whole, first);
        Assert.Equal("failed", run["result"]!.GetValue<string>());
        Assert.Equal(run["id"]!.GetValue<Guid>(), (await RunAsync(s, whole, first))["id"]!.GetValue<Guid>());
        var checks = await Ok(s.Owner.PostAsync(s.Url($"/{first}/impact/checks"), null));
        Assert.Equal(run["id"]!.GetValue<Guid>(), checks["runs"]![0]!["runId"]!.GetValue<Guid>());
        Assert.Single(await RunsAsync(s, whole));

        // The comparison run did not use up the one run against the followed ref.
        var followed = await RunAsync(s, whole, (await s.ScanAsync()).Id());
        Assert.Equal("passed", followed["result"]!.GetValue<string>());

        // The comparison runs have their own bound. Reaching it leaves the followed ref as it was.
        var refused = await RunAsync(s, whole, second, HttpStatusCode.Conflict);
        Assert.Contains("runs against comparison scans", refused["title"]!.GetValue<string>() + refused["detail"]?.GetValue<string>());
        var promise = await Ok(s.Owner.GetAsync(Promise(s, whole)));
        Assert.True(promise["current"]!["evidenceStatus"]!["counts"]!["passed"]!.GetValue<int>() == 1, promise["current"]!["evidenceStatus"]!.ToJsonString());
        Assert.Equal(2, (await RunsAsync(s, whole)).Count);
    }

    [Fact]
    public async Task The_checks_of_a_scan_take_one_unit_of_the_run_rate_each_and_a_later_call_goes_on_where_the_last_stopped()
    {
        using var limited = ImportHost.With(import.Fixture, services => services.AddSingleton(new PromiseLimits(RunsPerMinute: 1)));
        var s = await PromiseBindingTests.WithGitHubAsync(limited);
        await s.ScanAndAcceptAllAsync();
        var one = await PromiseBindingTests.OutboxPromiseAsync(s);
        var two = await PromiseBindingTests.OutboxPromiseAsync(s);
        s.GitHub.Commit(ImportScenario.Commit2, FixtureGitHub.Load("orders-outbox-removed"));
        s.GitHub.Branches["remove-outbox"] = ImportScenario.Commit2;
        s.GitHub.Head = ImportScenario.Commit1;
        var scan = (await s.ScanAsync(body: new { repository = "acme/orders", @ref = "remove-outbox" })).Id();

        var response = await s.Owner.PostAsync(s.Url($"/{scan}/impact/checks"), null);
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.NotNull(response.Headers.RetryAfter);
        Assert.Contains("1 of 2 checks ran", await response.Content.ReadAsStringAsync());
        Assert.Equal(1, (await RunsAsync(s, one)).Count + (await RunsAsync(s, two)).Count);
    }

    [Fact]
    public async Task A_run_against_a_comparison_is_no_place_where_the_pattern_was_found_before()
    {
        var s = await PromiseBindingTests.WithGitHubAsync(import);
        await s.ScanAndAcceptAllAsync();
        var whole = await PromiseBindingTests.OutboxPromiseAsync(s);
        // A copy of main on another branch: the pattern is found there, at Commit3.
        s.GitHub.Commit(ImportScenario.Commit3, FixtureGitHub.Load("orders"));
        s.GitHub.Branches["copy"] = ImportScenario.Commit3;
        s.GitHub.Head = ImportScenario.Commit1;
        var copy = (await s.ScanAsync(body: new { repository = "acme/orders", @ref = "copy" })).Id();
        Assert.Equal("passed", (await RunAsync(s, whole, copy))["result"]!.GetValue<string>());

        s.GitHub.Commit(ImportScenario.Commit2, FixtureGitHub.Load("orders-outbox-removed"));
        s.GitHub.Head = ImportScenario.Commit2;
        var failed = await RunAsync(s, whole, (await s.ScanAsync()).Id());
        Assert.Equal("failed", failed["result"]!.GetValue<string>());
        Assert.DoesNotContain(ImportScenario.Commit3, failed["detail"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_pull_request_snapshot_is_never_the_newest_state_of_the_repository()
    {
        var s = await PromiseBindingTests.WithGitHubAsync(import);
        // Main is scanned and nothing is accepted: the newest snapshot stands for the repository.
        var main = (await s.ScanAsync()).Id();
        var whole = await PromiseBindingTests.OutboxPromiseAsync(s);
        Assert.Equal("passed", (await RunAsync(s, whole, main))["result"]!.GetValue<string>());

        s.GitHub.Commit(ImportScenario.Commit2, FixtureGitHub.Load("orders-outbox-removed"));
        var pull = JsonNode.Parse(Fixtures.Read("GitHub/pull-request.json"))!.AsObject();
        pull["number"] = 1;
        pull["head"]!["repo"] = new JsonObject { ["full_name"] = "acme/orders" };
        s.GitHub.Pulls[1] = pull;
        Assert.Equal("completed", (await s.ScanAsync(body: new { repository = "acme/orders", pullRequest = 1 })).Text("status"));
        s.GitHub.Commit(ImportScenario.Commit3, FixtureGitHub.Load("orders-outbox-removed"));
        Assert.Equal("completed", (await s.ScanAsync(body: new { repository = "acme/orders", @ref = ImportScenario.Commit3 })).Text("status"));

        // The pass on main is still current: neither the pull request nor the bare commit moved the repository on.
        var status = (await Ok(s.Owner.GetAsync(Promise(s, whole))))["current"]!["evidenceStatus"]!["obligations"]![0]!.AsObject();
        Assert.Equal(("passed", true), (status["result"]!.GetValue<string>(), status["current"]!.GetValue<bool>()));
    }

    [Fact]
    public async Task A_scan_token_scans_only_a_repository_that_the_project_follows()
    {
        var s = await ImportScenario.CreateAsync(import);
        var factory = import.Host.Factory;
        var token = await TestHost.CreateAgentAsync(factory, s.Owner, s.WorkspaceId, ["scan-pull-request"], projectId: s.ProjectId);
        s.GitHub.Pulls[1] = JsonNode.Parse(Fixtures.Read("GitHub/pull-request.json"))!.AsObject();

        // No scan of acme/orders was accepted yet: the project follows nothing.
        var refused = await Ok(token.Client.PostAsJsonAsync(s.Url(), new { repository = "acme/orders", pullRequest = 1 }), HttpStatusCode.Forbidden);
        Assert.Equal("agent_not_allowed", refused.Text("type"));

        await s.ScanAndAcceptAllAsync();
        await Ok(token.Client.PostAsJsonAsync(s.Url(), new { repository = "acme/orders", pullRequest = 1 }), HttpStatusCode.Accepted);
        Assert.Equal("agent_not_allowed",
            (await Ok(token.Client.PostAsJsonAsync(s.Url(), new { repository = "acme/other", pullRequest = 1 }), HttpStatusCode.Forbidden)).Text("type"));
        await s.DrainAsync();
    }
}
