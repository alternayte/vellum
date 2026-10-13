using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Deedbox.Testing;
using Vellum.Modules.Drafts;
using Vellum.Modules.Drafts.Merge;
using Vellum.Modules.Modelling.Model;
using Vellum.Modules.Trace.Engine;
using Vellum.Shared;
using Decision = Vellum.Shared.CommandResult<System.Collections.Generic.IReadOnlyList<object>>;

namespace Vellum.Tests.Modules.Modelling;

/// <summary>
/// Within one model an ID names one entity of any kind. A fault or an assertion names its
/// target by ID alone, and a trace labels events by ID, so a second entity with the same ID
/// would make both ambiguous. The empty GUID is what a request without an ID reads as.
/// </summary>
public class OneIdPerEntityDeciderTests
{
    private static readonly ElementId Shop = G(1), Orders = G(2);
    private static readonly RelationshipId Uses = G(3);
    private static readonly MessageId OrderPlaced = G(4);
    private static readonly OperationId PlaceOrder = G(5);
    private static readonly ChannelId OrderEvents = G(6);
    private static readonly SubscriptionId OrdersSub = G(7);

    private static Guid G(int n) => new($"00000000-0000-0000-0000-{n:D12}");

    /// <summary>One entity of each of the six kinds.</summary>
    private static ModelState Model() => Decider.Given<ModelState>(
        new ModelEvent.ElementAdded(Shop, ElementKind.System, "Shop", null, null, null, ElementStatus.Current, null, []),
        new ModelEvent.ElementAdded(Orders, ElementKind.System, "Orders", null, null, null, ElementStatus.Current, null, []),
        new ModelEvent.RelationshipAdded(Uses, Shop, Orders, "uses", null, null),
        new ModelEvent.MessageAdded(OrderPlaced, "OrderPlaced", null, Orders, [], null, []),
        new ModelEvent.OperationAdded(PlaceOrder, Orders, "POST /orders", Transport.Http, null, null, 0, RetryPolicy.None),
        new ModelEvent.ChannelAdded(OrderEvents, "order-events", ChannelKind.Topic),
        new ModelEvent.SubscriptionAdded(OrdersSub, OrderEvents, Orders, RetryPolicy.None, null)).State;

    /// <summary>Every way to add an entity, with the ID it would take.</summary>
    private static Dictionary<string, Func<ModelState, Guid, Decision>> Adds() => new()
    {
        ["element"] = (state, id) => ModelDecider.AddElement(state,
            new AddElementCommand(id, ElementKind.System, "New", null, null, null, ElementStatus.Current, null, [])),
        ["relationship"] = (state, id) => ModelDecider.AddRelationship(state,
            new AddRelationshipCommand(id, Shop, Orders, null, null)),
        ["message"] = (state, id) => ModelDecider.AddMessage(state,
            new AddMessageCommand(id, "New", null, Orders, [], null, [])),
        ["operation"] = (state, id) => ContractDecider.AddOperation(state,
            new AddOperationCommand(id, Orders, "New", Transport.Http, null, null, 0, RetryPolicy.None)),
        ["channel"] = (state, id) => ContractDecider.AddChannel(state,
            new AddChannelCommand(id, "new", ChannelKind.Queue)),
        ["subscription"] = (state, id) => ContractDecider.AddSubscription(state,
            new AddSubscriptionCommand(id, OrderEvents, Shop, RetryPolicy.None, null)),
    };

    [Fact]
    public void No_kind_of_entity_can_take_an_ID_that_any_kind_already_has()
    {
        var model = Model();
        Guid[] taken = [Shop, Uses, OrderPlaced, PlaceOrder, OrderEvents, OrdersSub];

        foreach (var (kind, add) in Adds())
        {
            foreach (var id in taken)
            {
                var conflict = Assert.IsType<Decision.Conflict>(add(model, id));
                Assert.Contains(id.ToString(), conflict.Message);
            }

            Assert.True(add(model, G(99)) is Decision.Success, $"A new {kind} with a free ID is added");
        }
    }

