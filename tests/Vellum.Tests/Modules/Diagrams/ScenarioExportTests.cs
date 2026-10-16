using System.Collections.Immutable;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Vellum.Modules.Diagrams;
using Vellum.Modules.Modelling;
using Vellum.Modules.Promises;
using Vellum.Modules.Trace.Engine;

namespace Vellum.Tests.Modules.Diagrams;

/// <summary>
/// The export of a scenario as a sequence diagram: the Mermaid mapping of every kind of step
/// (call with onError, publish through a binding to two consumers, write, parallel, branch,
/// faults and assertions) is pinned line by line, hostile names stay inside their line, and
/// the SVG of the same sequence is well-formed and byte-stable.
/// </summary>
public class ScenarioExportTests
{
    private static Guid G(int n) => new(n, 0, 0, new byte[8]);

    private static readonly Guid Customer = G(1), Api = G(2), Db = G(3), Payments = G(4), Mailer = G(5), Fulfilment = G(6), Auditor = G(7);
    private static readonly Guid PlaceOrder = G(11), Charge = G(12);
    private static readonly Guid Orders = G(21), FulfilmentQueue = G(22), AuditQueue = G(23);
    private static readonly Guid MailerSub = G(31), FulfilmentSub = G(32), AuditSub = G(33);
    private static readonly Guid OrderPlaced = G(41);

    private static ElementState Element(Guid id, ElementKind kind, string name) =>
        new(id, kind, name, null, null, null, ElementStatus.Current, null, [], null, FactStatus.Declared);

    private static PinnedModel Model(string apiName = "Orders API", string chargeName = "Charge card") =>
        new(new ModelPin(Guid.Parse("11111111-1111-1111-1111-111111111111"), 42, null, null),
            ModelState.Initial with
            {
                Elements = new[]
                {
                    Element(Customer, ElementKind.Actor, "Customer"),
                    Element(Api, ElementKind.App, apiName),
                    Element(Db, ElementKind.Store, "Orders DB"),
                    Element(Payments, ElementKind.App, "Payments"),
                    Element(Mailer, ElementKind.App, "Mailer"),
                    Element(Fulfilment, ElementKind.App, "Fulfilment"),
                    Element(Auditor, ElementKind.App, "Auditor"),
                }.ToImmutableDictionary(e => e.Id),
                Messages = ImmutableDictionary<MessageId, MessageState>.Empty
                    .Add(OrderPlaced, new MessageState(OrderPlaced, "OrderPlaced", null, Api, [], null, [])),
                Operations = new[]
                {
                    new OperationState(PlaceOrder, Api, "Place order", Transport.Http, null, null, 0, RetryPolicy.None),
                    new OperationState(Charge, Payments, chargeName, Transport.Http, null, null, 0, RetryPolicy.None),
                }.ToImmutableDictionary(o => o.Id),
                Channels = new[]
                {
                    new ChannelState(Orders, "orders", ChannelKind.Topic),
                    new ChannelState(FulfilmentQueue, "fulfilment-queue", ChannelKind.Queue),
                    new ChannelState(AuditQueue, "audit-queue", ChannelKind.Queue),
                }.ToImmutableDictionary(c => c.Id),
                Subscriptions = new[]
                {
                    new SubscriptionState(MailerSub, Orders, Mailer, RetryPolicy.None, null),
                    new SubscriptionState(FulfilmentSub, FulfilmentQueue, Fulfilment, RetryPolicy.None, null),
                    new SubscriptionState(AuditSub, AuditQueue, Auditor, RetryPolicy.None, null),
                }.ToImmutableDictionary(s => s.Id),
                Bindings = new[]
                {
                    new BindingState(G(51), Orders, FulfilmentQueue, "order.*"),
                    // Its key does not match the step's routing key: no hop and no delivery.
                    new BindingState(G(52), Orders, AuditQueue, "refund.#"),
                }.ToImmutableDictionary(b => b.Id),
            });

    private static WriteStep Write(string id, Guid by, string dataset) => new(id, by, Db, dataset, null);

    private static Scenario Flow(string name = "Place an order") => new(
        G(99), name,
        [
            new CallStep("place", Customer, PlaceOrder, null, null,
                Then:
                [
                    Write("save", Api, "orders"),
                    new CallStep("charge", Api, Charge, null, null, [], OnError: [Write("record-failure", Api, "failed payments")]),
                    new PublishStep("announce", Api, Orders, OrderPlaced, null, new Dictionary<string, string>(),
                        new Dictionary<SubscriptionId, IReadOnlyList<Step>>
                        {
                            [FulfilmentSub] = [Write("ship", Fulfilment, "shipments")],
                            [AuditSub] = [Write("audit", Auditor, "audit")],
                        },
                        RoutingKey: "order.created"),
                ],
                OnError: []),
            new ParallelStep("fan-out", [[Write("log", Api, "audit")], [], [Write("ledger", Payments, "ledger")]]),
            new BranchStep("refund-if-failed", "charge", StepOutcome.Failed, [Write("refund", Api, "refunds")], []),
            new BranchStep("notify-unless-paid", "charge", StepOutcome.Succeeded, [], [Write("notify", Api, "notices")]),
        ],
        [new Fault(Charge, FaultKind.Timeout, 2)],
        [new OutcomeAssertion("a1", "charge", StepOutcome.Succeeded), new EventCountAssertion("a2", TraceEventKind.Delivered, null, 2)]);

