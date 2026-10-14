using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Vellum.Modules.Identity.AgentTokens;
using Vellum.Tests.Modules.Promises;

namespace Vellum.Tests.Modules.Mcp;

/// <summary>
/// The tools of SDD §6 over the review and over proposals, through the real app with real
/// agent tokens: what a token reads, what it proposes, that a proposal changes nothing until
/// a person decides, and that a token never decides.
/// </summary>
public class McpProposalToolTests(IntegrationFixture fixture) : IClassFixture<IntegrationFixture>
{
    private static string? ErrorType(string text) => JsonNode.Parse(text)!["error"]?["type"]?.GetValue<string>();

    private static async Task<JsonNode> DataAsync(McpWire mcp, string tool, JsonObject arguments)
    {
        var (isError, text) = await mcp.ToolAsync(tool, arguments);
        Assert.False(isError, $"{tool}: {text}");
        return JsonNode.Parse(text)!["data"]!;
    }

    private static async Task<JsonObject> Json(Task<HttpResponseMessage> request, HttpStatusCode expected)
    {
        var response = await request;
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode}: {text}");
        return JsonNode.Parse(text)!.AsObject();
    }

    [Fact]
    public async Task The_review_tools_read_findings_decisions_questions_and_runs_and_propose_without_deciding()
    {
        var factory = fixture.Factory;
        var owner = await TestHost.SignInAsync(factory);
        var (workspaceId, projectId) = await TestHost.CreateProjectAsync(owner);
        var orders = await OrdersReference.SeedAsync(owner, projectId);
        var promiseId = (await orders.CreateOrd014Async(requiresPattern: false))["id"]!.GetValue<Guid>();
        await orders.ApproveAsync(promiseId);
        await orders.RunAsync(promiseId, "O1");
        var agent = await TestHost.CreateAgentAsync(factory, owner, workspaceId, ["read", "propose"]);
        var mcp = new McpWire(agent.Client);
        JsonObject Project(params (string Name, JsonNode? Value)[] more)
        {
            var arguments = new JsonObject { ["projectId"] = projectId };
            foreach (var (name, value) in more)
                arguments[name] = value;
            return arguments;
        }

        // The deterministic review: the same findings and the same hash as the endpoint gives a person.
        var over = (await owner.GetFromJsonAsync<JsonObject>($"/api/projects/{projectId}/review/findings"))!;
        var review = await DataAsync(mcp, "get_review_findings", Project(("limit", 200)));
        Assert.NotEmpty(review["findings"]!.AsArray());
        Assert.Equal(over["findings"]!.AsArray().Count, review["total"]!.GetValue<int>());
        Assert.Equal(over["hash"]!.GetValue<string>(), review["hash"]!.GetValue<string>());
        Assert.Equal(over["counts"]!.ToJsonString(), review["counts"]!.ToJsonString());
        var concerns = await DataAsync(mcp, "get_review_findings", Project(("result", "concern"), ("limit", 200)));
        Assert.All(concerns["findings"]!.AsArray(), f => Assert.Equal("concern", f!["finding"]!["result"]!.GetValue<string>()));
        Assert.Equal(over["counts"]!["concerns"]!.GetValue<int>(), concerns["total"]!.GetValue<int>());
        Assert.Equal("validation_error", ErrorType((await mcp.ToolAsync("get_review_findings", Project(("result", "fine")))).Text));

        // The findings in pages by position, with no finding lost or twice.
        var seen = new List<string>();
        string? cursor = null;
        do
        {
            var (isError, text) = await mcp.ToolAsync("get_review_findings", Project(("limit", 3), ("cursor", cursor)));
            Assert.False(isError, text);
            var page = JsonNode.Parse(text)!;
            seen.AddRange(page["data"]!["findings"]!.AsArray().Select(f => f!["finding"]!["fingerprint"]!.GetValue<string>()));
            cursor = page["nextCursor"]?.GetValue<string>();
        }
        while (cursor is not null);
        Assert.Equal(over["findings"]!.AsArray().Select(f => f!["finding"]!["fingerprint"]!.GetValue<string>()), seen);

        // A decision: proposed, agent-suggested whatever the arguments say, and it settles nothing.
        var finding = over["findings"]!.AsArray().First(f => f!["finding"]!["result"]!.GetValue<string>() == "concern")!["finding"]!;
        var proposed = await DataAsync(mcp, "propose_decision", Project(
            ("title", "Publish after the answer"), ("decision", "We accept the loss of an event during an outage."),
            ("origin", "human"), ("status", "accepted"),
            ("settles", new JsonArray(new JsonObject { ["ruleId"] = finding["ruleId"]!.GetValue<string>(), ["subjectId"] = finding["subject"]!["id"]!.GetValue<Guid>() }))));
        var decisionId = proposed["decisionId"]!.GetValue<Guid>();
        Assert.Equal(("proposed", "agent-suggested"), (proposed["status"]!.GetValue<string>(), proposed["origin"]!.GetValue<string>()));
        Assert.Contains("workspace owner must accept", proposed["approval"]!.GetValue<string>());
        var listed = Assert.Single((await DataAsync(mcp, "list_decisions", Project()))["decisions"]!.AsArray())!;
        Assert.Equal((decisionId, "proposed"), (listed["id"]!.GetValue<Guid>(), listed["status"]!.GetValue<string>()));
        // The review is as it was: a proposed decision settles no finding.
        Assert.Equal(over["counts"]!.ToJsonString(), (await DataAsync(mcp, "get_review_findings", Project(("limit", 1))))["counts"]!.ToJsonString());
        // The token does not accept, over HTTP either.
        Assert.Equal(AgentBoundary.HumanOnlyError,
            (await Json(agent.Client.PostAsync($"/api/projects/{projectId}/decisions/{decisionId}/accept", null), HttpStatusCode.Forbidden))["type"]!.GetValue<string>());

        // A question: open, agent-suggested, and only a person answers it.
        var asked = await DataAsync(mcp, "propose_question", Project(("text", "Who replays a lost event?"), ("about", new JsonArray(OrdersReference.OrdersApi)), ("origin", "human")));
        Assert.Equal(("open", "agent-suggested"), (asked["status"]!.GetValue<string>(), asked["origin"]!.GetValue<string>()));
        var question = Assert.Single((await DataAsync(mcp, "list_questions", Project()))["questions"]!.AsArray())!;
        Assert.Equal(("Who replays a lost event?", "open"), (question["text"]!.GetValue<string>(), question["status"]!.GetValue<string>()));
        Assert.Equal(AgentBoundary.HumanOnlyError,
            (await Json(agent.Client.PostAsJsonAsync($"/api/projects/{projectId}/questions/{asked["questionId"]!.GetValue<Guid>()}/answer", new { answer = "Nobody." }), HttpStatusCode.Forbidden))["type"]!.GetValue<string>());

        // The runs of a promise, the newest first, and the runs of one obligation.
        var runs = (await DataAsync(mcp, "list_promise_runs", Project(("promiseId", promiseId))))["items"]!.AsArray();
        Assert.Equal("ORD-014/O1", Assert.Single(runs)!["obligationId"]!.GetValue<string>());
        Assert.Empty((await DataAsync(mcp, "list_promise_runs", Project(("promiseId", promiseId), ("obligation", "O9"))))["items"]!.AsArray());
        Assert.Equal("not_found", ErrorType((await mcp.ToolAsync("list_promise_runs", Project(("promiseId", Guid.NewGuid())))).Text));
    }

    [Fact]
    public async Task A_proposed_architecture_and_a_proposed_flow_change_nothing_until_a_person_accepts()
    {
        var factory = fixture.Factory;
        var owner = await TestHost.SignInAsync(factory);
        var (workspaceId, projectId) = await TestHost.CreateProjectAsync(owner);
        var orders = await OrdersReference.SeedAsync(owner, projectId);
        var agent = await TestHost.CreateAgentAsync(factory, owner, workspaceId, ["read", "propose"]);
        var mcp = new McpWire(agent.Client);
        var origin = $"agent-token:{agent.TokenId}";
        async Task<string> ModelAsync() =>
            (await DataAsync(mcp, "get_model_summary", new JsonObject { ["projectId"] = projectId })).ToJsonString()
            + (await DataAsync(mcp, "list_scenarios", new JsonObject { ["projectId"] = projectId })).ToJsonString();
        var before = await ModelAsync();
        var invented = Guid.NewGuid();

        // Additions: two that resolve, one that references an ID that names nothing, one that removes.
        var additions = await DataAsync(mcp, "propose_architecture", new JsonObject
        {
            ["projectId"] = projectId,
            ["request"] = "Add a service that tells the customer about the order.",
            ["proposal"] = new JsonObject
            {
                ["summary"] = "A notifications service on order-events",
                ["reasoningSummary"] = "The customer gets no message today.",
                ["openQuestions"] = new JsonArray("Which channel reaches the customer?"),
                ["items"] = new JsonArray(
                    new JsonObject
                    {
                        ["alias"] = "N1", ["entityKind"] = "element", ["name"] = "Notifications", ["elementKind"] = "app",
                        ["parent"] = OrdersReference.Shop, ["confidence"] = 0.8, ["rationale"] = "It sends the message.",
                    },
                    new JsonObject { ["alias"] = "N2", ["action"] = "add", ["entityKind"] = "subscription", ["channel"] = OrdersReference.OrderEvents, ["consumer"] = "N1", ["confidence"] = 0.7 },
                    new JsonObject { ["alias"] = "N3", ["entityKind"] = "relationship", ["from"] = "N1", ["to"] = invented, ["label"] = "calls" },
                    new JsonObject { ["alias"] = "N4", ["action"] = "remove", ["target"] = OrdersReference.Billing, ["entityKind"] = "element", ["name"] = "Billing" }),
            },
        });
        var architectureId = additions["proposalId"]!.GetValue<Guid>();
        Assert.Equal(("draft", origin), (additions["status"]!.GetValue<string>(), additions["origin"]!.GetValue<string>()));
        Assert.Contains("no agent token accepts", additions["approval"]!.GetValue<string>());
        Assert.Equal(["N1", "N2"], additions["items"]!.AsArray().Select(i => i!["alias"]!.GetValue<string>()).Order());
        var rejected = additions["rejectedReferences"]!.AsArray();
        Assert.Contains(rejected, r => r!["where"]!.GetValue<string>() == "item:N3.to" && r["alias"]!.GetValue<string>() == invented.ToString() && r["reason"]!.GetValue<string>().Contains("not the ID"));
        Assert.Contains(rejected, r => r!["where"]!.GetValue<string>() == "item:N4.action");

        // A flow: one step that resolves, and one that calls an operation that does not exist.
        var flow = await DataAsync(mcp, "propose_flow", new JsonObject
        {
            ["projectId"] = projectId,
            ["request"] = "Show an order with one apple.",
            ["flow"] = new JsonObject
            {
                ["name"] = "Place one apple",
                ["summary"] = "The customer places an order.",
                ["steps"] = new JsonArray(
                    new JsonObject
                    {
                        ["id"] = "place", ["type"] = "call", ["slot"] = "root", ["order"] = 1,
                        ["from"] = OrdersReference.Customer, ["operation"] = OrdersReference.PlaceOrder, ["requestJson"] = """{"sku":"apple","qty":1}""",
                    },
                    new JsonObject { ["id"] = "ghost", ["type"] = "call", ["slot"] = "root", ["order"] = 2, ["from"] = OrdersReference.Customer, ["operation"] = invented }),
                ["assertions"] = new JsonArray(new JsonObject { ["id"] = "accepted", ["type"] = "outcome", ["step"] = "place", ["expected"] = "succeeded" }),
            },
        });
        var flowId = flow["proposalId"]!.GetValue<Guid>();
        Assert.Equal(("draft", origin), (flow["status"]!.GetValue<string>(), flow["origin"]!.GetValue<string>()));
        Assert.Equal("place", Assert.Single(flow["scenario"]!["steps"]!.AsArray())!["id"]!.GetValue<string>());
        var dropped = Assert.Single(flow["rejectedReferences"]!.AsArray())!;
        Assert.Equal(("step:ghost.operation", invented.ToString()), (dropped["where"]!.GetValue<string>(), dropped["alias"]!.GetValue<string>()));
        Assert.Contains("not the ID", dropped["reason"]!.GetValue<string>());
        Assert.True(flow["preview"]!["eventCount"]!.GetValue<int>() > 0);

        // Nothing changed: the model and the scenarios are what they were.
        Assert.Equal(before, await ModelAsync());

        // The token does not decide: not over HTTP, with every scope the test gave it.
        foreach (var url in new[]
        {
            $"/api/projects/{projectId}/ai/architecture-proposals/{architectureId}/decide",
            $"/api/projects/{projectId}/ai/flow-proposals/{flowId}/accept",
        })
            Assert.Equal(AgentBoundary.HumanOnlyError, (await Json(agent.Client.PostAsJsonAsync(url, new { }), HttpStatusCode.Forbidden))["type"]!.GetValue<string>());
        Assert.Equal(before, await ModelAsync());

        // A person finds both among the submitted drafts, with the token as the origin.
        foreach (var (kind, id) in new[] { ("architecture", architectureId), ("flow", flowId) })
        {
            var pending = (await owner.GetFromJsonAsync<JsonObject>($"/api/projects/{projectId}/ai/{kind}-proposals?status=draft&submitted=true"))!["items"]!.AsArray();
            var summary = Assert.Single(pending)!;
            Assert.Equal((id, origin), (summary["id"]!.GetValue<Guid>(), summary["origin"]!.GetValue<string>()));
            Assert.Empty((await owner.GetFromJsonAsync<JsonObject>($"/api/projects/{projectId}/ai/{kind}-proposals?submitted=false"))!["items"]!.AsArray());
        }

        // The person accepts the additions: they are in the model as inferred facts that name the token and the proposal.
        var read = (await owner.GetFromJsonAsync<JsonObject>($"/api/projects/{projectId}/ai/architecture-proposals/{architectureId}"))!;
        Assert.Equal((origin, "agent-token"), (read["origin"]!.GetValue<string>(), read["provider"]!["kind"]!.GetValue<string>()));
        var itemIds = new JsonArray([.. read["items"]!.AsArray().Select(i => (JsonNode)i!["id"]!.GetValue<Guid>())]);
        var decided = await Json(owner.PostAsJsonAsync($"/api/projects/{projectId}/ai/architecture-proposals/{architectureId}/decide", new { accept = itemIds }), HttpStatusCode.OK);
        Assert.Equal("decided", decided["status"]!.GetValue<string>());
        var added = (await owner.GetFromJsonAsync<JsonObject>($"/api/projects/{projectId}/elements?limit=200"))!["items"]!.AsArray()
            .Single(e => e!["name"]!.GetValue<string>() == "Notifications")!;
        Assert.Equal("inferred", added["factStatus"]!.GetValue<string>());
        Assert.Equal($"{origin}#architecture-proposal:{architectureId}", added["provenance"]!["source"]!.GetValue<string>());

        // The person accepts the flow: the scenario says that it came from a submitted proposal of the token.
        var accepted = await Json(owner.PostAsJsonAsync($"/api/projects/{projectId}/ai/flow-proposals/{flowId}/accept", new { acceptWithIssues = true }), HttpStatusCode.OK);
        var scenario = (await owner.GetFromJsonAsync<JsonObject>($"/api/projects/{projectId}/scenarios/{accepted["scenarioId"]!.GetValue<Guid>()}"))!;
        Assert.Equal(
            ("submitted-proposal", flowId, "agent-token", agent.TokenId.ToString()),
            (scenario["origin"]!["kind"]!.GetValue<string>(), scenario["origin"]!["proposalId"]!.GetValue<Guid>(),
                scenario["origin"]!["provider"]!.GetValue<string>(), scenario["origin"]!["model"]!.GetValue<string>()));
    }
}
