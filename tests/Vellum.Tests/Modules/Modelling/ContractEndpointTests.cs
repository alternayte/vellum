using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Vellum.Modules.Drafts;
using Vellum.Modules.Modelling.Contracts;
using Vellum.Modules.Modelling.Variants;
using Vellum.Shared;

namespace Vellum.Tests.Modules.Modelling;

public class ContractEndpointTests(IntegrationFixture fixture) : IClassFixture<IntegrationFixture>
{
    private sealed record Model(
        Guid ProjectId, Guid Orders, Guid Inventory, Guid Schema,
        Guid PlaceOrder, Guid OrderEvents, Guid DeadLetters, Guid InventorySub);

    private static string Url(Guid projectId, string path, Guid? branchId = null) =>
        $"/api/projects/{projectId}/{path}" + (branchId is null ? "" : $"?branchId={branchId}");

    /// <summary>Two systems, a schema, one operation, two channels and one subscription with a dead-letter channel.</summary>
    private static async Task<Model> SeedAsync(HttpClient client, Guid projectId)
    {
        var model = new Model(projectId, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        await TestHost.Created(client.PostAsJsonAsync(Url(projectId, "elements"), new { id = model.Orders, kind = "system", name = "Orders" }));
        await TestHost.Created(client.PostAsJsonAsync(Url(projectId, "elements"), new { id = model.Inventory, kind = "system", name = "Inventory" }));
        await TestHost.Created(client.PostAsJsonAsync(Url(projectId, "schemas"),
            new { id = model.Schema, name = "OrderRequest", content = """{"type":"object"}""" }));
        await TestHost.Created(client.PostAsJsonAsync(Url(projectId, "operations"), new
        {
            id = model.PlaceOrder, ownerId = model.Orders, name = "POST /orders", transport = "http",
            requestSchemaId = model.Schema, timeoutMs = 2000, retry = new { maxAttempts = 3, backoffMs = 250 },
        }));
        await TestHost.Created(client.PostAsJsonAsync(Url(projectId, "channels"), new { id = model.OrderEvents, name = "order-events", kind = "topic" }));
        await TestHost.Created(client.PostAsJsonAsync(Url(projectId, "channels"), new { id = model.DeadLetters, name = "order-events.dlq", kind = "queue" }));
        await TestHost.Created(client.PostAsJsonAsync(Url(projectId, "subscriptions"), new
        {
            id = model.InventorySub, channelId = model.OrderEvents, consumerId = model.Inventory,
            retry = new { maxAttempts = 2, backoffMs = 100 }, deadLetterChannelId = model.DeadLetters,
        }));
        return model;
    }

    private static async Task<IReadOnlyList<T>> ListAsync<T>(HttpClient client, Guid projectId, string path, Guid? branchId = null) =>
        (await client.GetFromJsonAsync<Page<T>>(Url(projectId, path, branchId)))!.Items;

    private static async Task<string> ConflictAsync(Task<HttpResponseMessage> request)
    {
        var response = await request;
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ErrorResponse>())!.Title;
    }

