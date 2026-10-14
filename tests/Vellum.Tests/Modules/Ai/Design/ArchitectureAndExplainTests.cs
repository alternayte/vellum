using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Vellum.Modules.Ai.Design;
using Vellum.Tests.Shared.Outbound;
using static Vellum.Tests.Modules.Ai.Design.DesignKit;

namespace Vellum.Tests.Modules.Ai.Design;

/// <summary>Additions to the model as proposals, and Rubber Duck explanations, through the real provider adapters.</summary>
public class ArchitectureAndExplainTests(SharedOutboundHost shared) : IClassFixture<SharedOutboundHost>
{
    private readonly OutboundHost _host = shared.Host;

    public static TheoryData<string> Kinds => new(AiScenario.Kinds);

    private static async Task<JsonObject> GetAsync(HttpClient client, string url) =>
        await BodyAsync(await client.GetAsync(url), HttpStatusCode.OK);

    private static Task<HttpResponseMessage> DecideAsync(HttpClient client, Guid projectId, JsonObject proposal, Guid[] accept, Guid[]? reject = null) =>
        client.PostAsJsonAsync(Url(projectId, $"ai/architecture-proposals/{proposal["id"]}/decide"), new { accept, reject = reject ?? [] });

    [Theory, MemberData(nameof(Kinds))]
    public async Task Additions_are_proposed_with_real_ids_and_written_as_inferred_facts_only_when_accepted(string kind)
    {
        var (ai, projectId, model) = await OrdersAsync(_host, kind);
        ai.Stub.Json(ai.Completion(Reply("architecture-payments")));
        var versionBefore = await ModelVersionAsync(_host, projectId);

        var proposal = await BodyAsync(await ProposeArchitectureAsync(ai.Owner, projectId, "Orders must charge the customer and tell Billing."), HttpStatusCode.Created);

        Assert.Equal(("draft", "additions"), ((string)proposal["status"]!, (string)proposal["scope"]!));
        var items = proposal["items"]!.AsArray().ToDictionary(i => (string)i!["alias"]!, i => i!.AsObject());
        Assert.Equal(["N1", "N2", "N3", "N4", "N5", "N6"], items.Keys);
        Assert.All(items.Values, item => Assert.Equal(("add", "pending"), ((string)item["action"]!, (string)item["status"]!)));
        Guid Id(string alias) => (Guid)items[alias]["id"]!;

        // An existing thing is named by its real ID, a new thing by the ID of its item.
        Assert.Equal(("app", "Payments", model.Shop, "Go"),
            ((string)items["N1"]["proposed"]!["kind"]!, (string)items["N1"]["proposed"]!["name"]!, (Guid)items["N1"]["proposed"]!["parentId"]!, (string)items["N1"]["proposed"]!["technology"]!));
        Assert.Empty(items["N1"]["dependsOn"]!.AsArray());
        Assert.Equal((Id("N1"), "http"), ((Guid)items["N2"]["proposed"]!["ownerId"]!, (string)items["N2"]["proposed"]!["transport"]!));
        Assert.Equal([Id("N1")], items["N2"]["dependsOn"]!.AsArray().Select(d => (Guid)d!));
        Assert.Equal((model.Orders, Id("N1"), "behavioural"),
            ((Guid)items["N3"]["proposed"]!["fromId"]!, (Guid)items["N3"]["proposed"]!["toId"]!, (string)items["N3"]["proposed"]!["kind"]!));
        Assert.Equal("Orders → Payments: Requests payment", (string)items["N3"]["name"]!);
        Assert.Equal((Id("N4"), model.Billing), ((Guid)items["N5"]["proposed"]!["channelId"]!, (Guid)items["N5"]["proposed"]!["consumerId"]!));
        Assert.Equal("Billing on payment-events", (string)items["N5"]["name"]!);
        Assert.Equal([model.Billing], items["N6"]["proposed"]!["consumerIds"]!.AsArray().Select(c => (Guid)c!));
        Assert.Equal(0.9, (double)items["N1"]["confidence"]!);

        // Everything else was dropped, with the reason: a change, a removal, invented
        // references, an item that hangs from a dropped one, a taken alias, an unknown kind.
        var rejected = proposal["rejectedReferences"]!.AsArray().ToDictionary(r => (string)r!["where"]!, r => ((string)r!["alias"]!, (string)r["reason"]!));
        Assert.Equal(
            ["item:E4", "item:N10.from", "item:N11.owner", "item:N12.kind", "item:N7.action", "item:N8.action", "item:N9.owner"],
            rejected.Keys.Order(StringComparer.Ordinal));
        Assert.Equal("E4", rejected["item:N7.action"].Item1);
        Assert.Contains("additions only", rejected["item:N7.action"].Item2);
        Assert.Equal("E5", rejected["item:N8.action"].Item1);
        Assert.Equal("E99", rejected["item:N9.owner"].Item1);
        Assert.Contains("raw ID", rejected["item:N10.from"].Item2);

        // Proposing wrote nothing, and no ID of the model left the instance.
        Assert.Equal(versionBefore, await ModelVersionAsync(_host, projectId));
        var sent = Text(Assert.Single(ai.Stub.Requests));
        Assert.DoesNotContain(model.All, id => sent.Contains(id.ToString(), StringComparison.OrdinalIgnoreCase));
        Assert.Contains("This version proposes additions only", sent);

        // An item is refused while what it needs is not accepted. Nothing of the request is applied.
        var partial = await BodyAsync(await DecideAsync(ai.Owner, projectId, proposal, [Id("N4"), Id("N2")]), HttpStatusCode.Conflict);
        Assert.Equal("items_refused", (string)partial["type"]!);
        var failure = Assert.Single(partial["failures"]!.AsArray())!;
        Assert.Equal(Id("N2"), (Guid)failure["itemId"]!);
        Assert.Contains("Payments", (string)failure["reason"]!);
        var together = await BodyAsync(await DecideAsync(ai.Owner, projectId, proposal, [Id("N2")], [Id("N1")]), HttpStatusCode.Conflict);
        Assert.Contains("rejected", (string)together["failures"]![0]!["reason"]!);
        Assert.Equal(versionBefore, await ModelVersionAsync(_host, projectId));
        Assert.All((await GetAsync(ai.Owner, Url(projectId, $"ai/architecture-proposals/{proposal["id"]}")))["items"]!.AsArray(),
            item => Assert.Equal("pending", (string)item!["status"]!));

        // Requests that name no item, an unknown item or one item twice are not decisions.
        Assert.Equal(HttpStatusCode.BadRequest, (await DecideAsync(ai.Owner, projectId, proposal, [])).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await DecideAsync(ai.Owner, projectId, proposal, [Guid.NewGuid()])).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await DecideAsync(ai.Owner, projectId, proposal, [Id("N4")], [Id("N4")])).StatusCode);

        // Accepted with what they depend on, given in the wrong order; the channel is rejected.
        var decided = await BodyAsync(await DecideAsync(ai.Owner, projectId, proposal, [Id("N6"), Id("N3"), Id("N2"), Id("N1")], [Id("N4")]), HttpStatusCode.OK);
        Assert.Equal(
            [("N1", "accepted"), ("N2", "accepted"), ("N3", "accepted"), ("N4", "rejected"), ("N5", "pending"), ("N6", "accepted")],
            decided["items"]!.AsArray().Select(i => ((string)i!["alias"]!, (string)i["status"]!)));
        Assert.Equal("draft", (string)decided["status"]!);

        var me = (await ai.Owner.GetFromJsonAsync<JsonObject>("/api/auth/me"))!;
        var source = $"ai:{kind}/{ai.Model}#architecture-proposal:{proposal["id"]}";
        void Inferred(JsonNode fact, double confidence)
        {
            Assert.Equal("inferred", (string)fact["factStatus"]!);
            Assert.Equal((source, confidence, (string)me["id"]!),
                ((string)fact["provenance"]!["source"]!, (double)fact["provenance"]!["confidence"]!, (string)fact["provenance"]!["acceptedBy"]!));
            Assert.NotNull(fact["provenance"]!["acceptedAt"]);
        }

        var payments = await GetAsync(ai.Owner, Url(projectId, $"elements/{Id("N1")}"));
        Assert.Equal(("Payments", "app", model.Shop, "Go", "Charges orders."),
            ((string)payments["name"]!, (string)payments["kind"]!, (Guid)payments["parentId"]!, (string)payments["technology"]!, (string)payments["description"]!));
        Inferred(payments, 0.9);
        var charge = (await GetAsync(ai.Owner, Url(projectId, "operations")))["items"]!.AsArray().Single(o => (Guid)o!["id"]! == Id("N2"))!;
        Assert.Equal(("POST /charges", Id("N1")), ((string)charge["name"]!, (Guid)charge["ownerId"]!));
        Inferred(charge, 0.8);
        var relationship = await GetAsync(ai.Owner, Url(projectId, $"relationships/{Id("N3")}"));
        Assert.Equal((model.Orders, Id("N1"), "Requests payment", "behavioural"),
            ((Guid)relationship["fromId"]!, (Guid)relationship["toId"]!, (string)relationship["label"]!, (string)relationship["kind"]!));
        Inferred(relationship, 0.7);
        var message = await GetAsync(ai.Owner, Url(projectId, $"messages/{Id("N6")}"));
        Assert.Equal(("PaymentTaken", Id("N1")), ((string)message["name"]!, (Guid)message["producerId"]!));
        Inferred(message, 0.6);

        // The rejected channel is not in the model, and what a person declared is as it was.
        Assert.Equal(["order-events", "order-events.dlq"], (await GetAsync(ai.Owner, Url(projectId, "channels")))["items"]!.AsArray().Select(c => (string)c!["name"]!));
        foreach (var declared in new[] { model.Orders, model.OrdersDb, model.Billing })
        {
            var element = await GetAsync(ai.Owner, Url(projectId, $"elements/{declared}"));
            Assert.Equal(("declared", null), ((string)element["factStatus"]!, element["provenance"]));
        }
        Assert.Equal("Orders", (string)(await GetAsync(ai.Owner, Url(projectId, $"elements/{model.Orders}")))["name"]!);
        Assert.Equal(7, (await GetAsync(ai.Owner, Url(projectId, "elements")))["items"]!.AsArray().Count);

        // A decided item does not change, and the subscription cannot follow its rejected channel.
        var again = await BodyAsync(await DecideAsync(ai.Owner, projectId, proposal, [], [Id("N1")]), HttpStatusCode.Conflict);
        Assert.Contains("accepted already", (string)again["failures"]![0]!["reason"]!);
        var orphan = await BodyAsync(await DecideAsync(ai.Owner, projectId, proposal, [Id("N5")]), HttpStatusCode.Conflict);
        Assert.Contains("rejected", (string)orphan["failures"]![0]!["reason"]!);

        var closed = await BodyAsync(await DecideAsync(ai.Owner, projectId, proposal, [], [Id("N5")]), HttpStatusCode.OK);
        Assert.Equal("decided", (string)closed["status"]!);
        Assert.NotNull(closed["decidedAt"]);

        var listed = await GetAsync(ai.Owner, Url(projectId, "ai/architecture-proposals"));
        Assert.Equal(((Guid)proposal["id"]!, "decided"), ((Guid)listed["items"]![0]!["id"]!, (string)listed["items"]![0]!["status"]!));
        var row = Assert.Single(await ai.RowsAsync());
        Assert.Equal(("inferArchitecture", "infer-architecture", DesignPrompts.InferArchitectureVersion), (row.Task, row.Purpose, row.PromptTemplateVersion));
    }

    [Fact]
    public async Task A_proposal_decided_in_parts_keeps_who_decided_each_item_and_when()
    {
        var (ai, projectId, _) = await OrdersAsync(_host, AiScenario.Kinds[0]);
        ai.Stub.Json(ai.Completion(Reply("architecture-payments")));
        var proposal = await BodyAsync(await ProposeArchitectureAsync(ai.Owner, projectId, OrdersRequest), HttpStatusCode.Created);
        Guid Id(string alias) => (Guid)proposal["items"]!.AsArray().Single(i => (string)i!["alias"]! == alias)!["id"]!;
        var editor = await TestHost.SignInMemberAsync(_host.Factory, ai.Owner, ai.WorkspaceId, "Editor");
        var ownerId = (string)(await ai.Owner.GetFromJsonAsync<JsonObject>("/api/auth/me"))!["id"]!;
        var editorId = (string)(await editor.GetFromJsonAsync<JsonObject>("/api/auth/me"))!["id"]!;

        // The undecided proposal is the owner's own, and nobody else's.
        var mine = await GetAsync(ai.Owner, Url(projectId, "ai/architecture-proposals?mine=true&decided=false"));
        Assert.Equal((Guid)proposal["id"]!, (Guid)Assert.Single(mine["items"]!.AsArray())!["id"]!);
        Assert.Empty((await GetAsync(editor, Url(projectId, "ai/architecture-proposals?mine=true")))["items"]!.AsArray());

        // An editor accepts one item; later the owner rejects the rest.
        await BodyAsync(await DecideAsync(editor, projectId, proposal, [Id("N1")]), HttpStatusCode.OK);
        var rest = proposal["items"]!.AsArray().Select(i => (Guid)i!["id"]!).Where(id => id != Id("N1")).ToArray();
        var closed = await BodyAsync(await DecideAsync(ai.Owner, projectId, proposal, [], rest), HttpStatusCode.OK);

        var items = closed["items"]!.AsArray().ToDictionary(i => (string)i!["alias"]!, i => i!);
        Assert.Equal(("accepted", editorId), ((string)items["N1"]["status"]!, (string)items["N1"]["decidedBy"]!));
        Assert.Equal(("rejected", ownerId), ((string)items["N4"]["status"]!, (string)items["N4"]["decidedBy"]!));
        Assert.All(items.Values, item => Assert.NotNull(item["decidedAt"]));
        Assert.True((DateTimeOffset)items["N1"]["decidedAt"]! <= (DateTimeOffset)items["N4"]["decidedAt"]!);
        Assert.Equal(("decided", ownerId), ((string)closed["status"]!, (string)closed["decidedBy"]!));

        // The history lists it with the decision in numbers, and it reads the same later.
        var history = Assert.Single((await GetAsync(ai.Owner, Url(projectId, "ai/architecture-proposals?decided=true")))["items"]!.AsArray())!;
        Assert.Equal((6, 1, 5, ownerId), ((int)history["items"]!, (int)history["acceptedItems"]!, (int)history["rejectedItems"]!, (string)history["decidedBy"]!));
        Assert.Empty((await GetAsync(ai.Owner, Url(projectId, "ai/architecture-proposals?decided=false")))["items"]!.AsArray());
        var read = await GetAsync(editor, Url(projectId, $"ai/architecture-proposals/{proposal["id"]}"));
        Assert.Equal(editorId, (string)read["items"]!.AsArray().Single(i => (string)i!["alias"]! == "N1")!["decidedBy"]!);
    }

    [Fact]
    public async Task An_addition_that_the_model_refuses_fails_the_whole_request()
    {
        var (ai, projectId, model) = await OrdersAsync(_host, "openai");
        ai.Stub.Json(ai.Completion(Reply("architecture-payments")));
        var proposal = await BodyAsync(await ProposeArchitectureAsync(ai.Owner, projectId, "Add payments."), HttpStatusCode.Created);
        var items = proposal["items"]!.AsArray().ToDictionary(i => (string)i!["alias"]!, i => (Guid)i!["id"]!);

        // The parent system goes away between the proposal and the decision.
        foreach (var path in new[] { $"subscriptions/{model.InventorySub}", $"subscriptions/{model.BillingSub}", $"operations/{model.PlaceOrder}" })
            Assert.Equal(HttpStatusCode.NoContent, (await ai.Owner.DeleteAsync(Url(projectId, path))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await ai.Owner.DeleteAsync(Url(projectId, $"elements/{model.Shop}"))).StatusCode);
        var versionBefore = await ModelVersionAsync(_host, projectId);

        var refused = await BodyAsync(await DecideAsync(ai.Owner, projectId, proposal, [items["N4"], items["N1"]]), HttpStatusCode.Conflict);

        var failure = Assert.Single(refused["failures"]!.AsArray())!;
        Assert.Equal(items["N1"], (Guid)failure["itemId"]!);
        Assert.Contains($"The parent of the item ({model.Shop}) is not in the model", (string)failure["reason"]!);
        // The channel of the same request was not written either.
        Assert.Equal(versionBefore, await ModelVersionAsync(_host, projectId));
        Assert.DoesNotContain((await GetAsync(ai.Owner, Url(projectId, "channels")))["items"]!.AsArray(), c => (string)c!["name"]! == "payment-events");
        Assert.All((await GetAsync(ai.Owner, Url(projectId, $"ai/architecture-proposals/{proposal["id"]}")))["items"]!.AsArray(),
            item => Assert.Equal("pending", (string)item!["status"]!));
    }

    [Theory, MemberData(nameof(Kinds))]
    public async Task An_explanation_streams_as_text_and_stores_nothing_but_its_usage(string kind)
    {
        var (ai, projectId, model) = await OrdersAsync(_host, kind);
        ai.Stub.Sse(ai.Fixture("stream.sse"));
        var versionBefore = await ModelVersionAsync(_host, projectId);

        var response = await ExplainAsync(ai.Owner, projectId, "element", model.Orders, "Why does Orders publish an event?");

        Assert.Equal((HttpStatusCode.OK, "text/event-stream"), (response.StatusCode, response.Content.Headers.ContentType!.MediaType));
        var events = Events(await response.Content.ReadAsStringAsync());
        Assert.Equal(["Hello", " world"], events.SkipLast(1).Select(e => (string)e["delta"]!));
        Assert.True((bool)events[^1]["done"]!);
        Assert.Equal(21, (int)events[^1]["usage"]!["inputTokens"]!);

        // The target and its neighbours went out as data, by name and without an ID.
        var sent = Text(Assert.Single(ai.Stub.Requests));
        Assert.Contains(DesignPrompts.DeclaredBehaviourRule, sent);
        Assert.Contains("\"operationsItServes\":[{\"name\":\"POST /orders\"", sent);
        Assert.Contains("\"messagesItProduces\":[\"OrderCreated\"]", sent);
        Assert.Contains("\"parent\":{\"name\":\"Shop\"", sent);
        Assert.Matches(@"<<<UNTRUSTED-DATA [0-9a-f]{16}\nWhy does Orders publish an event\?", sent);
        Assert.DoesNotContain(model.All, id => sent.Contains(id.ToString(), StringComparison.OrdinalIgnoreCase));

        var row = Assert.Single(await ai.RowsAsync());
        Assert.Equal(("explain", "explain", DesignPrompts.ExplainVersion, "succeeded"), (row.Task, row.Purpose, row.PromptTemplateVersion, row.Outcome));
        Assert.Equal(versionBefore, await ModelVersionAsync(_host, projectId));
        Assert.Empty((await GetAsync(ai.Owner, Url(projectId, "ai/flow-proposals")))["items"]!.AsArray());
    }

    [Theory, MemberData(nameof(Kinds))]
    public async Task A_stream_that_fails_after_text_ends_with_an_error_event(string kind)
    {
        var (ai, projectId, model) = await OrdersAsync(_host, kind);
        ai.Stub.Sse(ai.Fixture("stream-error.sse"));

        var response = await ExplainAsync(ai.Owner, projectId, "channel", model.OrderEvents);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var events = Events(await response.Content.ReadAsStringAsync());
        Assert.Equal("Hel", (string)events[0]["delta"]!);
        Assert.Equal("provider_error", (string)events[^1]["error"]!["code"]!);
        Assert.False(string.IsNullOrWhiteSpace((string)events[^1]["error"]!["message"]!));
        Assert.DoesNotContain(events, e => e.ContainsKey("done"));
        Assert.Equal(["provider_error"], (await ai.RowsAsync()).Select(r => r.Outcome));
    }

    [Fact]
    public async Task A_provider_that_fails_before_any_text_gives_the_failed_state_as_json()
    {
        var (ai, projectId, model) = await OrdersAsync(_host, "anthropic");
        ai.Stub.Json(HttpStatusCode.Unauthorized, ai.Fixture("error-401.json"));

        var response = await ExplainAsync(ai.Owner, projectId, "message", model.OrderCreated);

        Assert.Equal("application/json", response.Content.Headers.ContentType!.MediaType);
        var state = await BodyAsync(response, HttpStatusCode.OK);
        Assert.Equal(("failed", "provider_error"), ((string)state["status"]!, (string)state["reason"]!["code"]!));
    }

    [Fact]
    public async Task A_reader_who_leaves_ends_the_provider_call()
    {
        var (ai, projectId, model) = await OrdersAsync(_host, "openai");
        var whole = ai.Fixture("stream.sse");
        var body = new HangingStream(Encoding.UTF8.GetBytes(whole[..whole.IndexOf(" world", StringComparison.Ordinal)]));
        ai.Stub.Body(() => body, "text/event-stream");
        using var cancel = new CancellationTokenSource();

        using var request = new HttpRequestMessage(HttpMethod.Post, Url(projectId, "ai/explain"))
        {
            Content = JsonContent.Create(new { target = new { kind = "subscription", id = model.BillingSub } }),
        };
        using var response = await ai.Owner.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel.Token);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());
        Assert.Equal("data: {\"delta\":\"Hello\"}", await reader.ReadLineAsync());

        cancel.Cancel();
        response.Dispose();

        // The server sees the reader leave, stops reading the provider and closes the usage row.
        for (var i = 0; i < 100 && (await ai.RowsAsync()).SingleOrDefault()?.Outcome != "cancelled"; i++)
            await Task.Delay(100);
        Assert.Equal(["cancelled"], (await ai.RowsAsync()).Select(r => r.Outcome));
        Assert.True(body.Disposed);
    }

    [Fact]
    public async Task A_scenario_and_a_trace_event_are_explained_from_the_playback()
    {
        var (ai, projectId, model) = await OrdersAsync(_host, "gemini");
        ai.Stub.Json(ai.Completion(Reply("flow-orders"))).Sse(ai.Fixture("stream.sse"));
        var proposal = await BodyAsync(await ProposeFlowAsync(ai.Owner, projectId, OrdersRequest), HttpStatusCode.Created);
        var scenarioId = (Guid)(await BodyAsync(
            await ai.Owner.PostAsync(Url(projectId, $"ai/flow-proposals/{proposal["id"]}/accept"), null), HttpStatusCode.OK))["scenarioId"]!;
        var run = await BodyAsync(await ai.Owner.PostAsync(Url(projectId, $"scenarios/{scenarioId}/run"), null), HttpStatusCode.OK);
        var published = run["events"]!.AsArray().First(e => (string)e!["kind"]! == "Published")!;

        var scenario = await ExplainAsync(ai.Owner, projectId, "scenario", scenarioId);
        Assert.Equal(["Hello", " world"], Events(await scenario.Content.ReadAsStringAsync()).SkipLast(1).Select(e => (string)e["delta"]!));
        var aboutScenario = Text(ai.Stub.Requests[1]);
        Assert.Contains("\"type\":\"scenario\",\"name\":\"Place an order\"", aboutScenario);
        Assert.Contains("\"operation\":\"POST /orders\"", aboutScenario);
        Assert.Contains("\"slotKey\":\"Billing on order-events\"", aboutScenario);

        var traced = await ai.Owner.PostAsJsonAsync(Url(projectId, "ai/explain"),
            new { target = new { kind = "trace-event", scenarioId, sequence = (int)published["sequence"]! }, question = "What happens next?" });
        Assert.Equal(HttpStatusCode.OK, traced.StatusCode);
        Assert.True((bool)Events(await traced.Content.ReadAsStringAsync())[^1]["done"]!);
        var aboutEvent = Text(ai.Stub.Requests[2]);
        Assert.Contains("\"kind\":\"Published\",\"stepId\":\"publish\"", aboutEvent);
        Assert.Contains("\"from\":\"Orders\"", aboutEvent);
        Assert.Contains("\"step\":{\"id\":\"publish\"", aboutEvent);
        Assert.Contains("\"eventsAfter\":[{", aboutEvent);
        Assert.Contains(DesignPrompts.DeclaredBehaviourRule, aboutEvent);
        Assert.DoesNotContain(model.All.Append(scenarioId), id => (aboutScenario + aboutEvent).Contains(id.ToString(), StringComparison.OrdinalIgnoreCase));

        var noEvent = await ai.Owner.PostAsJsonAsync(Url(projectId, "ai/explain"), new { target = new { kind = "trace-event", scenarioId, sequence = 100_000 } });
        Assert.Equal(HttpStatusCode.NotFound, noEvent.StatusCode);
        Assert.Equal(3, ai.Stub.Requests.Count);
    }

    /// <summary>Gives its bytes, then waits until it is disposed.</summary>
    private sealed class HangingStream(byte[] bytes) : Stream
    {
        private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _position;

        public bool Disposed => _closed.Task.IsCompleted;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (_position == bytes.Length)
            {
                await _closed.Task.WaitAsync(ct);
                return 0;
            }

            var count = Math.Min(buffer.Length, bytes.Length - _position);
            bytes.AsMemory(_position, count).CopyTo(buffer);
            _position += count;
            return count;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count, default).GetAwaiter().GetResult();

        protected override void Dispose(bool disposing)
        {
            _closed.TrySetResult();
            base.Dispose(disposing);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