    [Fact]
    public void The_empty_GUID_names_nothing()
    {
        foreach (var (_, add) in Adds())
        {
            var invalid = Assert.IsType<Decision.Invalid>(add(Model(), Guid.Empty));
            Assert.Equal("id", Assert.Single(invalid.Errors).Field);
        }
    }

    [Fact]
    public void A_merge_that_would_give_one_ID_to_two_kinds_is_refused()
    {
        // The draft added an element with the ID that main, meanwhile, gave to a channel.
        var main = Model();
        var fromDraft = new ModelEvent.ElementAdded(OrderEvents.Value, ElementKind.System, "Clash", null, null, null, ElementStatus.Current, null, []);

        var refusal = MergedModel.Refusal(main, [fromDraft]);

        Assert.NotNull(refusal);
        Assert.Contains("a channel", refusal);
        Assert.Null(MergedModel.Refusal(main, [fromDraft with { Id = G(99) }]));
    }
}

[Collection("Integration")]
public class OneIdPerEntityEndpointTests(IntegrationFixture fixture)
{
    private static string Url(Guid projectId, string path, Guid? branchId = null) =>
        $"/api/projects/{projectId}/{path}" + (branchId is null ? "" : $"?branchId={branchId}");

    [Fact]
    public async Task An_ID_that_a_channel_has_is_refused_for_an_element_a_relationship_and_a_message()
    {
        using var factory = TestHost.Create(fixture);
        using var client = await TestHost.SignInAsync(factory);
        var (_, projectId) = await TestHost.CreateProjectAsync(client);

        Guid orders = Guid.NewGuid(), billing = Guid.NewGuid(), shared = Guid.NewGuid();
        await TestHost.Created(client.PostAsJsonAsync(Url(projectId, "elements"), new { id = orders, kind = "system", name = "Orders" }));
        await TestHost.Created(client.PostAsJsonAsync(Url(projectId, "elements"), new { id = billing, kind = "system", name = "Billing" }));
        await TestHost.Created(client.PostAsJsonAsync(Url(projectId, "channels"), new { id = shared, name = "order-events", kind = "topic" }));

        var element = await client.PostAsJsonAsync(Url(projectId, "elements"), new { id = shared, kind = "system", name = "Clash" });
        var relationship = await client.PostAsJsonAsync(Url(projectId, "relationships"), new { id = shared, fromId = orders, toId = billing });
        var message = await client.PostAsJsonAsync(Url(projectId, "messages"), new { id = shared, name = "Clash", producerId = orders, consumerIds = Array.Empty<Guid>() });

        foreach (var response in new[] { element, relationship, message })
        {
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Contains("a channel", (await response.Content.ReadFromJsonAsync<ErrorResponse>())!.Title);
        }

        // And the other way round: a contract cannot take the ID of an element.
        var operation = await client.PostAsJsonAsync(Url(projectId, "operations"), new { id = orders, ownerId = orders, name = "Clash", transport = "http" });
        Assert.Equal(HttpStatusCode.Conflict, operation.StatusCode);
    }

