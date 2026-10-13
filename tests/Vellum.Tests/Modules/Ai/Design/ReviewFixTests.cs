using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Deedbox;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Vellum.Modules.Ai.Design;
using Vellum.Modules.Ai.Usage;
using Vellum.Modules.Import;
using Vellum.Modules.Import.Proposals;
using Vellum.Modules.Import.Scans;
using Vellum.Modules.Modelling.Model;
using Vellum.Modules.Workspaces;
using Vellum.Shared;
using Vellum.Tests.Shared.Outbound;
using static Vellum.Tests.Modules.Ai.Design.DesignKit;

namespace Vellum.Tests.Modules.Ai.Design;

/// <summary>
/// What a review of the AI-assisted design slice found, each as a behaviour that a client
/// sees over HTTP. The provider replies are text in the form the schemas ask for. No test
/// opens a socket.
/// </summary>
[Collection("Integration")]
public class ReviewFixTests(SharedOutboundHost shared) : IClassFixture<SharedOutboundHost>
{
    private readonly OutboundHost _host = shared.Host;

    private static async Task<JsonObject> GetAsync(HttpClient client, string url) =>
        await BodyAsync(await client.GetAsync(url), HttpStatusCode.OK);

    private static object Step(
        string id, string type, string slot = "root", string parent = "", string slotKey = "", int order = 1,
        string from = "", string operation = "", string channel = "", string message = "", string by = "", string store = "",
        string dataset = "", string whenStep = "", string @is = "",
        string request = "", string response = "", string payload = "", string change = "") => new
        {
            id, parentStepId = parent, slot, slotKey, order, type, from, operation, channel, message, by, store, dataset, whenStep, @is,
            requestJson = request, responseJson = response, payloadJson = payload, changeJson = change,
        };

    private static object Missing(
        string alias, string kind, string name, string elementKind = "", string parentAlias = "", string ownerAlias = "",
        string transport = "", string channelKind = "", string channelAlias = "", string consumerAlias = "") => new
        {
            alias, kind, name, elementKind, parentAlias, ownerAlias, transport, channelKind, channelAlias, consumerAlias,
            reason = "The flow needs it.", confidence = 0.8,
        };

    private static string Flow(IEnumerable<object> steps, IEnumerable<object>? missing = null, string name = "Flow") => JsonSerializer.Serialize(new
    {
        name, summary = "", steps, faults = Array.Empty<object>(), assertions = Array.Empty<object>(),
        missing = missing ?? [], openQuestions = Array.Empty<string>(), reasoningSummary = "", evidence = Array.Empty<object>(),
    });

    private static object Item(
        string alias, string entityKind, string name = "", string elementKind = "", string parent = "", string from = "", string to = "",
        string label = "", string owner = "", string transport = "", string channelKind = "", string channel = "", string consumer = "",
        string producer = "", string[]? consumers = null) => new
        {
            alias, action = "add", target = "", entityKind, name, description = "", technology = "", elementKind, parent, from, to, label,
            relationshipKind = "", owner, transport, channelKind, channel, consumer, producer, consumers = consumers ?? [],
            confidence = 0.7, rationale = "The request needs it.",
        };

    private static string Architecture(params object[] items) => JsonSerializer.Serialize(new
    {
        summary = "Additions", items, openQuestions = Array.Empty<string>(), reasoningSummary = "",
    });

