using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using Deedbox;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using Vellum.Modules.Identity;
using Vellum.Tests.Modules.Import;
using Vellum.Tests.Shared.Outbound;

namespace Vellum.Tests.Modules.Promises;

/// <summary>
/// Promises through the HTTP API on real Postgres: requirement IDs, the lifecycle, links,
/// obligations, the two deterministic checks, external evidence, isolation and the read model.
/// </summary>
[Collection("Integration")]
public class PromiseEndpointTests(ImportHost import) : IClassFixture<ImportHost>
{
    private static readonly string Sha = new('a', 40);

    private async Task<(HttpClient Owner, Guid WorkspaceId, Guid ProjectId)> NewProjectAsync()
    {
        var owner = await TestHost.SignInAsync(import.Host.Factory);
        var (workspaceId, projectId) = await TestHost.CreateProjectAsync(owner);
        return (owner, workspaceId, projectId);
    }

    private static string Url(Guid projectId, string path = "") => $"/api/projects/{projectId}/promises{path}";

    private static JsonObject Body(string title = "A promise", JsonObject? links = null) => new()
    {
        ["title"] = title,
        ["claim"] = "Something must always hold.",
        ["links"] = links,
    };

    private static async Task<JsonObject> Ok(Task<HttpResponseMessage> request, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = await request;
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<JsonObject>())!;
    }

    private static async Task<JsonObject> Refused(Task<HttpResponseMessage> request, HttpStatusCode expected)
    {
        var response = await request;
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode}: {text}");
        return JsonNode.Parse(text)!.AsObject();
    }

    private static Task<HttpResponseMessage> Post(HttpClient client, string url, JsonNode? body = null) =>
        client.PostAsync(url, body is null ? null : OrdersReference.Json(body));

    private static async Task SetPrefixAsync(HttpClient owner, Guid projectId, string prefix = "ORD") =>
        await Ok(owner.PutAsJsonAsync(Url(projectId, "/prefix"), new { prefix }));

    private static async Task<List<JsonObject>> AllAsync(HttpClient client, string url)
    {
        var items = new List<JsonObject>();
        string? cursor = null;
        do
        {
            var page = (await client.GetFromJsonAsync<JsonObject>($"{url}{(url.Contains('?') ? '&' : '?')}limit=7{(cursor is null ? "" : "&cursor=" + Uri.EscapeDataString(cursor))}"))!;
            items.AddRange(page["items"]!.AsArray().Select(i => i!.AsObject()));
            cursor = page["cursor"]?.GetValue<string>();
        }
        while (cursor is not null);
        return items;
    }

    [Fact]
    public async Task Requirement_ids_are_issued_in_sequence_under_concurrency_and_never_reused()
    {
        var (owner, _, projectId) = await NewProjectAsync();

        // No prefix yet: nothing can be numbered.
        var early = await Refused(Post(owner, Url(projectId), Body()), HttpStatusCode.Conflict);
        Assert.Contains("prefix", early["title"]!.GetValue<string>());
        await SetPrefixAsync(owner, projectId);

        var created = await Task.WhenAll(Enumerable.Range(0, 20).Select(i => Ok(Post(owner, Url(projectId), Body($"Promise {i}")), HttpStatusCode.Created)));
        var ids = created.Select(c => c["requirementId"]!.GetValue<string>()).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(Enumerable.Range(1, 20).Select(n => $"ORD-{n:000}"), ids);

        // The list pages in requirement order and shows every promise once.
        Assert.Equal(ids, (await AllAsync(owner, Url(projectId))).Select(p => p["requirementId"]!.GetValue<string>()));

        // A deleted draft keeps its number: the next promise gets a new one.
        var last = created.Single(c => c["requirementId"]!.GetValue<string>() == "ORD-020");
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync(Url(projectId, $"/{last["id"]}"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync(Url(projectId, $"/{last["id"]}"))).StatusCode);
        var next = await Ok(Post(owner, Url(projectId), Body("After the delete")), HttpStatusCode.Created);
        Assert.Equal("ORD-021", next["requirementId"]!.GetValue<string>());
        Assert.DoesNotContain("ORD-020", (await AllAsync(owner, Url(projectId))).Select(p => p["requirementId"]!.GetValue<string>()));

        // A refused create takes no number.
        await Refused(Post(owner, Url(projectId), new JsonObject { ["title"] = "", ["claim"] = "x" }), HttpStatusCode.BadRequest);
        Assert.Equal("ORD-022", (await Ok(Post(owner, Url(projectId), Body()), HttpStatusCode.Created))["requirementId"]!.GetValue<string>());
    }

    [Fact]
    public async Task The_prefix_is_set_once()
    {
        var (owner, workspaceId, projectId) = await NewProjectAsync();
        var editor = await TestHost.SignInMemberAsync(import.Host.Factory, owner, workspaceId, "Editor");

        Assert.Null((await owner.GetFromJsonAsync<JsonObject>(Url(projectId, "/prefix")))!["prefix"]);
        foreach (var bad in new[] { "o", "ord", "TOOLONG", "OR1", "" })
            await Refused(owner.PutAsJsonAsync(Url(projectId, "/prefix"), new { prefix = bad }), HttpStatusCode.BadRequest);
        Assert.Equal(HttpStatusCode.Forbidden, (await editor.PutAsJsonAsync(Url(projectId, "/prefix"), new { prefix = "PAY" })).StatusCode);

        var set = await Ok(owner.PutAsJsonAsync(Url(projectId, "/prefix"), new { prefix = "PAY", startAt = 7 }));
        Assert.Equal(("PAY", 7), (set["prefix"]!.GetValue<string>(), set["nextNumber"]!.GetValue<int>()));
        await Refused(owner.PutAsJsonAsync(Url(projectId, "/prefix"), new { prefix = "PAY" }), HttpStatusCode.Conflict);
        await Refused(owner.PutAsJsonAsync(Url(projectId, "/prefix"), new { prefix = "ORD" }), HttpStatusCode.Conflict);

        Assert.Equal("PAY-007", (await Ok(Post(editor, Url(projectId), Body()), HttpStatusCode.Created))["requirementId"]!.GetValue<string>());
        Assert.Equal(8, (await owner.GetFromJsonAsync<JsonObject>(Url(projectId, "/prefix")))!["nextNumber"]!.GetValue<int>());
    }

    [Fact]
    public async Task An_approved_revision_is_fixed_and_a_new_revision_does_not_replace_it_until_it_is_approved()
    {
        var (owner, _, projectId) = await NewProjectAsync();
        var orders = await OrdersReference.SeedAsync(owner, projectId);
        var draft = await orders.CreateOrd014Async();
        var id = draft["id"]!.GetValue<Guid>();
        Assert.Equal(("ORD-014", "draft", "human"), (draft["requirementId"]!.GetValue<string>(), draft["state"]!.GetValue<string>(), draft["origin"]!.GetValue<string>()));
        Assert.Null(draft["inForceRevision"]);

        // Approval needs the revision the approver read, and a complete promise.
        var wrongRevision = await orders.ApprovalAsync(id);
        wrongRevision["revision"] = 2;
        Assert.Equal("revision_changed", (await Refused(Post(owner, Url(projectId, $"/{id}/approve"), wrongRevision), HttpStatusCode.Conflict))["type"]!.GetValue<string>());
        var incomplete = orders.Ord014();
        incomplete["evidenceCriteria"] = " ";
        await Ok(owner.PutAsync(Url(projectId, $"/{id}"), OrdersReference.Json(incomplete)));
        var missing = await Refused(Post(owner, Url(projectId, $"/{id}/approve"), await orders.ApprovalAsync(id)), HttpStatusCode.BadRequest);
        Assert.Equal("evidenceCriteria", missing["errors"]![0]!["field"]!.GetValue<string>());
        await Ok(owner.PutAsync(Url(projectId, $"/{id}"), OrdersReference.Json(orders.Ord014())));

        var approved = await orders.ApproveAsync(id);
        Assert.Equal(("approved", 1), (approved["state"]!.GetValue<string>(), approved["inForceRevision"]!.GetValue<int>()));
        Assert.NotNull(approved["current"]!["approvedBy"]);
        Assert.NotNull(approved["current"]!["approvedAt"]);
        Assert.Null(approved["draft"]);

        // Nothing changes an approved revision: not its text, not its obligations, and it is not deleted.
        await Refused(owner.PutAsync(Url(projectId, $"/{id}"), OrdersReference.Json(orders.Ord014())), HttpStatusCode.Conflict);
        await Refused(owner.DeleteAsync(Url(projectId, $"/{id}/obligations/O1")), HttpStatusCode.Conflict);
        await Refused(Post(owner, Url(projectId, $"/{id}/obligations/generate")), HttpStatusCode.Conflict);
        await Refused(Post(owner, Url(projectId, $"/{id}/obligations"), new JsonObject { ["kind"] = "manual-review", ["title"] = "t", ["recipe"] = new JsonObject { ["text"] = "x" } }), HttpStatusCode.Conflict);
        await Refused(owner.DeleteAsync(Url(projectId, $"/{id}")), HttpStatusCode.Conflict);
        await Refused(Post(owner, Url(projectId, $"/{id}/approve"), await orders.ApprovalAsync(id)), HttpStatusCode.Conflict);

        // A change is a new draft revision. It starts as a copy; the approved one stays in force.
        var revised = await Ok(Post(owner, Url(projectId, $"/{id}/revise")));
        Assert.Equal((2, 1, "approved"), (revised["latestRevision"]!.GetValue<int>(), revised["inForceRevision"]!.GetValue<int>(), revised["state"]!.GetValue<string>()));
        Assert.Equal(("draft", 2), (revised["draft"]!["status"]!.GetValue<string>(), revised["draft"]!["revision"]!.GetValue<int>()));
        Assert.Equal(revised["current"]!["obligations"]!.ToJsonString(), revised["draft"]!["obligations"]!.ToJsonString());
        await Refused(Post(owner, Url(projectId, $"/{id}/revise")), HttpStatusCode.Conflict);

        var changed = orders.Ord014();
        changed["claim"] = "A changed claim.";
        var edited = await Ok(owner.PutAsync(Url(projectId, $"/{id}"), OrdersReference.Json(changed)));
        Assert.Equal("A changed claim.", edited["draft"]!["claim"]!.GetValue<string>());
        Assert.StartsWith("When the Orders API accepts", edited["current"]!["claim"]!.GetValue<string>());
        Assert.True(edited["current"]!["inForce"]!.GetValue<bool>());
        Assert.Contains(edited["draft"]!["evidenceStatus"]!["gaps"]!.AsArray(), g => g!.GetValue<string>().Contains("is a draft"));

        // The new revision takes over only when a person approves it. The old one stays readable.
        var second = await orders.ApproveAsync(id, 2);
        Assert.Equal((2, "A changed claim."), (second["inForceRevision"]!.GetValue<int>(), second["current"]!["claim"]!.GetValue<string>()));
        Assert.Equal(["approved", "approved"], second["revisions"]!.AsArray().Select(r => r!["status"]!.GetValue<string>()));
        Assert.StartsWith("When the Orders API accepts", second["revisions"]![0]!["claim"]!.GetValue<string>());

        // An approved promise leaves only through an approved revision that retires it.
        var retiring = await Ok(Post(owner, Url(projectId, $"/{id}/revise"), new JsonObject { ["retire"] = true }));
        Assert.Equal("approved", retiring["state"]!.GetValue<string>());
        var retired = await orders.ApproveAsync(id, 3);
        Assert.Equal("retired", retired["state"]!.GetValue<string>());
        await Refused(Post(owner, Url(projectId, $"/{id}/revise")), HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Only_an_owner_in_an_interactive_session_approves()
    {
        using var factory = TestHost.Create(import.Fixture.ConnectionString);
        var owner = await TestHost.SignInAsync(factory);
        var (workspaceId, projectId) = await TestHost.CreateProjectAsync(owner);
        var editor = await TestHost.SignInMemberAsync(factory, owner, workspaceId, "Editor");
        var ownerId = (await owner.GetFromJsonAsync<UserInfoResponse>("/api/auth/me"))!.Id;
        var orders = await OrdersReference.SeedAsync(owner, projectId);

        // A real agent token of the same owner, with every scope. It is the owner in every
        // role check and it is not the owner's session.
        var agent = (await TestHost.CreateAgentAsync(factory, owner, workspaceId)).Client;
        Assert.Equal(HttpStatusCode.OK, (await agent.GetAsync(Url(projectId))).StatusCode);

        // The body says a person wrote the promise. The origin is what the caller is.
        await Ok(owner.PutAsJsonAsync(Url(projectId, "/prefix"), new { prefix = "ORD", startAt = 14 }));
        var suggestion = orders.Ord014();
        suggestion["origin"] = "human";
        var suggested = await Ok(Post(agent, Url(projectId), suggestion), HttpStatusCode.Created);
        var id = suggested["id"]!.GetValue<Guid>();
        Assert.Equal(("agent-suggested", "draft"), (suggested["origin"]!.GetValue<string>(), suggested["state"]!.GetValue<string>()));

        // The agent may work on the draft. It may not approve it, whatever role its user has.
        await Ok(Post(agent, Url(projectId, $"/{id}/obligations/generate")));
        var refused = await Refused(agent.PostAsJsonAsync(Url(projectId, $"/{id}/approve"), new { revision = 1 }), HttpStatusCode.Forbidden);
        Assert.Equal("human_only", refused["type"]!.GetValue<string>());

        // An editor in a real session is below the role approval needs.
        Assert.Equal(HttpStatusCode.Forbidden, (await editor.PostAsJsonAsync(Url(projectId, $"/{id}/approve"), new { revision = 1 })).StatusCode);
        Assert.Equal("draft", (await orders.GetAsync(id))["state"]!.GetValue<string>());

        // A run approves nothing either. The agent may run a check of the draft when it names the draft.
        Assert.True((await Ok(Post(agent, Url(projectId, $"/{id}/obligations/O1/run"), new JsonObject { ["revision"] = 1 })))["draft"]!.GetValue<bool>());
        Assert.Equal("draft", (await orders.GetAsync(id))["state"]!.GetValue<string>());

        var approved = await orders.ApproveAsync(id);
        Assert.Equal(("approved", ownerId), (approved["state"]!.GetValue<string>(), approved["current"]!["approvedBy"]!.GetValue<string>()));

        // After approval the agent cannot revise its way around the gate: the draft it starts is not in force.
        await Ok(Post(agent, Url(projectId, $"/{id}/revise")));
        await Refused(agent.PostAsJsonAsync(Url(projectId, $"/{id}/approve"), new { revision = 2 }), HttpStatusCode.Forbidden);
        Assert.Equal(1, (await orders.GetAsync(id))["inForceRevision"]!.GetValue<int>());
    }

    [Fact]
    public void No_mcp_tool_approves_a_promise()
    {
        var tools = typeof(Program).Assembly.GetTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance))
            .Select(m => (Method: m, Tool: m.GetCustomAttribute<McpServerToolAttribute>()))
            .Where(x => x.Tool is not null)
            .ToList();

        Assert.NotEmpty(tools);
        Assert.DoesNotContain(tools, x => (x.Tool!.Name ?? x.Method.Name).Contains("approve", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(tools, x => x.Method.DeclaringType!.Namespace!.Contains("Promises", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Links_must_exist_and_a_removed_contract_makes_the_promise_need_review()
    {
        var (owner, _, projectId) = await NewProjectAsync();
        var orders = await OrdersReference.SeedAsync(owner, projectId);
        await SetPrefixAsync(owner, projectId);

        // Every kind of link is checked against the project.
        var unknown = Guid.NewGuid();
        foreach (var kind in new[] { "scenarioIds", "operationIds", "channelIds", "subscriptionIds", "messageIds", "elementIds" })
        {
            var refused = await Refused(Post(owner, Url(projectId), Body(links: new JsonObject { [kind] = new JsonArray(unknown.ToString()) })), HttpStatusCode.BadRequest);
            Assert.Equal($"links.{kind}", refused["errors"]![0]!["field"]!.GetValue<string>());
        }
        // An operation ID is not a channel ID.
        await Refused(Post(owner, Url(projectId), Body(links: new JsonObject { ["channelIds"] = new JsonArray(OrdersReference.PlaceOrder.ToString()) })), HttpStatusCode.BadRequest);
        // A scenario of another project is not a scenario of this one.
        var (other, _, otherProject) = await NewProjectAsync();
        var foreign = await OrdersReference.SeedAsync(other, otherProject);
        await Refused(Post(owner, Url(projectId), Body(links: new JsonObject { ["scenarioIds"] = new JsonArray(foreign.ScenarioId.ToString()) })), HttpStatusCode.BadRequest);

        var created = await Ok(Post(owner, Url(projectId), orders.Ord014()), HttpStatusCode.Created);
        var id = created["id"]!.GetValue<Guid>();
        Assert.False(created["needsReview"]!.GetValue<bool>());
        Assert.Empty(created["missingLinks"]!.AsArray());
        await Ok(Post(owner, Url(projectId, $"/{id}/obligations/generate")));
        var approved = await orders.ApproveAsync(id);
        Assert.DoesNotContain(approved["current"]!["evidenceStatus"]!["gaps"]!.AsArray(), g => g!.GetValue<string>().Contains("changed after"));

        // A linked operation changes after approval: the link stays, and the change is a gap.
        Assert.Equal(HttpStatusCode.OK, (await owner.PatchAsJsonAsync(orders.Url($"operations/{OrdersReference.PlaceOrder}"), new { timeoutMs = 9000 })).StatusCode);
        var changed = await orders.GetAsync(id);
        Assert.False(changed["needsReview"]!.GetValue<bool>());
        Assert.Contains(changed["current"]!["evidenceStatus"]!["gaps"]!.AsArray(),
            g => g!.GetValue<string>() == $"The linked operation 'POST /orders' ({OrdersReference.PlaceOrder}) changed after this revision was approved.");

        // A linked subscription is removed: the promise needs review and still names the link.
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync(orders.Url($"subscriptions/{OrdersReference.InventorySub}"))).StatusCode);
        var removed = await orders.GetAsync(id);
        Assert.True(removed["needsReview"]!.GetValue<bool>());
        var gone = Assert.Single(removed["missingLinks"]!.AsArray())!;
        Assert.Equal(("subscription", OrdersReference.InventorySub), (gone["kind"]!.GetValue<string>(), gone["id"]!.GetValue<Guid>()));
        Assert.Contains(OrdersReference.InventorySub.ToString(), removed["current"]!["links"]!["subscriptionIds"]!.ToJsonString());
        Assert.Contains(removed["current"]!["evidenceStatus"]!["gaps"]!.AsArray(),
            g => g!.GetValue<string>() == $"The linked subscription {OrdersReference.InventorySub} no longer exists.");
        Assert.True((await AllAsync(owner, Url(projectId))).Single()["needsReview"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Generated_obligations_are_the_same_for_the_same_promise_and_a_plan_alone_is_not_run()
    {
        async Task<(OrdersReference Orders, Guid Id, JsonObject Generated)> GenerateAsync()
        {
            var (owner, _, projectId) = await NewProjectAsync();
            var orders = await OrdersReference.SeedAsync(owner, projectId);
            await Ok(owner.PutAsJsonAsync(Url(projectId, "/prefix"), new { prefix = "ORD", startAt = 14 }));
            var id = (await Ok(Post(owner, Url(projectId), orders.Ord014()), HttpStatusCode.Created))["id"]!.GetValue<Guid>();
            return (orders, id, await Ok(Post(owner, Url(projectId, $"/{id}/obligations/generate"))));
        }

        var (first, firstId, one) = await GenerateAsync();
        var (second, _, two) = await GenerateAsync();

        Assert.Equal(["ORD-014/O1", "ORD-014/O2", "ORD-014/O3", "ORD-014/O4"], one["added"]!.AsArray().Select(a => a!.GetValue<string>()));
        var obligations = one["promise"]!["current"]!["obligations"]!.AsArray();
        Assert.Equal(["scenario-check", "failure-test", "contract-test", "source-pattern-check"], obligations.Select(o => o!["kind"]!.GetValue<string>()));
        Assert.Equal([true, false, false, true], obligations.Select(o => o!["supported"]!.GetValue<bool>()));
        Assert.Equal(["deterministic", "external-evidence", "external-evidence", "deterministic"], obligations.Select(o => o!["execution"]!.GetValue<string>()));
        Assert.All(obligations, o => Assert.Equal("generated", o!["source"]!.GetValue<string>()));

        // The scenario's own assertions, then the forbidden event of the promise as a zero count.
        var expect = obligations[0]!["recipe"]!["scenarioCheck"]!["expect"]!.AsArray();
        Assert.Equal(["order-accepted", "accepted-order-reaches-the-broker", "forbidden-1-Dropped"], expect.Select(e => e!["id"]!.GetValue<string>()));
        Assert.Equal(("event-count", "Dropped", 0), (expect[2]!["type"]!.GetValue<string>(), expect[2]!["kind"]!.GetValue<string>(), expect[2]!["expected"]!.GetValue<int>()));
        Assert.Contains(OrdersReference.DeadLetters.ToString(), obligations[1]!["recipe"]!["plan"]!["contractIds"]!.ToJsonString());
        Assert.Equal("transactional-outbox", obligations[3]!["recipe"]!["sourcePattern"]!["pattern"]!.GetValue<string>());

        // The same promise on the same model gives the same obligations. Only the scenario and schema IDs differ: they are per project.
        string Normal(JsonObject generated, OrdersReference orders) =>
            generated["promise"]!["current"]!["obligations"]!.ToJsonString()
                .Replace(orders.ScenarioId.ToString(), "SCENARIO").Replace(orders.OrderRequestSchema.ToString(), "SCHEMA");
        Assert.Equal(Normal(one, first), Normal(two, second));

        // Generating again adds nothing. After a person removed one, it adds that one again under a new ID.
        var again = await Ok(Post(first.Client, Url(first.ProjectId, $"/{firstId}/obligations/generate")));
        Assert.Empty(again["added"]!.AsArray());
        await Ok(first.Client.DeleteAsync(Url(first.ProjectId, $"/{firstId}/obligations/O3")));
        var regenerated = await Ok(Post(first.Client, Url(first.ProjectId, $"/{firstId}/obligations/generate")));
        Assert.Equal(["ORD-014/O5"], regenerated["added"]!.AsArray().Select(a => a!.GetValue<string>()));

        // A generated plan alone is not run, and Vellum does not pretend to run it.
        var status = regenerated["promise"]!["current"]!["evidenceStatus"]!;
        Assert.Equal((0, 0, 4, 0), Counts(status));
        Assert.All(status["obligations"]!.AsArray(), o => Assert.Equal("not-run", o!["result"]!.GetValue<string>()));
        Assert.Contains(status["gaps"]!.AsArray(), g => g!.GetValue<string>() == "ORD-014/O2 is a test plan with no evidence source. No result can be recorded for it.");
        // The promise is a draft: an obligation of it runs only when the draft is named.
        var unnamed = await Refused(Post(first.Client, Url(first.ProjectId, $"/{firstId}/obligations/O2/run")), HttpStatusCode.Conflict);
        Assert.Equal("no_approved_revision", unnamed["type"]!.GetValue<string>());
        var draft = new JsonObject { ["revision"] = 1 };
        var notRunnable = await Refused(Post(first.Client, Url(first.ProjectId, $"/{firstId}/obligations/O2/run"), draft.DeepClone()), HttpStatusCode.Conflict);
        Assert.Equal("not_runnable", notRunnable["type"]!.GetValue<string>());

        // A kind Vellum does not know is stored, is unsupported and cannot be run or given evidence.
        var custom = await Ok(Post(first.Client, Url(first.ProjectId, $"/{firstId}/obligations"),
            new JsonObject { ["kind"] = "manual-review", ["title"] = "A person reads the code", ["recipe"] = new JsonObject { ["text"] = "Read the handler." } }), HttpStatusCode.Created);
        var unsupported = custom["current"]!["evidenceStatus"]!["obligations"]!.AsArray().Single(o => o!["obligationId"]!.GetValue<string>() == "ORD-014/O6")!;
        Assert.Equal("unsupported", unsupported["result"]!.GetValue<string>());
        Assert.Equal(1, Counts(custom["current"]!["evidenceStatus"]!).Unsupported);
        Assert.Equal("not_runnable", (await Refused(Post(first.Client, Url(first.ProjectId, $"/{firstId}/obligations/O6/run"), draft.DeepClone()), HttpStatusCode.Conflict))["type"]!.GetValue<string>());
        Assert.Equal("not_a_test_plan", (await Refused(Post(first.Client, Url(first.ProjectId, $"/{firstId}/obligations/O6/evidence"), Evidence("github.com", "passed", revision: 1)), HttpStatusCode.Conflict))["type"]!.GetValue<string>());

        // A typed recipe must fit its kind.
        await Refused(Post(first.Client, Url(first.ProjectId, $"/{firstId}/obligations"),
            new JsonObject { ["kind"] = "scenario-check", ["title"] = "No recipe", ["recipe"] = new JsonObject { ["text"] = "x" } }), HttpStatusCode.BadRequest);
        var malformed = await Refused(Post(first.Client, Url(first.ProjectId, $"/{firstId}/obligations"),
            new JsonObject { ["kind"] = "scenario-check", ["title"] = "t", ["recipe"] = new JsonObject { ["text"] = "x", ["scenarioCheck"] = new JsonObject { ["scenarioId"] = first.ScenarioId.ToString(), ["expect"] = new JsonArray(new JsonObject { ["id"] = "no-type" }) } } }), HttpStatusCode.BadRequest);
        Assert.Equal("invalid_body", malformed["type"]!.GetValue<string>());
    }

    /// <summary>Passed, failed, not run and unsupported. A stale result is in none of them; see <see cref="Stale"/>.</summary>
    private static (int Passed, int Failed, int NotRun, int Unsupported) Counts(JsonNode status) => (
        status["counts"]!["passed"]!.GetValue<int>(), status["counts"]!["failed"]!.GetValue<int>(),
        status["counts"]!["notRun"]!.GetValue<int>(), status["counts"]!["unsupported"]!.GetValue<int>());

    private static int Stale(JsonNode status) => status["counts"]!["stale"]!.GetValue<int>();

    [Fact]
    public async Task ORD_014_fails_on_the_design_without_an_outbox_and_passes_on_the_design_with_one()
    {
        var (owner, _, projectId) = await NewProjectAsync();
        var orders = await OrdersReference.SeedAsync(owner, projectId);
        var id = (await orders.CreateOrd014Async())["id"]!.GetValue<Guid>();
        await orders.ApproveAsync(id);

        // Without an outbox the API answers, then publishes. The broker is down: the event is lost.
        var failed = await orders.RunAsync(id, "O1");
        Assert.Equal(("failed", "declared-design", "ORD-014/O1", 1), (
            failed["result"]!.GetValue<string>(), failed["environment"]!.GetValue<string>(),
            failed["obligationId"]!.GetValue<string>(), failed["promiseRevision"]!.GetValue<int>()));
        Assert.Contains("[accepted-order-reaches-the-broker]", failed["detail"]!.GetValue<string>());
        Assert.DoesNotContain("[order-accepted]", failed["detail"]!.GetValue<string>());
        Assert.Contains("declared design", failed["limitations"]![0]!.GetValue<string>());
        Assert.Contains($"Scenario 'Place an order' ({orders.ScenarioId})", failed["scope"]!.GetValue<string>());
        var failedEvidence = Assert.Single(failed["evidence"]!.AsArray())!;
        Assert.Equal(("trace", orders.ScenarioId), (failedEvidence["kind"]!.GetValue<string>(), failedEvidence["scenarioId"]!.GetValue<Guid>()));

        // The same inputs give the same result and the same evidence hash.
        var failedAgain = await orders.RunAsync(id, "O1");
        Assert.Equal(failedEvidence["hash"]!.GetValue<string>(), failedAgain["evidence"]![0]!["hash"]!.GetValue<string>());
        Assert.Equal(failed["modelRevision"]!.ToJsonString(), failedAgain["modelRevision"]!.ToJsonString());
        Assert.NotEqual(failed["id"]!.GetValue<Guid>(), failedAgain["id"]!.GetValue<Guid>());

        var promise = await orders.GetAsync(id);
        Assert.Equal(1, Counts(promise["current"]!["evidenceStatus"]!).Failed);
        Assert.Equal(["declared-design"], promise["current"]!["evidenceStatus"]!["environments"]!.AsArray().Select(e => e!.GetValue<string>()));

        // The design that writes to an outbox first: the relay publishes when the broker is back.
        await orders.UseOutboxAsync();
        var passed = await orders.RunAsync(id, "O1");
        Assert.Equal("passed", passed["result"]!.GetValue<string>());
        Assert.StartsWith("All 3 assertion(s) hold.", passed["detail"]!.GetValue<string>());
        Assert.NotEqual(failedEvidence["hash"]!.GetValue<string>(), passed["evidence"]![0]!["hash"]!.GetValue<string>());
        Assert.Equal(passed["evidence"]![0]!["hash"]!.GetValue<string>(), (await orders.RunAsync(id, "O1"))["evidence"]![0]!["hash"]!.GetValue<string>());

        // The status names the run, the exact model version and what is still open. The scenario changed after approval.
        var status = (await orders.GetAsync(id))["current"]!["evidenceStatus"]!;
        Assert.Equal((1, 0, 3, 0), Counts(status));
        var checkedObligation = status["obligations"]![0]!;
        Assert.Equal(("passed", passed["scope"]!.GetValue<string>()), (checkedObligation["result"]!.GetValue<string>(), checkedObligation["scope"]!.GetValue<string>()));
        Assert.Single(status["modelRevisions"]!.AsArray());
        Assert.Contains(status["gaps"]!.AsArray(), g => g!.GetValue<string>().StartsWith("The linked scenario 'Place an order'", StringComparison.Ordinal));
        Assert.Contains(status["gaps"]!.AsArray(), g => g!.GetValue<string>() == "ORD-014/O4 has not been run.");

        // A pinned named revision gives the same result later, when the model has moved on.
        var revisionId = Guid.NewGuid();
        await TestHost.Created(owner.PostAsJsonAsync(orders.Url("revisions"), new { id = revisionId, name = "With outbox" }));
        await TestHost.Created(owner.PostAsJsonAsync(orders.Url("elements"), new { id = Guid.NewGuid(), kind = "system", name = "Unrelated" }));
        var pinned = await orders.RunAsync(id, "O1", new { modelRevisionId = revisionId });
        Assert.Equal(passed["evidence"]![0]!["hash"]!.GetValue<string>(), pinned["evidence"]![0]!["hash"]!.GetValue<string>());
        Assert.Equal((revisionId, "With outbox"), (pinned["modelRevision"]!["revisionId"]!.GetValue<Guid>(), pinned["modelRevision"]!["revisionName"]!.GetValue<string>()));
        var stale = (await orders.GetAsync(id))["current"]!["evidenceStatus"]!["gaps"]!.AsArray().Select(g => g!.GetValue<string>()).ToList();
        Assert.Contains(stale, g => g.StartsWith("ORD-014/O1 passed (stale): it ran on model version", StringComparison.Ordinal) && g.Contains("The model is now at version"));

        // Runs are appended: all five are there, newest first.
        var runs = await AllAsync(owner, Url(projectId, $"/{id}/runs"));
        Assert.Equal(["passed", "passed", "passed", "failed", "failed"], runs.Select(r => r["result"]!.GetValue<string>()));
        Assert.Equal(5, (await AllAsync(owner, Url(projectId, $"/{id}/runs?obligation=O1"))).Count);
        Assert.Empty(await AllAsync(owner, Url(projectId, $"/{id}/runs?obligation=O2")));

        // A scenario that cannot be played is not a failed promise: the check did not run.
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync(orders.Url($"operations/{OrdersReference.RelayOutbox}"))).StatusCode);
        var unplayable = await orders.RunAsync(id, "O1");
        Assert.Equal("not-run", unplayable["result"]!.GetValue<string>());
        Assert.Contains("cannot be played", unplayable["detail"]!.GetValue<string>());
    }

    /// <summary>The workflow runs and their jobs that the stand-in for GitHub answers with, by <c>owner/name/run number</c>.</summary>
    private sealed class Actions
    {
        public Dictionary<string, JsonObject> Runs { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, JsonArray> Jobs { get; } = new(StringComparer.Ordinal);

        /// <summary>A completed run of <c>.github/workflows/ci.yml</c> of <c>acme/orders</c>, unless said otherwise.</summary>
        public void Run(
            long number, string sha, string? conclusion = "success", string status = "completed",
            string path = ".github/workflows/ci.yml", string repository = "acme/orders", string? answersAs = null, params (string Name, string? Conclusion)[] jobs)
        {
            Runs[$"{repository}/{number}"] = new JsonObject
            {
                ["id"] = number, ["head_sha"] = sha, ["status"] = status, ["conclusion"] = conclusion, ["name"] = "CI", ["path"] = path,
                ["repository"] = new JsonObject { ["full_name"] = answersAs ?? repository },
            };
            Jobs[$"{repository}/{number}"] = new JsonArray(jobs.Select((job, i) => (JsonNode)new JsonObject
            {
                ["id"] = number * 100 + i, ["run_id"] = number, ["head_sha"] = sha, ["name"] = job.Name,
                ["status"] = job.Conclusion is null ? "in_progress" : "completed", ["conclusion"] = job.Conclusion,
            }).ToArray());
        }
    }

    private async Task<(ImportScenario Scenario, Actions Actions)> WithGitHubAsync()
    {
        var owner = await TestHost.SignInAsync(import.Host.Factory);
        var (workspaceId, projectId) = await TestHost.CreateProjectAsync(owner);
        var host = $"{Guid.NewGuid():N}.gh.test";
        await OutboundHost.PutSettingsAsync(owner, workspaceId, new JsonObject { ["github"] = new JsonObject { ["apiBaseUrl"] = $"https://{host}" } });

        var gitHub = new FixtureGitHub().Commit(ImportScenario.Commit1, FixtureGitHub.Load("orders"));
        var actions = new Actions();
        static HttpResponseMessage Json(JsonNode? body) => body is null
            ? new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("""{"message":"Not Found","status":"404"}""", System.Text.Encoding.UTF8, "application/json") }
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body.ToJsonString(), System.Text.Encoding.UTF8, "application/json") };
        import.Host.Transport.For(host).Then(sent =>
        {
            var segments = sent.Uri.AbsolutePath.Trim('/').Split('/');
            return segments switch
            {
                ["repos", var o, var r, "actions", "runs", var number] => Task.FromResult(Json(actions.Runs.GetValueOrDefault($"{o}/{r}/{number}"))),
                ["repos", var o, var r, "actions", "runs", var number, "jobs"] => Task.FromResult(Json(
                    actions.Jobs.TryGetValue($"{o}/{r}/{number}", out var jobs) ? new JsonObject { ["total_count"] = jobs.Count, ["jobs"] = jobs.DeepClone() } : null)),
                _ => gitHub.Handle(sent),
            };
        });
        return (new ImportScenario(import, owner, workspaceId, projectId, host, gitHub), actions);
    }

    /// <summary>Names the workflow whose run is the evidence of a test plan. Only a draft can be changed so.</summary>
    private static async Task<HttpResponseMessage> BindAsync(
        OrdersReference orders, Guid id, string key, string workflow = "ci.yml", string? job = null, string repository = "acme/orders", string system = "github-actions")
    {
        var obligation = (await orders.GetAsync(id))["revisions"]!.AsArray().Last()!["obligations"]!.AsArray().Single(o => o!["key"]!.GetValue<string>() == key)!;
        var recipe = obligation["recipe"]!.DeepClone();
        recipe["plan"]!["evidenceSource"] = new JsonObject { ["system"] = system, ["repository"] = repository, ["workflow"] = workflow, ["job"] = job };
        return await orders.Client.PutAsync(orders.Url($"promises/{id}/obligations/{key}"), OrdersReference.Json(new JsonObject
        {
            ["kind"] = obligation["kind"]!.GetValue<string>(), ["title"] = obligation["title"]!.GetValue<string>(), ["recipe"] = recipe,
        }));
    }

    [Fact]
    public async Task The_source_pattern_check_passes_on_orders_and_fails_when_the_outbox_is_removed()
    {
        var (s, _) = await WithGitHubAsync();
        var orders = await OrdersReference.SeedAsync(s.Owner, s.ProjectId);
        var id = (await orders.CreateOrd014Async())["id"]!.GetValue<Guid>();
        await orders.ApproveAsync(id);

        // No snapshot named: the check is recorded as not run.
        var notRun = await orders.RunAsync(id, "O4");
        Assert.Equal("not-run", notRun["result"]!.GetValue<string>());
        Assert.Empty(notRun["evidence"]!.AsArray());

        // A scan of another project, or no scan at all, is not a snapshot of this project.
        var missing = await Refused(s.Owner.PostAsJsonAsync(orders.Url($"promises/{id}/obligations/O4/run"), new { scanId = Guid.NewGuid() }), HttpStatusCode.BadRequest);
        Assert.Equal("scanId", missing["errors"]![0]!["field"]!.GetValue<string>());

        // The orders fixture declares the outbox: passed, with the lines as evidence.
        var first = await s.ScanAsync();
        Assert.Equal("completed", first.Text("status"));
        var passed = await orders.RunAsync(id, "O4", new { scanId = first.Id() });
        Assert.Equal(("passed", $"source@{ImportScenario.Commit1}", ImportScenario.Commit1, first.Id()), (
            passed["result"]!.GetValue<string>(), passed["environment"]!.GetValue<string>(),
            passed["commitSha"]!.GetValue<string>(), passed["scanId"]!.GetValue<Guid>()));
        var evidence = passed["evidence"]!.AsArray();
        Assert.NotEmpty(evidence);
        Assert.All(evidence, e =>
        {
            Assert.Equal("source", e!["kind"]!.GetValue<string>());
            Assert.StartsWith($"{s.GitHubHost}/acme/orders@{ImportScenario.Commit1}:orders-api/", e["pointer"]!.GetValue<string>());
            Assert.StartsWith($"https://{s.GitHubHost}/acme/orders/blob/{ImportScenario.Commit1}/orders-api/", e["url"]!.GetValue<string>());
        });
        Assert.Contains("does not show that the pattern works at run time", passed["limitations"]![0]!.GetValue<string>());
        Assert.Equal(passed["evidence"]!.ToJsonString(), (await orders.RunAsync(id, "O4", new { scanId = first.Id() }))["evidence"]!.ToJsonString());

        // A scan limited to some paths cannot establish absence: unsupported, never failed.
        var scopedId = await s.StartAsync(body: new { repository = "acme/orders", includePaths = new[] { "billing" } });
        await s.DrainAsync();
        Assert.Equal("completed", (await s.GetAsync(scopedId)).Text("status"));
        var unsupported = await orders.RunAsync(id, "O4", new { scanId = scopedId });
        Assert.Equal("unsupported", unsupported["result"]!.GetValue<string>());
        Assert.Contains("cannot establish absence", unsupported["detail"]!.GetValue<string>());
        Assert.Contains("It was found before at", unsupported["detail"]!.GetValue<string>());

        // The commit that removes the outbox, scanned whole: failed, and it says where the pattern was.
        s.GitHub.Commit(ImportScenario.Commit2, FixtureGitHub.Load("orders-outbox-removed"));
        var second = await s.ScanAsync();
        Assert.Equal((ImportScenario.Commit2, true), (second.Text("commitSha"), second["isComplete"]!.GetValue<bool>()));
        var failed = await orders.RunAsync(id, "O4", new { scanId = second.Id() });
        Assert.Equal(("failed", $"source@{ImportScenario.Commit2}"), (failed["result"]!.GetValue<string>(), failed["environment"]!.GetValue<string>()));
        Assert.Empty(failed["evidence"]!.AsArray());
        Assert.Contains($"It was found before at {s.GitHubHost}/acme/orders@{ImportScenario.Commit1}:orders-api/", failed["detail"]!.GetValue<string>());

        var status = (await orders.GetAsync(id))["current"]!["evidenceStatus"]!;
        Assert.Equal("failed", status["obligations"]![3]!["result"]!.GetValue<string>());
        Assert.Equal([ImportScenario.Commit2], status["commits"]!.AsArray().Select(c => c!.GetValue<string>()));

        // An older pass is not the newest scan: running it again on the old commit shows as a gap.
        await orders.RunAsync(id, "O4", new { scanId = first.Id() });
        var gaps = (await orders.GetAsync(id))["current"]!["evidenceStatus"]!["gaps"]!.AsArray().Select(g => g!.GetValue<string>());
        Assert.Contains($"ORD-014/O4 passed (stale): evidence is for {ImportScenario.Commit1}; the newest scan of {s.GitHubHost}/acme/orders is at {ImportScenario.Commit2}.", gaps);

        // A scan of another repository is not evidence for this obligation.
        s.GitHub.Commit(ImportScenario.Commit3, FixtureGitHub.Load("orders"));
        var other = await s.ScanAsync("acme/payments");
        var wrong = await Refused(s.Owner.PostAsJsonAsync(orders.Url($"promises/{id}/obligations/O4/run"), new { scanId = other.Id() }), HttpStatusCode.BadRequest);
        Assert.Contains("acme/payments", wrong["errors"]![0]!["message"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_pattern_is_attributed_to_a_service_through_the_accepted_import()
    {
        var (s, _) = await WithGitHubAsync();
        var scan = await s.ScanAndAcceptAllAsync();
        var elements = await s.ElementsAsync();
        var ordersApi = elements.Single("name", "Orders.Api").Id();
        var billing = elements.Single("name", "Billing").Id();
        var unlinked = Guid.NewGuid();
        await TestHost.Created(s.Owner.PostAsJsonAsync(s.Model("elements"), new { id = unlinked, kind = "system", name = "Drawn by hand" }));
        await SetPrefixAsync(s.Owner, s.ProjectId);

        async Task<string> ResultForAsync(Guid service)
        {
            var body = Body($"Outbox in {service}");
            body["requiresPattern"] = new JsonObject { ["repository"] = "ACME/Orders", ["pattern"] = "transactional-outbox", ["service"] = service.ToString() };
            var id = (await Ok(Post(s.Owner, Url(s.ProjectId), body), HttpStatusCode.Created))["id"]!.GetValue<Guid>();
            var generated = await Ok(Post(s.Owner, Url(s.ProjectId, $"/{id}/obligations/generate")));
            var obligation = Assert.Single(generated["promise"]!["current"]!["obligations"]!.AsArray())!;
            Assert.Equal("acme/orders", obligation["recipe"]!["sourcePattern"]!["repository"]!.GetValue<string>());
            var run = await Ok(s.Owner.PostAsJsonAsync(Url(s.ProjectId, $"/{id}/obligations/{obligation["key"]}/run"), new { scanId = scan.Id(), revision = 1 }));
            Assert.True(run["draft"]!.GetValue<bool>());
            return run["result"]!.GetValue<string>();
        }

        Assert.Equal("passed", await ResultForAsync(ordersApi));
        // Billing is a service of the repository and declares no outbox: a complete scan establishes that.
        Assert.Equal("failed", await ResultForAsync(billing));
        // No accepted import says which service of the repository this element is.
        Assert.Equal("unsupported", await ResultForAsync(unlinked));

        var unknownService = Body();
        unknownService["requiresPattern"] = new JsonObject { ["repository"] = "acme/orders", ["pattern"] = "transactional-outbox", ["service"] = Guid.NewGuid().ToString() };
        await Refused(Post(s.Owner, Url(s.ProjectId), unknownService), HttpStatusCode.BadRequest);
        var unknownPattern = Body();
        unknownPattern["requiresPattern"] = new JsonObject { ["repository"] = "acme/orders", ["pattern"] = "saga" };
        await Refused(Post(s.Owner, Url(s.ProjectId), unknownPattern), HttpStatusCode.BadRequest);
    }

    private static JsonObject Evidence(
        string host, string result, long run = 42, string? sha = null, string system = "github-actions", string repository = "acme/orders", int? revision = null) => new()
    {
        ["result"] = result,
        ["source"] = new JsonObject
        {
            ["system"] = system,
            ["runUrl"] = $"https://{host}/{repository}/actions/runs/{run}",
            ["commitSha"] = sha,
        },
        ["artifactDigest"] = "sha256:" + new string('c', 64),
        ["revision"] = revision,
    };

    [Fact]
    public async Task External_evidence_counts_only_for_the_obligation_it_is_bound_to()
    {
        var (s, actions) = await WithGitHubAsync();
        // The project follows acme/orders: its tracked ref is at Commit1.
        await s.ScanAndAcceptAllAsync();
        var orders = await OrdersReference.SeedAsync(s.Owner, s.ProjectId);
        var id = (await orders.CreateOrd014Async())["id"]!.GetValue<Guid>();
        string Path(string key) => Url(s.ProjectId, $"/{id}/obligations/{key}/evidence");

        // An evidence source is a workflow of a repository the project follows, on the workspace's GitHub.
        var notFollowed = await Refused(BindAsync(orders, id, "O2", repository: "acme/payments"), HttpStatusCode.BadRequest);
        Assert.Equal("recipe.plan.evidenceSource.repository", notFollowed["errors"]![0]!["field"]!.GetValue<string>());
        Assert.Equal("recipe.plan.evidenceSource.system", (await Refused(BindAsync(orders, id, "O2", system: "jenkins"), HttpStatusCode.BadRequest))["errors"]![0]!["field"]!.GetValue<string>());
        Assert.Equal("recipe.plan.evidenceSource.workflow", (await Refused(BindAsync(orders, id, "O2", workflow: " "), HttpStatusCode.BadRequest))["errors"]![0]!["field"]!.GetValue<string>());

        // O2 is bound to the job failure-tests of ci.yml of acme/orders. O3 stays unbound.
        var bound = await Ok(BindAsync(orders, id, "O2", job: "failure-tests"));
        Assert.Equal("ci.yml", bound["current"]!["obligations"]![1]!["recipe"]!["plan"]!["evidenceSource"]!["workflow"]!.GetValue<string>());
        await orders.ApproveAsync(id);

        async Task<JsonObject> StatusOfAsync(string obligationId) =>
            (await orders.GetAsync(id))["current"]!["evidenceStatus"]!["obligations"]!.AsArray().Single(o => o!["obligationId"]!.GetValue<string>() == obligationId)!.AsObject();
        async Task<string> ReasonAsync(string key, JsonObject body)
        {
            var recorded = await Ok(Post(s.Owner, Path(key), body));
            Assert.Equal("not-run", recorded["result"]!.GetValue<string>());
            Assert.Equal("claimed", recorded["evidence"]![0]!["identity"]!.GetValue<string>());
            return recorded["evidence"]![0]!["identityReason"]!.GetValue<string>();
        }

        // A real, successful run of the right workflow. It is evidence for O2 only.
        actions.Run(42, ImportScenario.Commit1, jobs: [("build", "success"), ("failure-tests", "success")]);

        // A plan with no evidence source receives no result, whatever GitHub says about the run.
        Assert.Equal("no-evidence-source", await ReasonAsync("O3", Evidence(s.GitHubHost, "passed")));
        Assert.Equal("not-run", (await StatusOfAsync("ORD-014/O3"))["result"]!.GetValue<string>());

        // A successful run of ANOTHER repository is not evidence for this obligation.
        actions.Run(7, ImportScenario.Commit1, repository: "acme/payments", jobs: [("failure-tests", "success")]);
        Assert.Equal("repository-differs", await ReasonAsync("O2", Evidence(s.GitHubHost, "passed", run: 7, repository: "acme/payments")));
        // GitHub answers the URL of acme/orders with a run that belongs to another repository.
        actions.Run(8, ImportScenario.Commit1, answersAs: "acme/payments", jobs: [("failure-tests", "success")]);
        Assert.Equal("run-repository-differs", await ReasonAsync("O2", Evidence(s.GitHubHost, "passed", run: 8)));
        // A successful run of another workflow of the same repository is not evidence either.
        actions.Run(9, ImportScenario.Commit1, path: ".github/workflows/deploy.yml", jobs: [("failure-tests", "success")]);
        Assert.Equal("workflow-differs", await ReasonAsync("O2", Evidence(s.GitHubHost, "passed", run: 9)));
        // The job must exist in the run, be completed and decide the result.
        actions.Run(10, ImportScenario.Commit1, jobs: [("build", "success")]);
        Assert.Equal("job-not-found", await ReasonAsync("O2", Evidence(s.GitHubHost, "passed", run: 10)));
        actions.Run(11, ImportScenario.Commit1, jobs: [("failure-tests", null)]);
        Assert.Equal("job-not-completed", await ReasonAsync("O2", Evidence(s.GitHubHost, "passed", run: 11)));
        actions.Run(12, ImportScenario.Commit1, jobs: [("failure-tests", "skipped")]);
        Assert.Equal("job-not-conclusive", await ReasonAsync("O2", Evidence(s.GitHubHost, "passed", run: 12)));
        actions.Run(13, ImportScenario.Commit1, conclusion: "failure", jobs: [("build", "failure"), ("failure-tests", "success")]);
        Assert.Equal("run-not-success", await ReasonAsync("O2", Evidence(s.GitHubHost, "passed", run: 13)));
        // The run, the commit and the host.
        Assert.Equal("run-not-found", await ReasonAsync("O2", Evidence(s.GitHubHost, "passed", run: 404)));
        actions.Run(14, ImportScenario.Commit1, conclusion: null, status: "in_progress");
        Assert.Equal("run-not-completed", await ReasonAsync("O2", Evidence(s.GitHubHost, "passed", run: 14)));
        actions.Run(15, ImportScenario.Commit2, jobs: [("failure-tests", "success")]);
        Assert.Equal("commit-differs", await ReasonAsync("O2", Evidence(s.GitHubHost, "passed", run: 15)));
        Assert.Equal("commit-differs", await ReasonAsync("O2", Evidence(s.GitHubHost, "passed", sha: Sha)));
        Assert.Equal("run-url-host-differs", await ReasonAsync("O2", Evidence("github.com", "passed")));
        Assert.Equal("system-not-checkable", await ReasonAsync("O2", Evidence(s.GitHubHost, "passed", system: "other")));
        // What the caller says must be what GitHub says.
        Assert.Equal("result-differs", await ReasonAsync("O2", Evidence(s.GitHubHost, "failed")));

        // Every refusal above is a claim: the plan is still not run, and the gap says why.
        var shown = await StatusOfAsync("ORD-014/O2");
        Assert.Equal(("not-run", "failed"), (shown["result"]!.GetValue<string>(), shown["evidence"]![0]!["claimedResult"]!.GetValue<string>()));
        Assert.Contains((await orders.GetAsync(id))["current"]!["evidenceStatus"]!["gaps"]!.AsArray(),
            g => g!.GetValue<string>() == "ORD-014/O2 has only a claim of 'failed' that Vellum could not check (result-differs). It counts as not run.");

        // The bound run, with no commit named: the commit is the one the model follows. Passed, with its scope and limits.
        var passed = await Ok(Post(s.Owner, Path("O2"), Evidence(s.GitHubHost, "passed")));
        Assert.Equal(("passed", $"github-actions@{ImportScenario.Commit1}", ImportScenario.Commit1), (
            passed["result"]!.GetValue<string>(), passed["environment"]!.GetValue<string>(), passed["commitSha"]!.GetValue<string>()));
        Assert.Equal(
            $"GitHub Actions run 42 of acme/orders, workflow '.github/workflows/ci.yml', job 'failure-tests', at commit {ImportScenario.Commit1} (the commit the model follows on 'main'), concluded 'success'.",
            passed["scope"]!.GetValue<string>());
        var source = passed["evidence"]![0]!["source"]!;
        Assert.Equal(("verified", "failure-tests", "acme/orders", ".github/workflows/ci.yml", 42L), (
            passed["evidence"]![0]!["identity"]!.GetValue<string>(), source["jobName"]!.GetValue<string>(),
            source["repository"]!.GetValue<string>(), source["workflow"]!.GetValue<string>(), source["runId"]!.GetValue<long>()));
        Assert.Equal(($"{s.GitHubHost}/acme/orders", ImportScenario.Commit1), (passed["inputs"]!["repository"]!.GetValue<string>(), passed["inputs"]!["commitSha"]!.GetValue<string>()));
        Assert.Contains(passed["limitations"]!.AsArray(), l => l!.GetValue<string>().StartsWith("Vellum did not run this test.", StringComparison.Ordinal));
        var current = await StatusOfAsync("ORD-014/O2");
        Assert.Equal(("passed", true), (current["result"]!.GetValue<string>(), current["current"]!.GetValue<bool>()));
        Assert.Contains(import.Host.Transport.For(s.GitHubHost).Requests, r => r.Uri.AbsolutePath == "/repos/acme/orders/actions/runs/42/jobs");

        // The model follows a later commit: the pass is for the old one. It is stale, and it is not counted as passed.
        Assert.Equal(1, await s.SqlAsync($"UPDATE import.tracked_refs SET commit_sha = {ImportScenario.Commit2} WHERE project_id = {s.ProjectId}"));
        var status = (await orders.GetAsync(id))["current"]!["evidenceStatus"]!;
        var stale = status["obligations"]!.AsArray().Single(o => o!["obligationId"]!.GetValue<string>() == "ORD-014/O2")!;
        Assert.Equal(("passed", false), (stale["result"]!.GetValue<string>(), stale["current"]!.GetValue<bool>()));
        Assert.Contains(status["gaps"]!.AsArray(),
            g => g!.GetValue<string>() == $"ORD-014/O2 passed (stale): evidence is for {ImportScenario.Commit1}; the model follows {ImportScenario.Commit2}.");
        Assert.Equal((0, 1), (Counts(status).Passed, Stale(status)));

        // A commit the caller names is shown as the scope. It is current only while the model follows it.
        var named = await Ok(Post(s.Owner, Path("O2"), Evidence(s.GitHubHost, "passed", sha: ImportScenario.Commit1)));
        Assert.Contains("(a commit the caller named)", named["scope"]!.GetValue<string>());
        Assert.False((await StatusOfAsync("ORD-014/O2"))["current"]!.GetValue<bool>());
        // On the commit the model follows now, a failed job that GitHub confirms is a failed obligation.
        actions.Run(16, ImportScenario.Commit2, conclusion: "failure", jobs: [("failure-tests", "failure")]);
        var failed = await Ok(Post(s.Owner, Path("O2"), Evidence(s.GitHubHost, "failed", run: 16)));
        Assert.Equal(("failed", "failure"), (failed["result"]!.GetValue<string>(), failed["evidence"]![0]!["source"]!["conclusion"]!.GetValue<string>()));
        Assert.Equal(("failed", true), ((await StatusOfAsync("ORD-014/O2"))["result"]!.GetValue<string>(), (await StatusOfAsync("ORD-014/O2"))["current"]!.GetValue<bool>()));

        // A claim that contradicts itself, and evidence for a check Vellum runs itself, are refused.
        var contradiction = Evidence(s.GitHubHost, "passed");
        contradiction["source"]!["conclusion"] = "failure";
        await Refused(Post(s.Owner, Path("O3"), contradiction), HttpStatusCode.BadRequest);
        await Refused(Post(s.Owner, Path("O3"), Evidence(s.GitHubHost, "skipped")), HttpStatusCode.BadRequest);
        var notHttps = Evidence(s.GitHubHost, "passed");
        notHttps["source"]!["runUrl"] = "http://example.test/acme/orders/actions/runs/42";
        await Refused(Post(s.Owner, Path("O3"), notHttps), HttpStatusCode.BadRequest);
        var deterministic = await Refused(Post(s.Owner, Path("O1"), Evidence(s.GitHubHost, "passed")), HttpStatusCode.Conflict);
        Assert.Equal("not_a_test_plan", deterministic["type"]!.GetValue<string>());

        Assert.Equal((0, 1, 3, 0), Counts((await orders.GetAsync(id))["current"]!["evidenceStatus"]!));
    }

    [Fact]
    public async Task No_response_holds_a_flag_that_says_a_promise_is_verified()
    {
        var (s, actions) = await WithGitHubAsync();
        var scan = await s.ScanAndAcceptAllAsync();
        var orders = await OrdersReference.SeedAsync(s.Owner, s.ProjectId);
        var id = (await orders.CreateOrd014Async())["id"]!.GetValue<Guid>();
        await Ok(BindAsync(orders, id, "O2"));
        await Ok(BindAsync(orders, id, "O3"));
        await orders.ApproveAsync(id);
        await orders.UseOutboxAsync();
        actions.Run(42, ImportScenario.Commit1);

        // Every obligation passes on current inputs. The answer is still counts, scopes and gaps.
        var responses = new List<JsonNode>
        {
            await orders.RunAsync(id, "O1"),
            await Ok(Post(s.Owner, Url(s.ProjectId, $"/{id}/obligations/O2/evidence"), Evidence(s.GitHubHost, "passed"))),
            await Ok(Post(s.Owner, Url(s.ProjectId, $"/{id}/obligations/O3/evidence"), Evidence(s.GitHubHost, "passed"))),
            await orders.RunAsync(id, "O4", new { scanId = scan.Id() }),
        };
        var promise = await orders.GetAsync(id);
        Assert.Equal((4, 0, 0, 0), Counts(promise["current"]!["evidenceStatus"]!));
        Assert.Equal(0, Stale(promise["current"]!["evidenceStatus"]!));
        responses.Add(promise);
        responses.Add((await s.Owner.GetFromJsonAsync<JsonObject>(Url(s.ProjectId)))!);
        responses.Add((await s.Owner.GetFromJsonAsync<JsonObject>(Url(s.ProjectId, $"/{id}/runs")))!);
        responses.Add((await s.Owner.GetFromJsonAsync<JsonObject>($"/api/projects/{s.ProjectId}/build/manifest"))!);

        var flags = new List<string>();
        void Walk(JsonNode? node, string path)
        {
            switch (node)
            {
                case JsonObject obj:
                    foreach (var (name, value) in obj)
                    {
                        var at = $"{path}.{name}";
                        var isFlag = value is JsonValue v && v.GetValueKind() is JsonValueKind.True or JsonValueKind.False;
                        if (name.Equals("verified", StringComparison.OrdinalIgnoreCase) || name.Equals("isVerified", StringComparison.OrdinalIgnoreCase)
                            || (isFlag && (name.Contains("verif", StringComparison.OrdinalIgnoreCase) || name.Contains("proven", StringComparison.OrdinalIgnoreCase))))
                            flags.Add(at);
                        Walk(value, at);
                    }
                    break;
                case JsonArray array:
                    foreach (var item in array)
                        Walk(item, path + "[]");
                    break;
            }
        }
        foreach (var response in responses)
            Walk(response, "$");
        Assert.Empty(flags);
    }

    [Fact]
    public async Task Promises_are_isolated_by_workspace_and_gated_by_role()
    {
        var (owner, workspaceId, projectId) = await NewProjectAsync();
        var orders = await OrdersReference.SeedAsync(owner, projectId);
        var id = (await orders.CreateOrd014Async())["id"]!.GetValue<Guid>();
        var viewer = await TestHost.SignInMemberAsync(import.Host.Factory, owner, workspaceId, "Viewer");

        // A second user in a second workspace.
        var (outsider, _, otherProject) = await NewProjectAsync();
        await SetPrefixAsync(outsider, otherProject);
        var theirs = (await Ok(Post(outsider, Url(otherProject), Body()), HttpStatusCode.Created))["id"]!.GetValue<Guid>();

        var body = orders.Ord014();
        (HttpMethod Method, string Url, JsonNode? Body)[] calls =
        [
            (HttpMethod.Get, Url(projectId), null),
            (HttpMethod.Get, Url(projectId, "/prefix"), null),
            (HttpMethod.Get, Url(projectId, $"/{id}"), null),
            (HttpMethod.Get, Url(projectId, $"/{id}/runs"), null),
            (HttpMethod.Post, Url(projectId), body),
            (HttpMethod.Put, Url(projectId, "/prefix"), new JsonObject { ["prefix"] = "ZZ" }),
            (HttpMethod.Put, Url(projectId, $"/{id}"), body),
            (HttpMethod.Post, Url(projectId, $"/{id}/revise"), null),
            (HttpMethod.Post, Url(projectId, $"/{id}/approve"), new JsonObject { ["revision"] = 1, ["contentHash"] = "x" }),
            (HttpMethod.Delete, Url(projectId, $"/{id}"), null),
            (HttpMethod.Post, Url(projectId, $"/{id}/obligations"), new JsonObject { ["kind"] = "x", ["title"] = "t", ["recipe"] = new JsonObject { ["text"] = "x" } }),
            (HttpMethod.Post, Url(projectId, $"/{id}/obligations/generate"), null),
            (HttpMethod.Put, Url(projectId, $"/{id}/obligations/O1"), new JsonObject { ["kind"] = "x", ["title"] = "t", ["recipe"] = new JsonObject { ["text"] = "x" } }),
            (HttpMethod.Delete, Url(projectId, $"/{id}/obligations/O1"), null),
            (HttpMethod.Post, Url(projectId, $"/{id}/obligations/O1/run"), null),
            (HttpMethod.Post, Url(projectId, $"/{id}/obligations/O2/evidence"), Evidence("github.com", "passed")),
        ];

        async Task<HttpStatusCode> SendAsync(HttpClient client, (HttpMethod Method, string Url, JsonNode? Body) call)
        {
            using var request = new HttpRequestMessage(call.Method, call.Url) { Content = call.Body is null ? null : OrdersReference.Json(call.Body) };
            return (await client.SendAsync(request)).StatusCode;
        }

        var anonymous = import.Host.Factory.CreateClient();
        foreach (var call in calls)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, await SendAsync(anonymous, call));
            Assert.Equal(HttpStatusCode.Forbidden, await SendAsync(outsider, call));
            // A viewer reads and writes nothing.
            Assert.Equal(call.Method == HttpMethod.Get ? HttpStatusCode.OK : HttpStatusCode.Forbidden, await SendAsync(viewer, call));
        }

        // A promise ID of another project is not found through this project, for a read and for every write.
        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync(Url(projectId, $"/{theirs}"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync(Url(projectId, $"/{theirs}/runs"))).StatusCode);
        await Refused(owner.PutAsync(Url(projectId, $"/{theirs}"), OrdersReference.Json(body)), HttpStatusCode.NotFound);
        await Refused(owner.PostAsJsonAsync(Url(projectId, $"/{theirs}/approve"), new { revision = 1, contentHash = "x" }), HttpStatusCode.NotFound);
        await Refused(owner.DeleteAsync(Url(projectId, $"/{theirs}")), HttpStatusCode.NotFound);
        await Refused(Post(owner, Url(projectId, $"/{theirs}/obligations/generate")), HttpStatusCode.NotFound);
        await Refused(Post(owner, Url(projectId, $"/{theirs}/obligations/O1/run")), HttpStatusCode.NotFound);
        // An ID that names another kind of stream (the scenario) is not a promise.
        await Refused(owner.PutAsync(Url(projectId, $"/{orders.ScenarioId}"), OrdersReference.Json(body)), HttpStatusCode.NotFound);

        // Nothing above changed the promise or the other project.
        Assert.Equal(("draft", 1), ((await orders.GetAsync(id))["state"]!.GetValue<string>(), (await orders.GetAsync(id))["latestRevision"]!.GetValue<int>()));
        Assert.Single(await AllAsync(outsider, Url(otherProject)));
        Assert.Single(await AllAsync(owner, Url(projectId)));

        // Text and size rules.
        var control = orders.Ord014();
        control["title"] = "bad\u0000title";
        Assert.Equal("title", (await Refused(Post(owner, Url(projectId), control), HttpStatusCode.BadRequest))["errors"]![0]!["field"]!.GetValue<string>());
        var tooLong = orders.Ord014();
        tooLong["claim"] = new string('x', 4001);
        Assert.Equal("claim", (await Refused(Post(owner, Url(projectId), tooLong), HttpStatusCode.BadRequest))["errors"]![0]!["field"]!.GetValue<string>());
        var huge = orders.Ord014();
        huge["scope"] = new string('x', 70_000);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await Post(owner, Url(projectId), huge)).StatusCode);
        await Refused(owner.PostAsync(Url(projectId), new StringContent("{ not json", System.Text.Encoding.UTF8, "application/json")), HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_rebuild_gives_the_same_promises_and_runs_and_keeps_the_numbering()
    {
        var (owner, _, projectId) = await NewProjectAsync();
        var orders = await OrdersReference.SeedAsync(owner, projectId);
        var id = (await orders.CreateOrd014Async())["id"]!.GetValue<Guid>();
        await orders.ApproveAsync(id);
        await orders.RunAsync(id, "O1");
        await orders.RunAsync(id, "O4");
        await Ok(Post(owner, Url(projectId, $"/{id}/revise")));
        var deleted = (await Ok(Post(owner, Url(projectId), Body("Deleted later")), HttpStatusCode.Created))["id"]!.GetValue<Guid>();
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync(Url(projectId, $"/{deleted}"))).StatusCode);

        string[] reads = [Url(projectId), Url(projectId, $"/{id}"), Url(projectId, $"/{id}/runs"), Url(projectId, "/prefix")];
        async Task<string[]> ReadAllAsync() => await Task.WhenAll(reads.Select(owner.GetStringAsync));
        var before = await ReadAllAsync();
        Assert.Contains("ORD-014", before[0]);
        Assert.DoesNotContain("Deleted later", before[0]);

        var admin = import.Host.Factory.Services.GetRequiredService<IEventStoreAdmin>();
        var jobId = await admin.RebuildAsync("promise_read_model");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        while (true)
        {
            var job = await admin.GetJobAsync(jobId, timeout.Token);
            Assert.NotEqual(JobState.Failed, job!.Status);
            if (job.Status == JobState.Done
                && (await admin.GetStatusAsync(timeout.Token)).Consumers.Single(c => c.Name == "promise_read_model").Status == ConsumerState.Running)
                break;
            await Task.Delay(100, timeout.Token);
        }

        Assert.Equal(before, await ReadAllAsync());
        // The allocator is not a projection: the next number is still the next number.
        Assert.Equal("ORD-016", (await Ok(Post(owner, Url(projectId), Body()), HttpStatusCode.Created))["requirementId"]!.GetValue<string>());
    }
}