    [Fact]
    public async Task Operations_are_created_listed_updated_and_deleted()
    {
        var factory = fixture.Factory;
        using var client = await TestHost.SignInAsync(factory);
        var (_, projectId) = await TestHost.CreateProjectAsync(client);
        var model = await SeedAsync(client, projectId);

        var raw = await client.GetStringAsync(Url(projectId, "operations"));
        Assert.Contains("\"transport\":\"http\"", raw);
        Assert.Contains("\"retry\":{\"maxAttempts\":3,\"backoffMs\":250}", raw);
        Assert.Contains("\"factStatus\":\"declared\"", raw);

        var created = Assert.Single(await ListAsync<OperationDto>(client, projectId, "operations"));
        Assert.Equal(new OperationDto(model.PlaceOrder, model.Orders, "POST /orders", "http", model.Schema, null, 2000, new RetryDto(3, 250), "declared", null), created);

        var patched = await client.PatchAsJsonAsync(Url(projectId, $"operations/{model.PlaceOrder}"), new
        {
            name = "POST /v2/orders", transport = "grpc", timeoutMs = 500, setRequestSchemaId = true,
            factStatus = "inferred", setProvenance = true,
            provenance = new { source = "repo@abc:src/Orders.cs:1-9", revision = "abc", confidence = 0.7 },
        });
        Assert.Equal(HttpStatusCode.OK, patched.StatusCode);
        var updated = (await patched.Content.ReadFromJsonAsync<OperationDto>())!;
        Assert.Equal(("POST /v2/orders", "grpc", 500, (Guid?)null, "inferred"),
            (updated.Name, updated.Transport, updated.TimeoutMs, updated.RequestSchemaId, updated.FactStatus));
        Assert.Equal(new RetryDto(3, 250), updated.Retry);
        Assert.Equal(0.7, updated.Provenance!.Confidence);
        Assert.NotNull(updated.Provenance.AcceptedBy);

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync(Url(projectId, $"operations/{model.PlaceOrder}"))).StatusCode);
        Assert.Empty(await ListAsync<OperationDto>(client, projectId, "operations"));
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync(Url(projectId, $"operations/{model.PlaceOrder}"))).StatusCode);
    }

    [Fact]
    public async Task Channels_and_subscriptions_are_created_listed_updated_and_deleted()
    {
        var factory = fixture.Factory;
        using var client = await TestHost.SignInAsync(factory);
        var (_, projectId) = await TestHost.CreateProjectAsync(client);
        var model = await SeedAsync(client, projectId);

        Assert.Equal(["order-events", "order-events.dlq"], (await ListAsync<ChannelDto>(client, projectId, "channels")).Select(c => c.Name));
        var renamed = await client.PatchAsJsonAsync(Url(projectId, $"channels/{model.OrderEvents}"), new { name = "orders", kind = "exchange" });
        var channel = (await renamed.Content.ReadFromJsonAsync<ChannelDto>())!;
        Assert.Equal(("orders", "exchange"), (channel.Name, channel.Kind));

        var subscription = Assert.Single(await ListAsync<SubscriptionDto>(client, projectId, "subscriptions"));
        Assert.Equal(new SubscriptionDto(model.InventorySub, model.OrderEvents, model.Inventory, new RetryDto(2, 100), model.DeadLetters, "declared", null), subscription);

        var cleared = await client.PatchAsJsonAsync(Url(projectId, $"subscriptions/{model.InventorySub}"),
            new { consumerId = model.Orders, setDeadLetterChannelId = true, retry = new { maxAttempts = 5, backoffMs = 0 } });
        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
        var updated = (await cleared.Content.ReadFromJsonAsync<SubscriptionDto>())!;
        Assert.Equal((model.Orders, (Guid?)null, new RetryDto(5, 0)), (updated.ConsumerId, updated.DeadLetterChannelId, updated.Retry));

        // The dead-letter channel is free now; the listened channel is not.
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync(Url(projectId, $"channels/{model.DeadLetters}"))).StatusCode);
        Assert.Contains("Orders on orders", await ConflictAsync(client.DeleteAsync(Url(projectId, $"channels/{model.OrderEvents}"))));

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync(Url(projectId, $"subscriptions/{model.InventorySub}"))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync(Url(projectId, $"channels/{model.OrderEvents}"))).StatusCode);
        Assert.Empty(await ListAsync<ChannelDto>(client, projectId, "channels"));
    }

    [Fact]
    public async Task Invalid_contracts_are_refused_with_400()
    {
        var factory = fixture.Factory;
        using var client = await TestHost.SignInAsync(factory);
        var (_, projectId) = await TestHost.CreateProjectAsync(client);
        var model = await SeedAsync(client, projectId);
        var (_, otherProjectId) = await TestHost.CreateProjectAsync(client);
        var foreignSchema = Guid.NewGuid();
        await TestHost.Created(client.PostAsJsonAsync(Url(otherProjectId, "schemas"),
            new { id = foreignSchema, name = "Foreign", content = """{"type":"object"}""" }));

        async Task<string> Refused(string path, object body, HttpMethod? method = null)
        {
            var response = await client.SendAsync(new HttpRequestMessage(method ?? HttpMethod.Post, Url(projectId, path)) { Content = JsonContent.Create(body) });
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            return (await response.Content.ReadFromJsonAsync<ErrorResponse>())!.Errors!.Single().Field;
        }

        object Operation(object overrides) => Merge(new { id = Guid.NewGuid(), ownerId = model.Orders, name = "GET /orders", transport = "http" }, overrides);

        Assert.Equal("transport", await Refused("operations", Operation(new { transport = "carrier-pigeon" })));
        Assert.Equal("transport", await Refused("operations", Operation(new { transport = "1" })));
        Assert.Equal("ownerId", await Refused("operations", Operation(new { ownerId = Guid.NewGuid() })));
        Assert.Equal("name", await Refused("operations", Operation(new { name = "" })));
        Assert.Equal("timeoutMs", await Refused("operations", Operation(new { timeoutMs = 3_600_001 })));
        Assert.Equal("retry.maxAttempts", await Refused("operations", Operation(new { retry = new { maxAttempts = 11, backoffMs = 0 } })));
        Assert.Equal("requestSchemaId", await Refused("operations", Operation(new { requestSchemaId = Guid.NewGuid() })));
        // A schema of another project does not count as existing.
        Assert.Equal("responseSchemaId", await Refused("operations", Operation(new { responseSchemaId = foreignSchema })));
        Assert.Equal("responseSchemaId", await Refused($"operations/{model.PlaceOrder}", new { responseSchemaId = foreignSchema }, HttpMethod.Patch));

        Assert.Equal("kind", await Refused("channels", new { id = Guid.NewGuid(), name = "x", kind = "stream" }));
        Assert.Equal("channelId", await Refused("subscriptions", new { id = Guid.NewGuid(), channelId = Guid.NewGuid(), consumerId = model.Orders }));
        Assert.Equal("consumerId", await Refused("subscriptions", new { id = Guid.NewGuid(), channelId = model.OrderEvents, consumerId = Guid.NewGuid() }));
        Assert.Equal("deadLetterChannelId", await Refused("subscriptions",
            new { id = Guid.NewGuid(), channelId = model.OrderEvents, consumerId = model.Orders, deadLetterChannelId = model.OrderEvents }));
        Assert.Equal("provenance", await Refused($"channels/{model.OrderEvents}", new { factStatus = "inferred" }, HttpMethod.Patch));

        // Nothing above was written.
        Assert.Single(await ListAsync<OperationDto>(client, projectId, "operations"));
        Assert.Single(await ListAsync<SubscriptionDto>(client, projectId, "subscriptions"));
    }

    private static JsonObject Merge(object defaults, object overrides)
    {
        var merged = System.Text.Json.JsonSerializer.SerializeToNode(defaults)!.AsObject();
        foreach (var (key, value) in System.Text.Json.JsonSerializer.SerializeToNode(overrides)!.AsObject())
            merged[key] = value?.DeepClone();
        return merged;
    }

    [Fact]
    public async Task A_referenced_element_channel_or_schema_cannot_be_removed()
    {
        var factory = fixture.Factory;
        using var client = await TestHost.SignInAsync(factory);
        var (_, projectId) = await TestHost.CreateProjectAsync(client);
        var model = await SeedAsync(client, projectId);

        Assert.Contains("operation 'POST /orders'", await ConflictAsync(client.DeleteAsync(Url(projectId, $"elements/{model.Orders}"))));
        Assert.Contains("subscription 'Inventory on order-events'", await ConflictAsync(client.DeleteAsync(Url(projectId, $"elements/{model.Inventory}"))));
        Assert.Contains(model.InventorySub.ToString(), await ConflictAsync(client.DeleteAsync(Url(projectId, $"channels/{model.OrderEvents}"))));
        Assert.Contains(model.InventorySub.ToString(), await ConflictAsync(client.DeleteAsync(Url(projectId, $"channels/{model.DeadLetters}"))));
        Assert.Contains("operation 'POST /orders'", await ConflictAsync(client.DeleteAsync(Url(projectId, $"schemas/{model.Schema}"))));

        // Everything is still there.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(Url(projectId, $"elements/{model.Orders}"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(Url(projectId, $"schemas/{model.Schema}"))).StatusCode);
        Assert.Equal(2, (await ListAsync<ChannelDto>(client, projectId, "channels")).Count);

        // Once the contracts are gone, the removals go through.
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync(Url(projectId, $"operations/{model.PlaceOrder}"))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync(Url(projectId, $"schemas/{model.Schema}"))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync(Url(projectId, $"elements/{model.Orders}"))).StatusCode);
    }

    [Fact]
    public async Task A_schema_that_only_a_draft_references_cannot_be_deleted()
    {
        var factory = fixture.Factory;
        using var client = await TestHost.SignInAsync(factory);
        var (_, projectId) = await TestHost.CreateProjectAsync(client);
        var orders = Guid.NewGuid();
        var schema = Guid.NewGuid();
        await TestHost.Created(client.PostAsJsonAsync(Url(projectId, "elements"), new { id = orders, kind = "system", name = "Orders" }));
        await TestHost.Created(client.PostAsJsonAsync(Url(projectId, "schemas"), new { id = schema, name = "Order", content = """{"type":"object"}""" }));
        var draft = await (await TestHost.Created(client.PostAsJsonAsync(Url(projectId, "drafts"), new { id = Guid.NewGuid(), name = "Checkout" })))
            .Content.ReadFromJsonAsync<DraftDto>();
        await TestHost.Created(client.PostAsJsonAsync(Url(projectId, "operations", draft!.StreamId),
            new { id = Guid.NewGuid(), ownerId = orders, name = "POST /checkout", transport = "http", responseSchemaId = schema }));

        var message = await ConflictAsync(client.DeleteAsync(Url(projectId, $"schemas/{schema}")));

        Assert.Contains("operation 'POST /checkout'", message);
        Assert.Contains("draft 'Checkout'", message);
    }

    [Fact]
    public async Task Contracts_of_one_project_are_not_reachable_through_another()
    {
        var factory = fixture.Factory;
        using var client = await TestHost.SignInAsync(factory);
        var (_, projectId) = await TestHost.CreateProjectAsync(client);
        var model = await SeedAsync(client, projectId);
        var (_, otherProjectId) = await TestHost.CreateProjectAsync(client);

        Assert.Empty(await ListAsync<OperationDto>(client, otherProjectId, "operations"));
        Assert.Empty(await ListAsync<ChannelDto>(client, otherProjectId, "channels"));
        Assert.Empty(await ListAsync<SubscriptionDto>(client, otherProjectId, "subscriptions"));

        Assert.Equal(HttpStatusCode.NotFound, (await client.PatchAsJsonAsync(Url(otherProjectId, $"operations/{model.PlaceOrder}"), new { name = "stolen" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync(Url(otherProjectId, $"operations/{model.PlaceOrder}"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PatchAsJsonAsync(Url(otherProjectId, $"channels/{model.OrderEvents}"), new { name = "stolen" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync(Url(otherProjectId, $"subscriptions/{model.InventorySub}"))).StatusCode);

        // A branch of one project is not a branch of another.
        var draft = await (await TestHost.Created(client.PostAsJsonAsync(Url(projectId, "drafts"), new { id = Guid.NewGuid(), name = "Draft" })))
            .Content.ReadFromJsonAsync<DraftDto>();
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(Url(otherProjectId, "operations", draft!.StreamId))).StatusCode);

        Assert.Equal("POST /orders", Assert.Single(await ListAsync<OperationDto>(client, projectId, "operations")).Name);
    }

    [Fact]
    public async Task A_viewer_reads_contracts_and_cannot_write_them()
    {
        var factory = fixture.Factory;
        using var owner = await TestHost.SignInAsync(factory);
        var (workspaceId, projectId) = await TestHost.CreateProjectAsync(owner);
        var model = await SeedAsync(owner, projectId);
        using var viewer = await TestHost.SignInMemberAsync(factory, owner, workspaceId, "Viewer");
        using var outsider = await TestHost.SignInAsync(factory);
        using var anonymous = factory.CreateClient();

        foreach (var path in new[] { "operations", "channels", "subscriptions" })
        {
            Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync(Url(projectId, path))).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await outsider.GetAsync(Url(projectId, path))).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(Url(projectId, path))).StatusCode);
        }

        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsJsonAsync(Url(projectId, "operations"),
            new { id = Guid.NewGuid(), ownerId = model.Orders, name = "x", transport = "http" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PatchAsJsonAsync(Url(projectId, $"operations/{model.PlaceOrder}"), new { name = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.DeleteAsync(Url(projectId, $"operations/{model.PlaceOrder}"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsJsonAsync(Url(projectId, "channels"), new { id = Guid.NewGuid(), name = "x", kind = "topic" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.DeleteAsync(Url(projectId, $"channels/{model.DeadLetters}"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsJsonAsync(Url(projectId, "subscriptions"),
            new { id = Guid.NewGuid(), channelId = model.OrderEvents, consumerId = model.Orders })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.DeleteAsync(Url(projectId, $"subscriptions/{model.InventorySub}"))).StatusCode);

        Assert.Single(await ListAsync<OperationDto>(owner, projectId, "operations"));
        Assert.Single(await ListAsync<SubscriptionDto>(owner, projectId, "subscriptions"));
    }

    [Fact]
    public async Task Drafts_and_variants_carry_contracts_and_change_them_apart_from_main()
    {
        var factory = fixture.Factory;
        using var client = await TestHost.SignInAsync(factory);
        var (_, projectId) = await TestHost.CreateProjectAsync(client);
        var model = await SeedAsync(client, projectId);

        var draft = await (await TestHost.Created(client.PostAsJsonAsync(Url(projectId, "drafts"), new { id = Guid.NewGuid(), name = "Draft" })))
            .Content.ReadFromJsonAsync<DraftDto>();
        var variant = await (await TestHost.Created(client.PostAsJsonAsync(Url(projectId, "variants"), new { id = Guid.NewGuid(), name = "Variant" })))
            .Content.ReadFromJsonAsync<VariantDto>();

        foreach (var branch in new[] { draft!.StreamId, variant!.StreamId })
        {
            Assert.Equal(await client.GetStringAsync(Url(projectId, "operations")), await client.GetStringAsync(Url(projectId, "operations", branch)));
            Assert.Equal(await client.GetStringAsync(Url(projectId, "channels")), await client.GetStringAsync(Url(projectId, "channels", branch)));
            Assert.Equal(await client.GetStringAsync(Url(projectId, "subscriptions")), await client.GetStringAsync(Url(projectId, "subscriptions", branch)));
        }

        var patched = await client.PatchAsJsonAsync(Url(projectId, $"operations/{model.PlaceOrder}", draft.StreamId), new { timeoutMs = 9000 });
        Assert.Equal(HttpStatusCode.OK, patched.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync(Url(projectId, $"subscriptions/{model.InventorySub}", variant.StreamId))).StatusCode);

        Assert.Equal(9000, Assert.Single(await ListAsync<OperationDto>(client, projectId, "operations", draft.StreamId)).TimeoutMs);
        Assert.Equal(2000, Assert.Single(await ListAsync<OperationDto>(client, projectId, "operations")).TimeoutMs);
        Assert.Empty(await ListAsync<SubscriptionDto>(client, projectId, "subscriptions", variant.StreamId));
        Assert.Single(await ListAsync<SubscriptionDto>(client, projectId, "subscriptions"));
    }

    [Fact]
    public async Task A_merge_brings_the_drafts_contracts_to_main()
    {
        var factory = fixture.Factory;
        using var client = await TestHost.SignInAsync(factory);
        var (_, projectId) = await TestHost.CreateProjectAsync(client);
        var model = await SeedAsync(client, projectId);

        var draftId = Guid.NewGuid();
        var draft = await (await TestHost.Created(client.PostAsJsonAsync(Url(projectId, "drafts"), new { id = draftId, name = "Billing" })))
            .Content.ReadFromJsonAsync<DraftDto>();
        var branch = draft!.StreamId;

        var billing = Guid.NewGuid();
        var charge = Guid.NewGuid();
        var payments = Guid.NewGuid();
        var billingSub = Guid.NewGuid();
        await TestHost.Created(client.PostAsJsonAsync(Url(projectId, "elements", branch), new { id = billing, kind = "system", name = "Billing" }));
        await TestHost.Created(client.PostAsJsonAsync(Url(projectId, "operations", branch),
            new { id = charge, ownerId = billing, name = "Charge", transport = "command", retry = new { maxAttempts = 4, backoffMs = 10 } }));
        await TestHost.Created(client.PostAsJsonAsync(Url(projectId, "channels", branch), new { id = payments, name = "payments", kind = "queue" }));
        await TestHost.Created(client.PostAsJsonAsync(Url(projectId, "subscriptions", branch),
            new { id = billingSub, channelId = payments, consumerId = billing, deadLetterChannelId = model.DeadLetters }));
        // An existing operation changes, with a new fact status, and an existing subscription goes.
        Assert.Equal(HttpStatusCode.OK, (await client.PatchAsJsonAsync(Url(projectId, $"operations/{model.PlaceOrder}", branch), new
        {
            timeoutMs = 750, factStatus = "observed-in-source", setProvenance = true,
            provenance = new { source = "repo@abc:src/Orders.cs:4", revision = "abc" },
        })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync(Url(projectId, $"subscriptions/{model.InventorySub}", branch))).StatusCode);

        var expectedOperations = await client.GetStringAsync(Url(projectId, "operations", branch));
        var expectedChannels = await client.GetStringAsync(Url(projectId, "channels", branch));
        var expectedSubscriptions = await client.GetStringAsync(Url(projectId, "subscriptions", branch));

        var preview = await (await client.PostAsync(Url(projectId, $"drafts/{draftId}/merge/preview"), null)).Content.ReadFromJsonAsync<JsonObject>();
        Assert.Empty(preview!["conflicts"]!.AsArray());
        Assert.Equal(
            ["channel:added", "element:added", "operation:added", "operation:modified", "subscription:added", "subscription:removed"],
            preview["autoResolved"]!.AsArray().Select(c => $"{c!["entityType"]}:{c["changeKind"]}").Order());

        var executed = await client.PostAsJsonAsync(Url(projectId, $"drafts/{draftId}/merge/execute"),
            new { resolutions = Array.Empty<object>(), expectedMainVersion = (int)preview["mainVersion"]! });
        Assert.Equal(HttpStatusCode.OK, executed.StatusCode);

        Assert.Equal(expectedOperations, await client.GetStringAsync(Url(projectId, "operations")));
        Assert.Equal(expectedChannels, await client.GetStringAsync(Url(projectId, "channels")));
        Assert.Equal(expectedSubscriptions, await client.GetStringAsync(Url(projectId, "subscriptions")));
    }

    [Fact]
    public async Task A_merge_that_would_leave_a_dangling_contract_is_refused()
    {
        var factory = fixture.Factory;
        using var client = await TestHost.SignInAsync(factory);
        var (_, projectId) = await TestHost.CreateProjectAsync(client);
        var payments = Guid.NewGuid();
        await TestHost.Created(client.PostAsJsonAsync(Url(projectId, "elements"), new { id = payments, kind = "system", name = "Payments" }));

        var draftId = Guid.NewGuid();
        var draft = await (await TestHost.Created(client.PostAsJsonAsync(Url(projectId, "drafts"), new { id = draftId, name = "Charge" })))
            .Content.ReadFromJsonAsync<DraftDto>();
        var charge = Guid.NewGuid();
        await TestHost.Created(client.PostAsJsonAsync(Url(projectId, "operations", draft!.StreamId),
            new { id = charge, ownerId = payments, name = "Charge", transport = "http" }));

        // Main removes the element the draft's operation belongs to.
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync(Url(projectId, $"elements/{payments}"))).StatusCode);

        var preview = await (await client.PostAsync(Url(projectId, $"drafts/{draftId}/merge/preview"), null)).Content.ReadFromJsonAsync<JsonObject>();
        var conflict = Assert.Single(preview!["conflicts"]!.AsArray(), c => (string)c!["entityType"]! == "operation");
        Assert.Equal("dangling_operation", (string)conflict!["conflictKind"]!);

        var resolutions = preview["conflicts"]!.AsArray()
            .Select(c => new { entityId = (Guid)c!["entityId"]!, resolution = (Guid)c["entityId"]! == charge ? "take_theirs" : "keep_ours" })
            .ToArray();
        var forced = await client.PostAsJsonAsync(Url(projectId, $"drafts/{draftId}/merge/execute"),
            new { resolutions, expectedMainVersion = (int)preview["mainVersion"]! });

        Assert.Contains("dangling reference", await ConflictAsync(Task.FromResult(forced)));
        Assert.Empty(await ListAsync<OperationDto>(client, projectId, "operations"));
        Assert.Equal("open", (await client.GetFromJsonAsync<DraftDto>(Url(projectId, $"drafts/{draftId}")))!.Status);
    }

    [Fact]
    public async Task Export_and_import_round_trip_contracts_deterministically()
    {
        var factory = fixture.Factory;
        using var client = await TestHost.SignInAsync(factory);
        var (_, projectId) = await TestHost.CreateProjectAsync(client);
        var model = await SeedAsync(client, projectId);
        Assert.Equal(HttpStatusCode.OK, (await client.PatchAsJsonAsync(Url(projectId, $"channels/{model.OrderEvents}"), new
        {
            factStatus = "inferred", setProvenance = true,
            provenance = new { source = "repo@abc:compose.yml:12", revision = "abc", confidence = 0.4 },
        })).StatusCode);

        var exported = await client.GetStringAsync(Url(projectId, "export") + "?format=json");
        Assert.Equal(exported, await client.GetStringAsync(Url(projectId, "export") + "?format=json"));
        var document = JsonNode.Parse(exported)!.AsObject();
        Assert.Equal("http", (string)document["operations"]![0]!["transport"]!);
        Assert.Equal(250, (int)document["operations"]![0]!["retry"]!["backoffMs"]!);
        Assert.Equal("inferred", (string)document["channels"]!.AsArray().Single(c => (Guid)c!["id"]! == model.OrderEvents)!["factStatus"]!);
        Assert.Equal(model.DeadLetters, (Guid)document["subscriptions"]![0]!["deadLetterChannelId"]!);

        // Importing a project's own export changes nothing.
        async Task<HttpResponseMessage> Import(Guid target, JsonNode body) => await client.PostAsync(
            Url(target, "import"), new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, (await Import(projectId, document)).StatusCode);
        Assert.Equal(exported, await client.GetStringAsync(Url(projectId, "export") + "?format=json"));

        // Into another project. A schema stream belongs to one project, so the copy carries none.
        var (_, targetProjectId) = await TestHost.CreateProjectAsync(client);
        var portable = document.DeepClone().AsObject();
        portable["schemas"] = new JsonArray();
        portable["operations"]![0]!["requestSchemaId"] = null;
        Assert.Equal(HttpStatusCode.OK, (await Import(targetProjectId, portable)).StatusCode);

        var copy = JsonNode.Parse(await client.GetStringAsync(Url(targetProjectId, "export") + "?format=json"))!.AsObject();
        foreach (var collection in new[] { "operations", "channels", "subscriptions" })
            Assert.Equal(portable[collection]!.ToJsonString(), copy[collection]!.ToJsonString());

        // The import refuses a contract that points at nothing, and writes none of the document.
        var (_, emptyProjectId) = await TestHost.CreateProjectAsync(client);
        var dangling = portable.DeepClone().AsObject();
        dangling["subscriptions"]![0]!["consumerId"] = Guid.NewGuid();
        var refused = await Import(emptyProjectId, dangling);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("Consumer element not found", await refused.Content.ReadAsStringAsync());
        Assert.Empty(await ListAsync<ChannelDto>(client, emptyProjectId, "channels"));

        // A document written before contracts existed imports with none.
        var older = portable.DeepClone().AsObject();
        older.Remove("operations");
        older.Remove("channels");
        older.Remove("subscriptions");
        Assert.Equal(HttpStatusCode.OK, (await Import(emptyProjectId, older)).StatusCode);
        Assert.Empty(await ListAsync<OperationDto>(client, emptyProjectId, "operations"));
        Assert.Equal(2, JsonNode.Parse(await client.GetStringAsync(Url(emptyProjectId, "export") + "?format=json"))!["elements"]!.AsArray().Count);
    }
}