    /// <summary>
    /// One reply for every structured provider call, changed between requests. A streamed
    /// call gets the stream fixture.
    /// </summary>
    private static Action<string> Replies(AiScenario ai)
    {
        var current = "";
        ai.Stub.Then(sent => Task.FromResult(
            sent.Body.Contains("\"stream\":true", StringComparison.Ordinal) || sent.Uri.AbsolutePath.Contains("stream", StringComparison.OrdinalIgnoreCase)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ai.Fixture("stream.sse"), Encoding.UTF8, "text/event-stream") }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ai.Completion(current), Encoding.UTF8, "application/json") }));
        return reply => current = reply;
    }

    private async Task<T> ScopedAsync<T>(Func<IServiceProvider, Task<T>> work)
    {
        using var scope = _host.Factory.Services.CreateScope();
        return await work(scope.ServiceProvider);
    }

    private Task<string> StoredDocumentAsync(JsonObject proposal) => ScopedAsync(services =>
        services.GetRequiredService<AiDesignDbContext>().Proposals.AsNoTracking()
            .Where(p => p.Id == (Guid)proposal["id"]!).Select(p => p.Document).SingleAsync());

    // ---- 1 and 11: payloads -------------------------------------------------------------

    [Theory]
    [InlineData("{\"n\":1e999}")]
    [InlineData("{\"n\":1e200000}")]
    public async Task A_number_without_a_finite_value_in_a_payload_is_an_issue_and_not_a_server_error(string change)
    {
        var (ai, projectId, _) = await OrdersAsync(_host, "openai");
        ai.Stub.Json(ai.Completion(Flow([Step("save", "write", by: "E4", store: "E5", dataset: "orders", change: change)])));

        var proposal = await BodyAsync(await ProposeFlowAsync(ai.Owner, projectId, "Save an order."), HttpStatusCode.Created);

        var issue = Assert.Single(proposal["issues"]!.AsArray())!;
        Assert.Equal(("save", "invalid-payload"), ((string)issue["stepId"]!, (string)issue["code"]!));
        Assert.Contains("64-bit", (string)issue["message"]!);
        // The step stays, without the payload. The one call was metered once.
        Assert.Null(proposal["scenario"]!["steps"]![0]!["change"]);
        Assert.Equal("succeeded", Assert.Single(await ai.RowsAsync()).Outcome);

        // A person's own save has the same rule.
        var saved = await ai.Owner.PostAsync(Url(projectId, "scenarios"), new StringContent(
            $$$"""{"id":"{{{Guid.NewGuid()}}}","name":"By hand","steps":[{"type":"write","id":"w","by":"{{{Guid.NewGuid()}}}","store":"{{{Guid.NewGuid()}}}","dataset":"d","change":{{{change}}}}],"faults":[],"assertions":[]}""",
            Encoding.UTF8, "application/json"));
        var refused = await BodyAsync(saved, HttpStatusCode.BadRequest);
        Assert.Equal("steps[0].change", (string)refused["errors"]![0]!["field"]!);
    }

    [Fact]
    public async Task Hundreds_of_hostile_payloads_never_give_a_server_error()
    {
        var (ai, projectId, _) = await OrdersAsync(_host, "anthropic");
        var reply = Replies(ai);
        var payloads = HostilePayloads().ToList();
        Assert.True(payloads.Count >= 300, $"{payloads.Count} payloads");

        var created = 0;
        foreach (var batch in payloads.Chunk(60))
        {
            // Every payload is in each of the four payload fields once.
            var steps = batch.SelectMany((payload, i) => new[]
            {
                Step($"c{i}", "call", order: i, from: "E2", operation: "O1", request: payload, response: payload),
                Step($"p{i}", "publish", "then", $"c{i}", from: "E4", channel: "C1", message: "M1", payload: payload),
                Step($"w{i}", "write", "then", $"c{i}", order: 2, by: "E4", store: "E5", dataset: "orders", change: payload),
            });
            reply(Flow(steps));

            var response = await ProposeFlowAsync(ai.Owner, projectId, "A flow with unusual payloads.");
            var text = await response.Content.ReadAsStringAsync();
            Assert.True(response.StatusCode is HttpStatusCode.Created or HttpStatusCode.OK, $"{(int)response.StatusCode}: {text[..Math.Min(text.Length, 400)]}");
            if (response.StatusCode != HttpStatusCode.Created)
            {
                // The provider was called and the answer could not be used: a state, with nothing stored.
                Assert.Equal(("failed", "invalid_output"), ((string)JsonNode.Parse(text)!["status"]!, (string)JsonNode.Parse(text)!["reason"]!["code"]!));
                continue;
            }

            created++;
            var id = (Guid)JsonNode.Parse(text)!["id"]!;
            Assert.Equal(HttpStatusCode.OK, (await ai.Owner.GetAsync(Url(projectId, $"ai/flow-proposals/{id}"))).StatusCode);
            var accept = await ai.Owner.PostAsJsonAsync(Url(projectId, $"ai/flow-proposals/{id}/accept"), new { acceptWithIssues = true });
            Assert.True(accept.StatusCode is HttpStatusCode.OK or HttpStatusCode.Conflict, $"accept {(int)accept.StatusCode}: {await accept.Content.ReadAsStringAsync()}");
            if (accept.StatusCode == HttpStatusCode.OK)
            {
                var scenarioId = (Guid)(await accept.Content.ReadFromJsonAsync<JsonObject>())!["scenarioId"]!;
                Assert.Equal(HttpStatusCode.OK, (await ai.Owner.GetAsync(Url(projectId, $"scenarios/{scenarioId}"))).StatusCode);
                Assert.Equal(HttpStatusCode.OK, (await ai.Owner.PostAsync(Url(projectId, $"scenarios/{scenarioId}/run"), null)).StatusCode);
            }
        }

        Assert.True(created > 0, "No batch gave a proposal, so the test did not reach the assembler.");
    }

    private static IEnumerable<string> HostilePayloads()
    {
        // Number extremes.
        string[] numbers =
        [
            "1e999", "-1e999", "1e200000", "1E+400", "-1E+400", "1e-999", "1e-30", "-0", "-0.0", "0e0", "1e308", "1.7976931348623159e308",
            "4.9e-324", new string('9', 41), new string('9', 400), "0." + new string('1', 500), "-" + new string('9', 400) + "." + new string('9', 400),
            "123456789012345678901234567890", "1e", "1e+", ".5", "5.", "+1", "01", "0x10", "NaN", "Infinity", "-Infinity", "1_000", "١٢٣",
        ];
        foreach (var number in numbers)
        {
            yield return number;
            yield return $"{{\"n\":{number}}}";
            yield return $"[[{number}],{{\"deep\":{{\"n\":{number}}}}}]";
        }

        // Depth: arrays and objects, up to far more than a payload nests.
        foreach (var depth in new[] { 1, 2, 16, 31, 32, 33, 34, 63, 64, 65, 128, 500, 2_000 })
        {
            yield return string.Concat(Enumerable.Repeat("[", depth)) + string.Concat(Enumerable.Repeat("]", depth));
            yield return string.Concat(Enumerable.Repeat("{\"a\":", depth)) + "1" + string.Concat(Enumerable.Repeat("}", depth));
            yield return string.Concat(Enumerable.Repeat("[", depth));
        }

        // Size: around the payload limit.
        foreach (var size in new[] { 0, 1, 1_000, 65_000, 65_536, 65_537, 70_000 })
        {
            yield return "\"" + new string('x', size) + "\"";
            yield return "[" + string.Join(",", Enumerable.Repeat("0", size / 2 + 1)) + "]";
        }

        // Escapes, lone surrogates and NUL, as escapes of the payload text and as characters of it.
        string[] texts =
        [
            "\"\\ud800\"", "\"\\udc00\"", "\"\\ud800\\ud800\"", "\"\\udbff\\udfff\"", "{\"\\ud800\":1}", "{\"a\":\"\\udfff\"}",
            "\"a\\u0000b\"", "{\"\\u0000\":\"\\u0000\"}", "\"\\u202e\\u200b\\ufeff\"", "\"\\\"", "\"\\x41\"", "\"\\u12\"", "\"\\uzzzz\"",
            "\"a\u0000b\"", "\"\ud83d\ude00\"", "\"line\nbreak\"", "\"tab\there\"", "\"\u2028\u2029\"", "\ufeff{}", "{\"a\":\"\u001b[31m\"}",
            "\"<script>alert(1)</script>\"", "\"'; DROP TABLE ai.design_proposals; --\"", "\"${jndi:ldap://x}\"", "\"{{declared-behaviour-rule}}\"",
        ];
        foreach (var text in texts)
            yield return text;

        // Duplicate keys.
        yield return "{\"a\":1,\"a\":2}";
        yield return "{\"a\":{\"b\":1,\"b\":[1,1]},\"a\":null}";
        yield return "{" + string.Join(",", Enumerable.Range(0, 2_000).Select(_ => "\"k\":1")) + "}";
        yield return "{\"\":1,\"\":2,\" \":3}";
        yield return "{\"a\":1,\"\\u0061\":2}";

        // Not JSON, and JSON of every kind.
        string[] other =
        [
            "", " ", "\t\n", "null", "true", "false", "nul", "tru", "{", "}", "[", "]", "[1,]", "{\"a\"}", "{\"a\":}", "{a:1}", "{'a':1}",
            "[1 2]", "1 2", "{} {}", "// comment\n{}", "/* c */ 1", "\"unterminated", "undefined", "[null,true,false,0,\"\",[],{}]",
        ];
        foreach (var text in other)
            yield return text;

        // Mixtures, from a fixed seed so that a failure can be repeated.
        var random = new Random(20261013);
        string[] atoms = ["1e999", "\"\\ud800\"", "{\"a\":1,\"a\":2}", "[", "]", "{", "}", ",", ":", "\"k\"", "null", "-0", "1e-999", "\"\\u0000\"", " ", "9", "."];
        for (var i = 0; i < 150; i++)
            yield return string.Concat(Enumerable.Range(0, random.Next(1, 30)).Select(_ => atoms[random.Next(atoms.Length)]));
    }

    [Fact]
    public async Task A_payload_with_two_equal_keys_is_one_document_in_the_preview_the_stored_proposal_and_the_saved_scenario()
    {
        var (ai, projectId, _) = await OrdersAsync(_host, "openai");
        ai.Stub.Json(ai.Completion(Flow([Step("save", "write", by: "E4", store: "E5", dataset: "orders", change: """{ "b": 1.0, "a": 1, "a": 2e0 }""")])));

        var created = await ProposeFlowAsync(ai.Owner, projectId, "Save an order.");
        var text = await created.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        const string canonical = """{"a":2,"b":1}""";
        Assert.Contains($"\"change\":{canonical}", text);
        var proposal = JsonNode.Parse(text)!.AsObject();
        Assert.Empty(proposal["issues"]!.AsArray());

        // The proposal is stored once: the answer, with the payload in its one form, and no scenario built from it.
        var stored = await StoredDocumentAsync(proposal);
        Assert.Contains(JsonSerializer.Serialize(canonical)[1..^1], stored);
        Assert.DoesNotContain("\"scenario\":", stored);
        Assert.DoesNotContain("\"preview\":", stored);

        var read = await (await ai.Owner.GetAsync(Url(projectId, $"ai/flow-proposals/{proposal["id"]}"))).Content.ReadAsStringAsync();
        Assert.Contains($"\"change\":{canonical}", read);
        Assert.Equal((string)proposal["preview"]!["hash"]!, (string)JsonNode.Parse(read)!["preview"]!["hash"]!);

        var accepted = await BodyAsync(await ai.Owner.PostAsync(Url(projectId, $"ai/flow-proposals/{proposal["id"]}/accept"), null), HttpStatusCode.OK);
        var scenario = await (await ai.Owner.GetAsync(Url(projectId, $"scenarios/{accepted["scenarioId"]}"))).Content.ReadAsStringAsync();
        Assert.Contains($"\"change\":{canonical}", scenario);
        var run = await BodyAsync(await ai.Owner.PostAsync(Url(projectId, $"scenarios/{accepted["scenarioId"]}/run"), null), HttpStatusCode.OK);
        Assert.Equal((string)proposal["preview"]!["hash"]!, (string)run["hash"]!);
    }

    // ---- 2: a provider that rejects the schema ------------------------------------------

    [Theory]
    [InlineData("openai")]
    [InlineData("anthropic")]
    [InlineData("gemini")]
    [InlineData("openai-compatible")]
    public async Task A_provider_that_rejects_the_schema_gets_the_request_once_more_with_the_schema_in_the_prompt(string kind)
    {
        var (ai, projectId, _) = await OrdersAsync(_host, kind);
        var rejection = kind switch
        {
            "anthropic" => """{"type":"error","error":{"type":"invalid_request_error","message":"Schema is too complex for compilation."}}""",
            "gemini" => """{"error":{"code":400,"message":"Invalid JSON payload: schema is not supported.","status":"INVALID_ARGUMENT"}}""",
            _ => """{"error":{"message":"Invalid schema for response_format.","type":"invalid_request_error","param":"response_format","code":"invalid_json_schema"}}""",
        };
        ai.Stub.Json(HttpStatusCode.BadRequest, rejection)
            .Json(ai.Completion(Flow([Step("save", "write", by: "E4", store: "E5", dataset: "orders")])));

        var proposal = await BodyAsync(await ProposeFlowAsync(ai.Owner, projectId, "Save an order."), HttpStatusCode.Created);

        Assert.Equal("save", (string)proposal["scenario"]!["steps"]![0]!["id"]!);
        Assert.Equal(2, ai.Stub.Requests.Count);
        Assert.Contains(ai.NativeSchemaField, ai.Stub.Requests[0].Body);
        Assert.DoesNotContain(ai.NativeSchemaField, ai.Stub.Requests[1].Body);
        Assert.Contains("valid against this JSON Schema", Text(ai.Stub.Requests[1]));

        var rows = await ai.RowsAsync();
        Assert.Equal(["provider_error", "succeeded"], rows.Select(r => r.Outcome));
        Assert.Equal(("invalid_request", 0m, null), (rows[0].ErrorCode, rows[0].CostUsd ?? 0m, rows[0].UsageNote));
        Assert.Equal(AiUsageLedger.NoteSchemaFallback, rows[1].UsageNote);
        Assert.Equal("schema_rejected_fallback", AiUsageLedger.NoteSchemaFallback);

        // The fallback is made once. A provider that rejects the request in both modes fails it.
        var (twice, otherProject, _) = await OrdersAsync(_host, kind);
        twice.Stub.Json(HttpStatusCode.BadRequest, rejection);
        var failed = await BodyAsync(await ProposeFlowAsync(twice.Owner, otherProject, "Save an order."), HttpStatusCode.OK);
        Assert.Equal(("failed", "provider_error"), ((string)failed["status"]!, (string)failed["reason"]!["code"]!));
        Assert.Equal(2, twice.Stub.Requests.Count);

        // The connection test says that the provider's own mode is not usable, and what happens instead.
        var tested = await AiScenario.CreateAsync(_host, kind);
        tested.Stub.Json(HttpStatusCode.BadRequest, rejection).Json(tested.Completion("""{"ok":true}"""));
        var test = (await (await tested.Owner.PostAsync($"/api/workspaces/{tested.WorkspaceId}/ai/providers/{tested.ProviderId}/test", null))
            .Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.Equal((true, false), ((bool)test["ok"]!, (bool)test["supportsStructuredOutput"]!));
        Assert.Contains("schema_rejected_fallback", (string)test["structuredOutputNote"]!);
    }

    // ---- 3: things that exist already ---------------------------------------------------

    [Fact]
    public async Task A_missing_thing_that_the_model_holds_is_not_created_and_its_references_go_to_the_existing_thing()
    {
        var (ai, projectId, model) = await OrdersAsync(_host, "openai");
        ai.Stub.Json(ai.Completion(Flow(
            [
                Step("place", "call", from: "E2", operation: "N2", request: """{"sku":"apple","qty":1}"""),
                Step("publish", "publish", "then", "place", from: "N1", channel: "N3", message: "N4", payload: """{"orderId":"o-1"}"""),
                Step("charge", "call", "then", "place", order: 2, from: "N1", operation: "N7"),
            ],
            [
                // The same things as the model holds, spelled another way.
                Missing("N1", "element", "  orders ", elementKind: "app", parentAlias: "E6"),
                Missing("N2", "operation", "post  /orders/", ownerAlias: "N1", transport: "http"),
                Missing("N3", "channel", "ORDER-EVENTS", channelKind: "topic"),
                Missing("N4", "message", "ordercreated", ownerAlias: "N1"),
                Missing("N5", "subscription", "", channelAlias: "N3", consumerAlias: "E1"),
                // A look-alike and a hidden character.
                Missing("N6", "element", "\u041Erders", elementKind: "app", parentAlias: "E6"),
                Missing("N8", "channel", "order\u200B-events", channelKind: "topic"),
                // New things: an operation on the existing owner, and its owner by another alias.
                Missing("N7", "operation", "POST /charges", ownerAlias: "N1", transport: "http"),
            ])));
        var versionBefore = await ModelVersionAsync(_host, projectId);

        var proposal = await BodyAsync(await ProposeFlowAsync(ai.Owner, projectId, "Place an order and charge it."), HttpStatusCode.Created);

        var resolved = proposal["resolvedToExisting"]!.AsArray().ToDictionary(r => (string)r!["alias"]!, r => ((Guid)r!["id"]!, (string)r["name"]!));
        Assert.Equal(
            new Dictionary<string, (Guid, string)>
            {
                ["N1"] = (model.Orders, "Orders"), ["N2"] = (model.PlaceOrder, "POST /orders"), ["N3"] = (model.OrderEvents, "order-events"),
                ["N4"] = (model.OrderCreated, "OrderCreated"), ["N5"] = (model.BillingSub, "Billing on order-events"),
            }, resolved);
        // Only the thing that is new is left to create, and its owner is the existing element.
        var missing = Assert.Single(proposal["missing"]!.AsArray())!;
        Assert.Equal(("N7", "POST /charges", model.Orders), ((string)missing["alias"]!, (string)missing["name"]!, (Guid)missing["owner"]!["id"]!));
        var rejected = proposal["rejectedReferences"]!.AsArray().ToDictionary(r => (string)r!["where"]!, r => (string)r!["reason"]!);
        Assert.Equal(["missing:N6.name", "missing:N8.name"], rejected.Keys.Order(StringComparer.Ordinal));
        Assert.Contains("looks the same as the existing name 'Orders'", rejected["missing:N6.name"]);
        Assert.Contains("does not show", rejected["missing:N8.name"]);

        // The steps use the existing IDs. The step that needs the new operation waits.
        var place = proposal["scenario"]!["steps"]![0]!;
        Assert.Equal((model.Customer, model.PlaceOrder), ((Guid)place["from"]!, (Guid)place["operation"]!));
        var publish = Assert.Single(place["then"]!.AsArray())!;
        Assert.Equal((model.Orders, model.OrderEvents, model.OrderCreated), ((Guid)publish["from"]!, (Guid)publish["channel"]!, (Guid)publish["message"]!));
        Assert.Equal("charge", (string)Assert.Single(proposal["blockedSteps"]!.AsArray())!["stepId"]!);
        Assert.Equal(versionBefore, await ModelVersionAsync(_host, projectId));

        // An alias that resolved to an existing thing cannot be created.
        var again = await BodyAsync(await ai.Owner.PostAsync(Url(projectId, $"ai/flow-proposals/{proposal["id"]}/missing/N1/create"), null), HttpStatusCode.Conflict);
        Assert.Contains($"'Orders' ({model.Orders})", (string)again["title"]!);

        // A person adds the operation by hand. The create of N7 is then refused and names it.
        var byHand = Guid.NewGuid();
        await TestHost.Created(ai.Owner.PostAsJsonAsync(Url(projectId, "operations"), new { id = byHand, ownerId = model.Orders, name = "post /charges", transport = "http" }));
        var refused = await BodyAsync(await ai.Owner.PostAsync(Url(projectId, $"ai/flow-proposals/{proposal["id"]}/missing/N7/create"), null), HttpStatusCode.Conflict);
        Assert.Contains($"'post /charges' ({byHand})", (string)refused["title"]!);
        Assert.Single((await GetAsync(ai.Owner, Url(projectId, "operations")))["items"]!.AsArray(), o => ModelIndex.NormalRoute((string)o!["name"]!) == "POST /charges");

        // Read again, the proposal uses the operation that exists now.
        var read = await GetAsync(ai.Owner, Url(projectId, $"ai/flow-proposals/{proposal["id"]}"));
        Assert.Empty(read["missing"]!.AsArray());
        Assert.Empty(read["blockedSteps"]!.AsArray());
        Assert.Contains(read["resolvedToExisting"]!.AsArray(), r => (Guid)r!["id"]! == byHand);
        Assert.True((bool)read["stale"]!);
    }

    [Fact]
    public async Task An_architecture_item_that_the_model_holds_is_not_proposed_and_is_refused_when_it_appears_before_the_decision()
    {
        var (ai, projectId, model) = await OrdersAsync(_host, "openai");
        ai.Stub.Json(ai.Completion(Architecture(
            Item("N1", "element", "orders", elementKind: "app", parent: "E6"),
            Item("N2", "operation", "POST /orders/{id}/cancel", owner: "N1", transport: "http"),
            Item("N3", "channel", "Order-Events", channelKind: "topic"),
            Item("N4", "element", "Payments", elementKind: "app", parent: "E6"),
            Item("N5", "relationship", from: "N1", to: "N4", label: "Requests payment"),
            Item("N6", "element", "Ord\u0435rs", elementKind: "app", parent: "E6"),
            Item("N7", "message", "OrderCreated", producer: "N1"))));

        var proposal = await BodyAsync(await ProposeArchitectureAsync(ai.Owner, projectId, "Add payments."), HttpStatusCode.Created);

        Assert.Equal(
            [("N1", model.Orders), ("N3", model.OrderEvents), ("N7", model.OrderCreated)],
            proposal["resolvedToExisting"]!.AsArray().Select(r => ((string)r!["alias"]!, (Guid)r["id"]!)).OrderBy(r => r.Item1, StringComparer.Ordinal));
        var items = proposal["items"]!.AsArray().ToDictionary(i => (string)i!["alias"]!, i => i!);
        Assert.Equal(["N2", "N4", "N5"], items.Keys.Order(StringComparer.Ordinal));
        // The operation hangs from the existing element, and so does the relationship. Neither waits for an item.
        Assert.Equal(model.Orders, (Guid)items["N2"]["proposed"]!["ownerId"]!);
        Assert.Empty(items["N2"]["dependsOn"]!.AsArray());
        Assert.Equal(model.Orders, (Guid)items["N5"]["proposed"]!["fromId"]!);
        Assert.Contains("looks the same", (string)Assert.Single(proposal["rejectedReferences"]!.AsArray())!["reason"]!);
        Assert.False((bool)proposal["stale"]!);

        // A person adds Payments by hand before the decision.
        var byHand = Guid.NewGuid();
        await TestHost.Created(ai.Owner.PostAsJsonAsync(Url(projectId, "elements"), new { id = byHand, kind = "app", name = "payments", parentId = model.Shop }));
        var read = await GetAsync(ai.Owner, Url(projectId, $"ai/architecture-proposals/{proposal["id"]}"));
        Assert.True((bool)read["stale"]!);
        var issue = Assert.Single(read["itemIssues"]!.AsArray())!;
        Assert.Equal(((Guid)items["N4"]["id"]!, true), ((Guid)issue["itemId"]!, ((string)issue["reason"]!).Contains($"'payments' ({byHand})")));

        var versionBefore = await ModelVersionAsync(_host, projectId);
        var refused = await BodyAsync(await ai.Owner.PostAsJsonAsync(
            Url(projectId, $"ai/architecture-proposals/{proposal["id"]}/decide"), new { accept = new[] { (Guid)items["N4"]["id"]!, (Guid)items["N2"]["id"]! } }),
            HttpStatusCode.Conflict);
        var failure = Assert.Single(refused["failures"]!.AsArray())!;
        Assert.Equal((Guid)items["N4"]["id"]!, (Guid)failure["itemId"]!);
        Assert.Contains($"'payments' ({byHand})", (string)failure["reason"]!);
        Assert.Equal(versionBefore, await ModelVersionAsync(_host, projectId));
    }

    // ---- 4: a proposal that the model moved away from -----------------------------------

    [Fact]
    public async Task A_proposal_is_read_and_accepted_against_the_model_as_it_is_now()
    {
        var (ai, projectId, model) = await OrdersAsync(_host, "openai");
        ai.Stub.Json(ai.Completion(Reply("flow-orders")));
        var proposal = await BodyAsync(await ProposeFlowAsync(ai.Owner, projectId, OrdersRequest), HttpStatusCode.Created);
        var url = Url(projectId, $"ai/flow-proposals/{proposal["id"]}");
        Assert.Empty(proposal["issues"]!.AsArray());
        Assert.False((bool)proposal["stale"]!);
        Assert.False((bool)(await GetAsync(ai.Owner, url))["stale"]!);

        // The subscription that a handler of the flow runs under goes away.
        Assert.Equal(HttpStatusCode.NoContent, (await ai.Owner.DeleteAsync(Url(projectId, $"subscriptions/{model.BillingSub}"))).StatusCode);

        var read = await GetAsync(ai.Owner, url);
        Assert.True((bool)read["stale"]!);
        Assert.Contains("unknown-subscription", Codes(read));
        Assert.Null(read["preview"]);
        Assert.Equal((long)proposal["modelVersion"]!, (long)read["modelVersion"]!);

        // The old preview is not what gets accepted.
        var refused = await BodyAsync(await ai.Owner.PostAsync($"{url}/accept", null), HttpStatusCode.Conflict);
        Assert.Equal("proposal_stale", (string)refused["type"]!);
        Assert.Contains("unknown-subscription", refused["issues"]!.AsArray().Select(i => (string)i!["code"]!));
        Assert.Null(refused["preview"]);
        Assert.Equal(await ModelVersionAsync(_host, projectId), (long)refused["modelVersion"]!);
        Assert.Empty((await GetAsync(ai.Owner, Url(projectId, "scenarios")))["items"]!.AsArray());
        Assert.Equal("draft", (string)(await GetAsync(ai.Owner, url))["status"]!);

        // A person who has seen the issues can accept the proposal as it is now.
        var accepted = await BodyAsync(await ai.Owner.PostAsJsonAsync($"{url}/accept", new { acceptWithIssues = true }), HttpStatusCode.OK);
        var decided = await GetAsync(ai.Owner, url);
        Assert.Equal(("accepted", false), ((string)decided["status"]!, (bool)decided["stale"]!));
        Assert.Contains("unknown-subscription", Codes(decided));

        // 10: the scenario says where it came from. A scenario that a person wrote does not.
        var scenario = await GetAsync(ai.Owner, Url(projectId, $"scenarios/{accepted["scenarioId"]}"));
        Assert.Equal(
            ("ai-proposal", (Guid)proposal["id"]!, "openai", ai.Model),
            ((string)scenario["origin"]!["kind"]!, (Guid)scenario["origin"]!["proposalId"]!, (string)scenario["origin"]!["provider"]!, (string)scenario["origin"]!["model"]!));
        var own = Guid.NewGuid();
        await TestHost.Created(ai.Owner.PostAsJsonAsync(Url(projectId, "scenarios"), new { id = own, name = "By hand", steps = Array.Empty<object>(), faults = Array.Empty<object>(), assertions = Array.Empty<object>() }));
        var byHand = await GetAsync(ai.Owner, Url(projectId, $"scenarios/{own}"));
        Assert.True(byHand.ContainsKey("origin"));
        Assert.Null(byHand["origin"]);

        // A person's edit of the AI-authored scenario keeps the origin.
        scenario["name"] = "Edited";
        Assert.Equal(HttpStatusCode.OK, (await ai.Owner.PutAsync(
            Url(projectId, $"scenarios/{accepted["scenarioId"]}"), new StringContent(scenario.ToJsonString(), Encoding.UTF8, "application/json"))).StatusCode);
        Assert.Equal("ai-proposal", (string)(await GetAsync(ai.Owner, Url(projectId, $"scenarios/{accepted["scenarioId"]}")))["origin"]!["kind"]!);
    }

    // ---- 5 and 7: private sources -------------------------------------------------------

    /// <summary>Marks an element as observed in a repository and links it to a scan of that repository.</summary>
    private async Task LinkToRepositoryAsync(AiScenario ai, Guid projectId, Guid elementId, string repository, bool? isPrivate)
    {
        await ScopedAsync(async services =>
        {
            var streamId = await services.GetRequiredService<WorkspacesDbContext>().Projects.AsNoTracking()
                .Where(p => p.Id == projectId).Select(p => p.StreamId).FirstAsync();
            var source = $"github.com/{repository}@4f9a1c7:src/Service.cs:1-9";
            var recorded = await services.GetRequiredService<IEventStore>().ForProject(projectId).Decide<ModelState>(
                streamId,
                state => ModelDecider.RecordElementProvenance(state, elementId, FactStatus.ObservedInSource, new Provenance(source, "4f9a1c7")),
                CancellationToken.None);
            Assert.IsType<CommandResult.Success>(recorded);

            var import = services.GetRequiredService<ImportDbContext>();
            var scan = new ScanEntity
            {
                Id = Guid.NewGuid(), ProjectId = projectId, WorkspaceId = ai.WorkspaceId, Host = "github.com", Repository = repository,
                CommitSha = "4f9a1c7", RepositoryIsPrivate = isPrivate, Limits = new ScanLimits(10, 10, 10, []).ToJson(),
                Status = ScanStatus.Completed, RequestedBy = "user:test", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            };
            import.Scans.Add(scan);
            import.FactLinks.Add(new FactLinkEntity
            {
                ProjectId = projectId, Host = "github.com", Repository = repository, FactKey = $"service:{elementId:N}", FactKeyHash = Vellum.Modules.Import.Scans.KeyHash.Of($"service:{elementId:N}"),
                EntityKind = EntityKinds.Element, EntityId = elementId, ScanId = scan.Id, CommitSha = "4f9a1c7",
                AcceptedBy = "user:test", AcceptedAt = DateTimeOffset.UtcNow,
            });
            await import.SaveChangesAsync();
            return true;
        });
    }

    private static async Task AllowPrivateCodeAsync(AiScenario ai)
    {
        var settings = await GetAsync(ai.Owner, $"/api/workspaces/{ai.WorkspaceId}/settings");
        var overrides = settings["overrides"]!.DeepClone().AsObject();
        overrides["privacy"] = new JsonObject { ["sendPrivateCodeToAi"] = true };
        await OutboundHost.PutSettingsAsync(ai.Owner, ai.WorkspaceId, overrides);
        Assert.True((bool)(await GetAsync(ai.Owner, $"/api/workspaces/{ai.WorkspaceId}/settings"))["effective"]!["privacy"]!["sendPrivateCodeToAi"]!);
    }

    [Fact]
    public async Task What_came_from_a_private_repository_stays_out_of_every_prompt_until_the_setting_allows_it()
    {
        var (ai, projectId, model) = await OrdersAsync(_host, "anthropic");
        var reply = Replies(ai);
        // Inventory came from a private repository, Billing from a public one. A person typed the rest.
        await LinkToRepositoryAsync(ai, projectId, model.Inventory, "acme/secret-warehouse", isPrivate: true);
        await LinkToRepositoryAsync(ai, projectId, model.Billing, "acme/open-billing", isPrivate: false);
        // A link to a declared element does not make what a person typed private.
        await ScopedAsync(async services =>
        {
            var import = services.GetRequiredService<ImportDbContext>();
            var scan = await import.Scans.FirstAsync(s => s.ProjectId == projectId && s.RepositoryIsPrivate == true);
            import.FactLinks.Add(new FactLinkEntity
            {
                ProjectId = projectId, Host = scan.Host, Repository = scan.Repository, FactKey = "service:orders", FactKeyHash = Vellum.Modules.Import.Scans.KeyHash.Of("service:orders"), EntityKind = EntityKinds.Element,
                EntityId = model.Orders, ScanId = scan.Id, CommitSha = "4f9a1c7", AcceptedBy = "user:test", AcceptedAt = DateTimeOffset.UtcNow,
            });
            return await import.SaveChangesAsync();
        });

        // The setting is off. A flow is proposed: the table does not hold Inventory or its subscription.
        reply(Flow([Step("save", "write", by: "E3", store: "E4", dataset: "orders")]));
        var proposal = await BodyAsync(await ProposeFlowAsync(ai.Owner, projectId, "Reserve stock in Inventory."), HttpStatusCode.Created);
        var sent = Text(ai.Stub.Requests[^1]);
        Assert.DoesNotContain("Inventory", sent.Replace("Reserve stock in Inventory.", ""));
        Assert.DoesNotContain("secret-warehouse", sent);
        Assert.Contains("\"name\":\"Billing\"", sent);
        Assert.Contains("\"name\":\"Orders\"", sent);
        Assert.Contains("\"withheld\":1", sent);
        Assert.Equal((1, "privacy.sendPrivateCodeToAi"), ((int)proposal["omittedPrivate"]!["count"]!, (string)proposal["omittedPrivate"]!["reason"]!));
        // Without Inventory the aliases move up: E3 is Orders now.
        Assert.Equal(model.Orders, (Guid)proposal["scenario"]!["steps"]![0]!["by"]!);

        reply(Architecture(Item("N1", "channel", "stock-events", channelKind: "topic")));
        var additions = await BodyAsync(await ProposeArchitectureAsync(ai.Owner, projectId, "Add stock events."), HttpStatusCode.Created);
        Assert.DoesNotContain("Inventory", Text(ai.Stub.Requests[^1]));
        Assert.Equal(1, (int)additions["omittedPrivate"]!["count"]!);

        // The private thing itself is not explained, and no provider is called for it.
        var calls = ai.Stub.Requests.Count;
        var hidden = await ExplainAsync(ai.Owner, projectId, "element", model.Inventory);
        Assert.Equal("no-store", hidden.Headers.CacheControl!.ToString());
        var unavailable = await BodyAsync(hidden, HttpStatusCode.OK);
        Assert.Equal(("unavailable", "disabled", "privacy.sendPrivateCodeToAi"),
            ((string)unavailable["status"]!, (string)unavailable["reason"]!["code"]!, (string)unavailable["reason"]!["setting"]!));
        Assert.Equal(calls, ai.Stub.Requests.Count);

        // A neighbour is explained without it, and the stream says so first.
        var stream = await ExplainAsync(ai.Owner, projectId, "element", model.Shop);
        Assert.Equal("no-store", stream.Headers.CacheControl!.ToString());
        var events = Events(await stream.Content.ReadAsStringAsync());
        Assert.Equal(1, (int)events[0]["notice"]!["omittedPrivate"]!);
        Assert.Equal("declared-design", (string)events[^1]["disclaimer"]!);
        var explained = Text(ai.Stub.Requests[^1]);
        Assert.DoesNotContain("Inventory", explained);
        Assert.Contains("\"name\":\"Billing\"", explained);
        Assert.Contains("left out of the context", explained);

        // The source pointer of a public repository is sent. It is the target's own.
        var billing = await ExplainAsync(ai.Owner, projectId, "element", model.Billing);
        Assert.DoesNotContain(Events(await billing.Content.ReadAsStringAsync()), e => e.ContainsKey("notice"));
        Assert.Contains("github.com/acme/open-billing@4f9a1c7:src/Service.cs:1-9", Text(ai.Stub.Requests[^1]));

        // The setting is on: the same requests hold the private thing, and nothing is counted as left out.
        await AllowPrivateCodeAsync(ai);
        reply(Flow([Step("save", "write", by: "E4", store: "E5", dataset: "orders")]));
        var allowed = await BodyAsync(await ProposeFlowAsync(ai.Owner, projectId, "Reserve stock."), HttpStatusCode.Created);
        Assert.Contains("\"name\":\"Inventory\"", Text(ai.Stub.Requests[^1]));
        Assert.Null(allowed["omittedPrivate"]);
        Assert.Equal(model.Orders, (Guid)allowed["scenario"]!["steps"]![0]!["by"]!);

        var open = await ExplainAsync(ai.Owner, projectId, "element", model.Inventory);
        Assert.Equal("text/event-stream", open.Content.Headers.ContentType!.MediaType);
        Assert.DoesNotContain(Events(await open.Content.ReadAsStringAsync()), e => e.ContainsKey("notice"));
        Assert.Contains("github.com/acme/secret-warehouse@4f9a1c7:src/Service.cs:1-9", Text(ai.Stub.Requests[^1]));
    }

    [Fact]
    public async Task A_step_of_a_revised_scenario_that_references_a_thing_outside_the_table_is_kept_as_it_is()
    {
        var (ai, projectId, model) = await OrdersAsync(_host, "anthropic");
        var reply = Replies(ai);
        var scenarioId = Guid.NewGuid();
        await TestHost.Created(ai.Owner.PostAsync(Url(projectId, "scenarios"), new StringContent(
            $$$"""
            {"id":"{{{scenarioId}}}","name":"Reserve","steps":[
              {"type":"write","id":"save","by":"{{{model.Orders}}}","store":"{{{model.OrdersDb}}}","dataset":"orders","change":{"status":"new"}},
              {"type":"write","id":"reserve","by":"{{{model.Inventory}}}","store":"{{{model.OrdersDb}}}","dataset":"stock","change":{"secret-field":7}}],
             "faults":[{"target":"{{{model.Inventory}}}","kind":"error","failFirst":1}],
             "assertions":[{"type":"event-count","id":"writes","kind":"DataWritten","target":"{{{model.Inventory}}}","expected":0}]}
            """, Encoding.UTF8, "application/json")));
        await LinkToRepositoryAsync(ai, projectId, model.Inventory, "acme/secret-warehouse", isPrivate: true);

        // The model returns the step it saw, changed, and writes a step with the ID of the one it did not see.
        reply(Flow(
        [
            Step("save", "write", by: "E3", store: "E4", dataset: "orders-v2", change: """{"status":"new"}"""),
            Step("reserve", "write", order: 2, by: "E3", store: "E4", dataset: "overwritten"),
        ]));
        var proposal = await BodyAsync(await ProposeFlowAsync(ai.Owner, projectId, "Save to orders-v2.", scenarioId), HttpStatusCode.Created);

        // Nothing of the hidden step went out: no name, no payload, no placeholder.
        var sent = Text(ai.Stub.Requests[^1]);
        Assert.DoesNotContain("Inventory", sent);
        Assert.DoesNotContain("secret-field", sent);
        Assert.DoesNotContain("UNKNOWN", sent);
        Assert.DoesNotContain(model.Inventory.ToString("N"), sent);
        Assert.Contains("\"kept\":[{\"id\":\"reserve\"", sent);

        // The step, the fault and the assertion are in the proposal as they were.
        var steps = proposal["scenario"]!["steps"]!.AsArray();
        Assert.Equal(["save", "reserve"], steps.Select(s => (string)s!["id"]!));
        Assert.Equal((model.Inventory, "stock", 7), ((Guid)steps[1]!["by"]!, (string)steps[1]!["dataset"]!, (int)steps[1]!["change"]!["secret-field"]!));
        Assert.Equal(model.Inventory, (Guid)proposal["scenario"]!["faults"]![0]!["target"]!);
        Assert.Equal(model.Inventory, (Guid)proposal["scenario"]!["assertions"]![0]!["target"]!);
        Assert.Equal(["save"], proposal["diff"]!["changedSteps"]!.AsArray().Select(s => (string)s!));
        Assert.Empty(proposal["diff"]!["removedSteps"]!.AsArray());
        Assert.Contains("kept-step-not-revised", Codes(proposal));
        Assert.Empty(proposal["rejectedReferences"]!.AsArray());

        var accepted = await BodyAsync(await ai.Owner.PostAsync(Url(projectId, $"ai/flow-proposals/{proposal["id"]}/accept"), null), HttpStatusCode.OK);
        var saved = await GetAsync(ai.Owner, Url(projectId, $"scenarios/{accepted["scenarioId"]}"));
        Assert.Equal(("orders-v2", "stock"), ((string)saved["steps"]![0]!["dataset"]!, (string)saved["steps"]![1]!["dataset"]!));
    }

    // ---- 6: the model append and the proposal row are one unit --------------------------

    [Fact]
    public async Task When_the_proposal_row_cannot_be_updated_the_model_append_is_rolled_back()
    {
        var (ai, projectId, _) = await OrdersAsync(_host, "openai");
        var reply = Replies(ai);
        reply(Reply("flow-missing"));
        var flow = await BodyAsync(await ProposeFlowAsync(ai.Owner, projectId, "Charge the order."), HttpStatusCode.Created);
        reply(Architecture(Item("N1", "channel", "refund-events", channelKind: "topic")));
        var additions = await BodyAsync(await ProposeArchitectureAsync(ai.Owner, projectId, "Add refunds."), HttpStatusCode.Created);
        var item = (Guid)additions["items"]![0]!["id"]!;
        var versionBefore = await ModelVersionAsync(_host, projectId);

        // The database refuses every update of the two proposal rows.
        var trigger = $"fail_{Guid.NewGuid():N}";
        await using (var connection = new NpgsqlConnection(shared.Fixture.ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(
                $"""
                CREATE FUNCTION ai.{trigger}() RETURNS trigger AS $$ BEGIN RAISE EXCEPTION 'the row update fails'; END $$ LANGUAGE plpgsql;
                CREATE TRIGGER {trigger} BEFORE UPDATE ON ai.design_proposals FOR EACH ROW
                    WHEN (OLD.id IN ('{flow["id"]}', '{additions["id"]}')) EXECUTE FUNCTION ai.{trigger}();
                """, connection);
            await command.ExecuteNonQueryAsync();
        }

        try
        {
            var missing = (string)flow["missing"]![0]!["alias"]!;
            var create = await ai.Owner.PostAsync(Url(projectId, $"ai/flow-proposals/{flow["id"]}/missing/{missing}/create"), null);
            var decide = await ai.Owner.PostAsJsonAsync(Url(projectId, $"ai/architecture-proposals/{additions["id"]}/decide"), new { accept = new[] { item } });

            Assert.Equal(HttpStatusCode.InternalServerError, create.StatusCode);
            Assert.Equal(HttpStatusCode.InternalServerError, decide.StatusCode);
        }
        finally
        {
            await using var connection = new NpgsqlConnection(shared.Fixture.ConnectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(
                $"DROP TRIGGER {trigger} ON ai.design_proposals; DROP FUNCTION ai.{trigger}();", connection);
            await command.ExecuteNonQueryAsync();
        }

        // Nothing reached the model, and the proposals show what the model holds: nothing created, the item pending.
        Assert.Equal(versionBefore, await ModelVersionAsync(_host, projectId));
        Assert.DoesNotContain((await GetAsync(ai.Owner, Url(projectId, "channels")))["items"]!.AsArray(), c => (string)c!["name"]! == "refund-events");
        Assert.All((await GetAsync(ai.Owner, Url(projectId, $"ai/flow-proposals/{flow["id"]}")))["missing"]!.AsArray(), m => Assert.Null(m!["createdId"]));
        Assert.Equal("pending", (string)(await GetAsync(ai.Owner, Url(projectId, $"ai/architecture-proposals/{additions["id"]}")))["items"]![0]!["status"]!);

        // With the fault gone the same requests succeed.
        var decided = await BodyAsync(await ai.Owner.PostAsJsonAsync(
            Url(projectId, $"ai/architecture-proposals/{additions["id"]}/decide"), new { accept = new[] { item } }), HttpStatusCode.OK);
        Assert.Equal("accepted", (string)decided["items"]![0]!["status"]!);
        Assert.Contains((await GetAsync(ai.Owner, Url(projectId, "channels")))["items"]!.AsArray(), c => (string)c!["name"]! == "refund-events");
    }

    // ---- 8 and 9: cost controls and the explanation stream ------------------------------

    [Fact]
    public async Task A_project_holds_a_bounded_number_of_drafts_and_a_draft_can_be_deleted()
    {
        var (ai, projectId, _) = await OrdersAsync(_host, "openai");
        ai.Stub.Json(ai.Completion(Reply("flow-orders")));
        var first = await BodyAsync(await ProposeFlowAsync(ai.Owner, projectId, OrdersRequest), HttpStatusCode.Created);

        // The project is filled with drafts of both kinds.
        await ScopedAsync(async services =>
        {
            var db = services.GetRequiredService<AiDesignDbContext>();
            var row = await db.Proposals.AsNoTracking().SingleAsync(p => p.Id == (Guid)first["id"]!);
            for (var i = 1; i < ProposalStore.MaxDraftsPerProject; i++)
            {
                db.Proposals.Add(new Vellum.Modules.Ai.Design.ProposalEntity
                {
                    Id = Guid.NewGuid(), Kind = i % 2 == 0 ? FlowProposals.Kind : ArchitectureProposals.Kind, WorkspaceId = row.WorkspaceId,
                    ProjectId = projectId, Status = "draft", Name = $"Draft {i}", Request = row.Request, Document = row.Document,
                    CreatedBy = row.CreatedBy, CreatedAt = row.CreatedAt.AddSeconds(i),
                });
            }
            return await db.SaveChangesAsync();
        });

        // The next request is refused before a provider is called.
        foreach (var propose in new[] { ProposeFlowAsync(ai.Owner, projectId, OrdersRequest), ProposeArchitectureAsync(ai.Owner, projectId, "Add payments.") })
        {
            var full = await BodyAsync(await propose, HttpStatusCode.Conflict);
            Assert.Equal("too_many_drafts", (string)full["type"]!);
            Assert.Contains("delete a draft", (string)full["detail"]!);
        }
        Assert.Single(ai.Stub.Requests);

        // A viewer cannot delete. An editor deletes a draft, and there is room again.
        var url = Url(projectId, $"ai/flow-proposals/{first["id"]}");
        var viewer = await TestHost.SignInMemberAsync(_host.Factory, ai.Owner, ai.WorkspaceId, "viewer");
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.DeleteAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await ai.Owner.DeleteAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await ai.Owner.GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await ai.Owner.DeleteAsync(url)).StatusCode);
        var second = await BodyAsync(await ProposeFlowAsync(ai.Owner, projectId, OrdersRequest), HttpStatusCode.Created);

        // A decided proposal is a record of a decision. It is not deleted.
        await BodyAsync(await ai.Owner.PostAsync(Url(projectId, $"ai/flow-proposals/{second["id"]}/reject"), null), HttpStatusCode.OK);
        var kept = await BodyAsync(await ai.Owner.DeleteAsync(Url(projectId, $"ai/flow-proposals/{second["id"]}")), HttpStatusCode.Conflict);
        Assert.Contains("Only a draft can be deleted", (string)kept["title"]!);
    }

    [Fact]
    public async Task The_generating_endpoints_share_a_token_bucket_for_each_user()
    {
        // No provider is routed, so a request that is let through costs nothing.
        var (ai, projectId, model) = await OrdersAsync(_host, "openai", route: false);

        HttpResponseMessage? limited = null;
        for (var i = 0; i < AiGenerationLimit.PerUser + 1 && limited is null; i++)
        {
            var response = (i % 3) switch
            {
                0 => await ExplainAsync(ai.Owner, projectId, "element", model.Orders),
                1 => await ProposeFlowAsync(ai.Owner, projectId, OrdersRequest),
                _ => await ProposeArchitectureAsync(ai.Owner, projectId, "Add payments."),
            };
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
                limited = response;
            else
                Assert.Equal("unavailable", (string)(await BodyAsync(response, HttpStatusCode.OK))["status"]!);
        }

        Assert.NotNull(limited);
        Assert.InRange(int.Parse(limited.Headers.GetValues("Retry-After").Single()), 1, 60);
        var body = (await limited.Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.Equal("rate_limited", (string)body["type"]!);
        Assert.Matches(@"Wait \d+ seconds", (string)body["detail"]!);

        // The reads and the decisions are not limited, and another user of the project has a bucket of their own.
        Assert.Equal(HttpStatusCode.OK, (await ai.Owner.GetAsync(Url(projectId, "ai/flow-proposals"))).StatusCode);
        var other = await TestHost.SignInMemberAsync(_host.Factory, ai.Owner, ai.WorkspaceId, "viewer");
        Assert.Equal(HttpStatusCode.OK, (await ExplainAsync(other, projectId, "element", model.Orders)).StatusCode);
    }

    [Fact]
    public async Task A_user_has_a_bounded_number_of_explanations_in_progress()
    {
        var (ai, projectId, model) = await OrdersAsync(_host, "openai");
        // Each stream starts and then waits for the provider.
        var whole = ai.Fixture("stream.sse");
        var first = Encoding.UTF8.GetBytes(whole[..whole.IndexOf(" world", StringComparison.Ordinal)]);
        ai.Stub.Body(() => new WaitingStream(first), "text/event-stream");
        using var cancel = new CancellationTokenSource();

        var open = new List<HttpResponseMessage>();
        try
        {
            for (var i = 0; i < ExplainStreams.MaxPerUser; i++)
            {
                var request = new HttpRequestMessage(HttpMethod.Post, Url(projectId, "ai/explain"))
                {
                    Content = JsonContent.Create(new { target = new { kind = "element", id = model.Orders } }),
                };
                open.Add(await ai.Owner.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel.Token));
                Assert.Equal("text/event-stream", open[^1].Content.Headers.ContentType!.MediaType);
            }

            var refused = await ExplainAsync(ai.Owner, projectId, "element", model.Orders);
            Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
            Assert.Equal("too_many_streams", (string)(await refused.Content.ReadFromJsonAsync<JsonObject>())!["type"]!);
            Assert.Equal(ExplainStreams.MaxPerUser, ai.Stub.Requests.Count);
        }
        finally
        {
            cancel.Cancel();
            foreach (var response in open)
                response.Dispose();
        }

        // A stream that ended gives its place back.
        ai.Stub.Sse(ai.Fixture("stream.sse"));
        for (var attempt = 0; ; attempt++)
        {
            var again = await ExplainAsync(ai.Owner, projectId, "element", model.Orders);
            if (again.StatusCode == HttpStatusCode.OK)
                break;
            Assert.True(attempt < 100, "The places of the closed streams did not come back.");
            await Task.Delay(50);
        }
    }

    /// <summary>Gives its bytes once and then waits until the reader is cancelled.</summary>
    private sealed class WaitingStream(byte[] bytes) : Stream
    {
        private int _position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (_position < bytes.Length)
            {
                var count = Math.Min(buffer.Length, bytes.Length - _position);
                bytes.AsMemory(_position, count).CopyTo(buffer);
                _position += count;
                return count;
            }

            await Task.Delay(Timeout.Infinite, ct);
            return 0;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count, default).GetAwaiter().GetResult();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