    [Theory]
    [InlineData("elements", """{ "kind": "system", "name": "No ID" }""")]
    [InlineData("relationships", """{ "fromId": "FROM", "toId": "TO" }""")]
    [InlineData("messages", """{ "name": "No ID", "producerId": "FROM", "consumerIds": [] }""")]
    [InlineData("operations", """{ "ownerId": "FROM", "name": "No ID", "transport": "http" }""")]
    [InlineData("channels", """{ "name": "no-id", "kind": "topic" }""")]
    [InlineData("subscriptions", """{ "channelId": "CHANNEL", "consumerId": "FROM" }""")]
    public async Task A_create_without_an_ID_is_a_400_that_names_the_id(string path, string body)
    {
        using var factory = TestHost.Create(fixture);
        using var client = await TestHost.SignInAsync(factory);
        var (_, projectId) = await TestHost.CreateProjectAsync(client);

        Guid from = Guid.NewGuid(), to = Guid.NewGuid(), channel = Guid.NewGuid();
        await TestHost.Created(client.PostAsJsonAsync(Url(projectId, "elements"), new { id = from, kind = "system", name = "From" }));
        await TestHost.Created(client.PostAsJsonAsync(Url(projectId, "elements"), new { id = to, kind = "system", name = "To" }));
        await TestHost.Created(client.PostAsJsonAsync(Url(projectId, "channels"), new { id = channel, name = "c", kind = "topic" }));

        var json = body.Replace("FROM", from.ToString()).Replace("TO", to.ToString()).Replace("CHANNEL", channel.ToString());
        var response = await client.PostAsync(Url(projectId, path), new StringContent(json, System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = (await response.Content.ReadFromJsonAsync<ErrorResponse>())!;
        Assert.Equal("id", Assert.Single(error.Errors!).Field);
    }

    [Fact]
    public async Task An_import_that_gives_one_ID_to_two_kinds_is_refused_and_writes_nothing()
    {
        using var factory = TestHost.Create(fixture);
        using var client = await TestHost.SignInAsync(factory);
        var (_, projectId) = await TestHost.CreateProjectAsync(client);

        var shared = Guid.NewGuid();
        var document = new JsonObject
        {
            ["version"] = "1.0",
            ["project"] = new JsonObject { ["name"] = "Clash" },
            ["elements"] = new JsonArray(new JsonObject
            {
                ["id"] = shared, ["kind"] = "system", ["name"] = "Orders", ["status"] = "current", ["tags"] = new JsonArray(),
            }),
            ["relationships"] = new JsonArray(),
            ["messages"] = new JsonArray(),
            ["schemas"] = new JsonArray(),
            ["channels"] = new JsonArray(new JsonObject { ["id"] = shared, ["name"] = "order-events", ["kind"] = "topic" }),
        };

        var response = await client.PostAsJsonAsync(Url(projectId, "import"), document);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("an element", (await response.Content.ReadFromJsonAsync<ErrorResponse>())!.Title);
        Assert.Empty((await client.GetFromJsonAsync<JsonObject>(Url(projectId, "elements")))!["items"]!.AsArray());
    }

    [Fact]
    public async Task A_merge_that_would_give_one_ID_to_two_kinds_is_a_conflict()
    {
        using var factory = TestHost.Create(fixture);
        using var client = await TestHost.SignInAsync(factory);
        var (_, projectId) = await TestHost.CreateProjectAsync(client);

        var draft = (await (await TestHost.Created(client.PostAsJsonAsync(Url(projectId, "drafts"), new { id = Guid.NewGuid(), name = "Clash" })))
            .Content.ReadFromJsonAsync<DraftDto>())!;

        // The same ID: an element on the draft, a channel on main.
        var shared = Guid.NewGuid();
        await TestHost.Created(client.PostAsJsonAsync(Url(projectId, "elements", draft.StreamId), new { id = shared, kind = "system", name = "On the draft" }));
        await TestHost.Created(client.PostAsJsonAsync(Url(projectId, "channels"), new { id = shared, name = "on-main", kind = "topic" }));

        var preview = (await (await client.PostAsync(Url(projectId, $"drafts/{draft.Id}/merge/preview"), null)).Content.ReadFromJsonAsync<JsonObject>())!;
        var merge = await client.PostAsJsonAsync(Url(projectId, $"drafts/{draft.Id}/merge/execute"),
            new { resolutions = Array.Empty<object>(), expectedMainVersion = (int)preview["mainVersion"]! });

        Assert.Equal(HttpStatusCode.Conflict, merge.StatusCode);
        Assert.Contains("a channel", (await merge.Content.ReadFromJsonAsync<ErrorResponse>())!.Title);
        Assert.Empty((await client.GetFromJsonAsync<JsonObject>(Url(projectId, "elements")))!["items"]!.AsArray());
    }
}