    [Fact]
    public void Each_kind_of_step_maps_to_its_mermaid_line()
    {
        var text = MermaidSequence.Write(ScenarioSequence.Build(Flow(), 7, Model()));

        const string expected = """
            %% Vellum scenario "Place an order", Mermaid sequence diagram.
            %% From steps of scenario 00000063-0000-0000-0000-000000000000 at version 7 (a scenario is not part of a revision), names from stream 11111111-1111-1111-1111-111111111111 at version 42.
            %% One-way export. It drops: payloads, headers, routing keys, schemas, transports, timeouts, retry policies, dead-letter routes, the result of a playback (outcomes, attempts, timing and the routes that a fault closes), truth labels and their provenance, promises, costs and comments.
            sequenceDiagram
                actor customer as Customer
                participant orders_api as Orders API
                participant orders_db as Orders DB
                participant payments as Payments
                participant orders as orders #40;topic#41;
                participant fulfilment_queue as fulfilment-queue #40;queue#41;
                participant mailer as Mailer
                participant fulfilment as Fulfilment
                Note over payments: fault: timeout on the first 2 attempts at operation Charge card
                customer->>orders_api: Place order
                orders_api->>orders_db: orders
                orders_api->>payments: Charge card
                payments-->>orders_api: response
                alt Charge card fails #40;fault: timeout on the first 2 attempts#41;
                    orders_api->>orders_db: failed payments
                end
                orders_api-)orders: OrderPlaced
                orders-)fulfilment_queue: OrderPlaced
                par Mailer on orders
                    orders-)mailer: OrderPlaced
                and Fulfilment on fulfilment-queue
                    fulfilment_queue-)fulfilment: OrderPlaced
                    fulfilment->>orders_db: shipments
                end
                Note over orders_api: the handler of Auditor on audit-queue does not run: the message does not reach it
                orders_api-->>customer: response
                par branch 1
                    orders_api->>orders_db: audit
                and branch 3
                    payments->>orders_db: ledger
                end
                alt if charge failed
                    orders_api->>orders_db: refunds
                end
                alt if charge did not succeed
                    orders_api->>orders_db: notices
                end
                Note over customer,fulfilment: assert a1: charge succeeded
                Note over customer,fulfilment: assert a2: 2 Delivered events

            """;
        Assert.Equal(expected.ReplaceLineEndings("\n"), text);
    }

    [Fact]
    public void Unknown_references_are_named_and_hostile_names_stay_on_their_line()
    {
        const string hostile = "x; end\n%%{init: {\"theme\":\"dark\"}}%% <b>#1</b>";
        var unknownOperation = G(77);
        var scenario = new Scenario(G(98), hostile,
            [
                new CallStep("a", Api, unknownOperation, null, null, [], []),
                new CallStep("b", Customer, Charge, null, null, [], []),
                new WriteStep("c", G(88), Db, hostile, null),
            ],
            [], []);
        var text = MermaidSequence.Write(ScenarioSequence.Build(scenario, 1, Model(apiName: hostile, chargeName: hostile)));
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.All(lines.Take(3), l => Assert.StartsWith("%% ", l));
        Assert.DoesNotContain("%%{", text);
        Assert.Equal("sequenceDiagram", lines[3]);
        // Five participants, a note and three arrows: a name adds no line, no statement and no markup.
        Assert.Equal(13, lines.Length);
        var body = System.Text.RegularExpressions.Regex.Replace(string.Join('\n', lines.Skip(4)), "#[a-z0-9]+;", "");
        Assert.DoesNotContain(";", body);
        Assert.DoesNotContain("<", body);
        Assert.DoesNotContain("%", body);
        Assert.Contains(": calls unknown operation 0000004d\n", text);
        Assert.Contains("participant unknown_element_00000058 as unknown element 00000058", body);
    }

    [Fact]
    public void The_svg_draws_the_same_sequence_and_has_fixed_bytes()
    {
        var sequence = ScenarioSequence.Build(Flow("<script>alert(1)</script>"), 7, Model());
        var first = SequenceSvg.Write(sequence);
        Assert.Equal(first, SequenceSvg.Write(ScenarioSequence.Build(Flow("<script>alert(1)</script>"), 7, Model())));

        var svg = XDocument.Parse(first);
        Assert.DoesNotContain(svg.Descendants(), e => e.Name.LocalName == "script");
        Assert.Equal(8, svg.Descendants().Count(e => (string?)e.Attribute("class") is { } c && c.StartsWith("participant ", StringComparison.Ordinal)));
        Assert.Equal(8, svg.Descendants().Count(e => (string?)e.Attribute("class") == "lifeline"));
        // Every arrow and every block of the Mermaid export, and the same notes.
        Assert.Equal(15, svg.Descendants().Count(e => (string?)e.Attribute("class") is { } c && c.StartsWith("message ", StringComparison.Ordinal)));
        Assert.Equal(5, svg.Descendants().Count(e => (string?)e.Attribute("class") is { } c && c.StartsWith("block ", StringComparison.Ordinal)));
        Assert.Equal(4, svg.Descendants().Count(e => (string?)e.Attribute("class") == "note"));
        Assert.Contains("<script>", svg.Root!.Elements().First(e => e.Name.LocalName == "title").Value);
        Assert.Contains("version 7", svg.Root.Elements().First(e => e.Name.LocalName == "desc").Value);
    }
}

/// <summary>The scenario export endpoints against a real database.</summary>
public class ScenarioExportEndpointTests(IntegrationFixture fixture) : IClassFixture<IntegrationFixture>
{
    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Vellum.slnx")))
                return directory.FullName;
        }
        throw new InvalidOperationException("Vellum.slnx was not found above the test directory.");
    }

    private static StringContent Json(string text) => new(text, Encoding.UTF8, "application/json");

    [Fact]
    public async Task A_scenario_of_the_orders_example_exports_and_a_revision_keeps_its_names()
    {
        var client = await TestHost.SignInAsync(fixture.Factory);
        var (workspaceId, projectId) = await TestHost.CreateProjectAsync(client);
        string Url(string path) => $"/api/projects/{projectId}/{path}";

        var directory = Path.Combine(RepositoryRoot(), "examples", "orders");
        var model = await File.ReadAllTextAsync(Path.Combine(directory, "model.json"));
        var imported = await client.PostAsync(Url("import"), Json(model));
        Assert.True(imported.StatusCode == HttpStatusCode.OK, await imported.Content.ReadAsStringAsync());
        var document = await File.ReadAllTextAsync(Path.Combine(directory, "scenarios", "place-an-order.json"));
        var scenarioId = JsonNode.Parse(document)!["id"]!.GetValue<Guid>();
        await TestHost.Created(client.PostAsync(Url("scenarios"), Json(document)));

        var revision = Guid.NewGuid();
        await TestHost.Created(client.PostAsync(Url("revisions"), Json($$"""{"id":"{{revision}}","name":"Review 1"}""")));

        var mermaid = await client.GetAsync(Url($"scenarios/{scenarioId}/export/mermaid?revisionId={revision}"));
        Assert.Equal(HttpStatusCode.OK, mermaid.StatusCode);
        Assert.Equal("text/plain", mermaid.Content.Headers.ContentType?.MediaType);
        var pinned = await mermaid.Content.ReadAsStringAsync();
        Assert.Contains("\nsequenceDiagram\n", pinned);
        Assert.Contains("revision \"Review 1\"", pinned);
        Assert.Contains("Orders API", pinned);

        var svg = await client.GetAsync(Url($"scenarios/{scenarioId}/export/svg"));
        Assert.Equal("image/svg+xml", svg.Content.Headers.ContentType?.MediaType);
        XDocument.Parse(await svg.Content.ReadAsStringAsync());

        // A rename on main changes the main export and not the export of the revision.
        var elements = JsonNode.Parse(model)!["elements"]!.AsArray();
        var api = elements.First(e => e!["name"]!.GetValue<string>() == "Orders API")!["id"]!.GetValue<Guid>();
        var renamed = await client.PatchAsync(Url($"elements/{api}"), Json("""{"name":"Order intake"}"""));
        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
        Assert.Equal(pinned, await client.GetStringAsync(Url($"scenarios/{scenarioId}/export/mermaid?revisionId={revision}")));
        Assert.Contains("Order intake", await client.GetStringAsync(Url($"scenarios/{scenarioId}/export/mermaid")));

        // A Viewer reads both exports; an unknown scenario or revision is refused.
        var viewer = await TestHost.SignInMemberAsync(fixture.Factory, client, workspaceId, "Viewer");
        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync(Url($"scenarios/{scenarioId}/export/svg"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(Url($"scenarios/{Guid.NewGuid()}/export/mermaid"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.GetAsync(Url($"scenarios/{scenarioId}/export/mermaid?revisionId={Guid.NewGuid()}"))).StatusCode);
    }
}
