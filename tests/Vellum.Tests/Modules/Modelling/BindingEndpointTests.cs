using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Vellum.Modules.Drafts;
using Vellum.Modules.Modelling;
using Vellum.Shared;

namespace Vellum.Tests.Modules.Modelling;

public class BindingEndpointTests(IntegrationFixture fixture) : IClassFixture<IntegrationFixture>
{
    private static string Url(Guid projectId, string path, Guid? branchId = null) =>
        $"/api/projects/{projectId}/{path}" + (branchId is null ? "" : $"?branchId={branchId}");

    private static async Task<IReadOnlyList<T>> ListAsync<T>(HttpClient client, Guid projectId, string path, Guid? branchId = null) =>
        (await client.GetFromJsonAsync<Page<T>>(Url(projectId, path, branchId)))!.Items;

    private static async Task<string> RefusedAsync(Task<HttpResponseMessage> request, HttpStatusCode status)
    {
        var response = await request;
        Assert.Equal(status, response.StatusCode);
        return await response.Content.ReadAsStringAsync();
    }

    [Fact]
    public async Task Bindings_are_created_listed_updated_and_deleted_and_hold_their_channels()
    {
        var factory = fixture.Factory;
        using var client = await TestHost.SignInAsync(factory);
        var (_, projectId) = await TestHost.CreateProjectAsync(client);
        Guid exchange = Guid.NewGuid(), queue = Guid.NewGuid(), other = Guid.NewGuid(), binding = Guid.NewGuid();
        await TestHost.Created(client.PostAsJsonAsync(Url(projectId, "channels"), new { id = exchange, name = "order-events", kind = "exchange" }));
        await TestHost.Created(client.PostAsJsonAsync(Url(projectId, "channels"), new { id = queue, name = "billing.order-created", kind = "queue" }));
        await TestHost.Created(client.PostAsJsonAsync(Url(projectId, "channels"), new { id = other, name = "inventory.order-created", kind = "queue" }));

        var created = await TestHost.Created(client.PostAsJsonAsync(Url(projectId, "bindings"),
            new { id = binding, sourceChannelId = exchange, targetChannelId = queue, routingKey = "" }));
        Assert.Equal(new BindingDto(binding, exchange, queue, null, "declared", null), await created.Content.ReadFromJsonAsync<BindingDto>());
        Assert.Equal(binding, Assert.Single(await ListAsync<BindingDto>(client, projectId, "bindings")).Id);

        // The same route again, a queue as the source, and a channel that is not there.
        await RefusedAsync(client.PostAsJsonAsync(Url(projectId, "bindings"),
            new { id = Guid.NewGuid(), sourceChannelId = exchange, targetChannelId = queue }), HttpStatusCode.Conflict);
        Assert.Contains("exchange or a topic", await RefusedAsync(client.PostAsJsonAsync(Url(projectId, "bindings"),
            new { id = Guid.NewGuid(), sourceChannelId = queue, targetChannelId = other }), HttpStatusCode.BadRequest));
        await RefusedAsync(client.PostAsJsonAsync(Url(projectId, "bindings"),
            new { id = Guid.NewGuid(), sourceChannelId = exchange, targetChannelId = Guid.NewGuid() }), HttpStatusCode.BadRequest);

        var patched = await client.PatchAsJsonAsync(Url(projectId, $"bindings/{binding}"), new
        {
            targetChannelId = other, routingKey = "order.*", factStatus = "inferred", setProvenance = true,
            provenance = new { source = "ai:test/model#architecture-proposal:1", confidence = 0.5 },
        });
        Assert.Equal(HttpStatusCode.OK, patched.StatusCode);
        var updated = (await patched.Content.ReadFromJsonAsync<BindingDto>())!;
        Assert.Equal((other, (string?)"order.*", "inferred", (double?)0.5), (updated.TargetChannelId, updated.RoutingKey, updated.FactStatus, updated.Provenance!.Confidence));
        var cleared = await (await client.PatchAsJsonAsync(Url(projectId, $"bindings/{binding}"), new { setRoutingKey = true }))
            .Content.ReadFromJsonAsync<BindingDto>();
        Assert.Null(cleared!.RoutingKey);

        // A channel that the binding names stays, and cannot take a kind the binding does not allow.
        Assert.Contains(binding.ToString(), await RefusedAsync(client.DeleteAsync(Url(projectId, $"channels/{exchange}")), HttpStatusCode.Conflict));
        Assert.Contains(binding.ToString(), await RefusedAsync(client.DeleteAsync(Url(projectId, $"channels/{other}")), HttpStatusCode.Conflict));
        await RefusedAsync(client.PatchAsJsonAsync(Url(projectId, $"channels/{exchange}"), new { kind = "queue" }), HttpStatusCode.Conflict);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync(Url(projectId, $"channels/{queue}"))).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync(Url(projectId, $"bindings/{binding}"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync(Url(projectId, $"bindings/{binding}"))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync(Url(projectId, $"channels/{exchange}"))).StatusCode);
    }

    [Fact]
    public async Task A_draft_carries_bindings_a_merge_brings_them_to_main_and_an_export_round_trips_them()
    {
        var factory = fixture.Factory;
        using var client = await TestHost.SignInAsync(factory);
        var (_, projectId) = await TestHost.CreateProjectAsync(client);
        Guid exchange = Guid.NewGuid(), queue = Guid.NewGuid(), other = Guid.NewGuid(), first = Guid.NewGuid(), second = Guid.NewGuid();
        await TestHost.Created(client.PostAsJsonAsync(Url(projectId, "channels"), new { id = exchange, name = "order-events", kind = "exchange" }));
        await TestHost.Created(client.PostAsJsonAsync(Url(projectId, "channels"), new { id = queue, name = "billing.order-created", kind = "queue" }));
        await TestHost.Created(client.PostAsJsonAsync(Url(projectId, "bindings"), new { id = first, sourceChannelId = exchange, targetChannelId = queue }));

        var draftId = Guid.NewGuid();
        var draft = await (await TestHost.Created(client.PostAsJsonAsync(Url(projectId, "drafts"), new { id = draftId, name = "Inventory" })))
            .Content.ReadFromJsonAsync<DraftDto>();
        var branch = draft!.StreamId;
        Assert.Equal(await client.GetStringAsync(Url(projectId, "bindings")), await client.GetStringAsync(Url(projectId, "bindings", branch)));

        await TestHost.Created(client.PostAsJsonAsync(Url(projectId, "channels", branch), new { id = other, name = "inventory.order-created", kind = "queue" }));
        await TestHost.Created(client.PostAsJsonAsync(Url(projectId, "bindings", branch),
            new { id = second, sourceChannelId = exchange, targetChannelId = other, routingKey = "order.created" }));
        Assert.Equal(HttpStatusCode.OK, (await client.PatchAsJsonAsync(Url(projectId, $"bindings/{first}", branch), new { routingKey = "order.#" })).StatusCode);
        Assert.Single(await ListAsync<BindingDto>(client, projectId, "bindings"));
        var expected = await client.GetStringAsync(Url(projectId, "bindings", branch));

        var preview = await (await client.PostAsync(Url(projectId, $"drafts/{draftId}/merge/preview"), null)).Content.ReadFromJsonAsync<JsonObject>();
        Assert.Empty(preview!["conflicts"]!.AsArray());
        Assert.Equal(["binding:added", "binding:modified", "channel:added"],
            preview["autoResolved"]!.AsArray().Select(c => $"{c!["entityType"]}:{c["changeKind"]}").Order());
        var executed = await client.PostAsJsonAsync(Url(projectId, $"drafts/{draftId}/merge/execute"),
            new { resolutions = Array.Empty<object>(), expectedMainVersion = (int)preview["mainVersion"]! });
        Assert.Equal(HttpStatusCode.OK, executed.StatusCode);
        Assert.Equal(expected, await client.GetStringAsync(Url(projectId, "bindings")));

        // The export holds the bindings, in ID order, and importing it changes nothing.
        var exported = await client.GetStringAsync(Url(projectId, "export") + "?format=json");
        var document = JsonNode.Parse(exported)!.AsObject();
        Assert.Equal(new[] { first, second }.Order(), document["bindings"]!.AsArray().Select(b => (Guid)b!["id"]!));
        Assert.Equal("order.created", (string)document["bindings"]!.AsArray().Single(b => (Guid)b!["id"]! == second)!["routingKey"]!);
        async Task<HttpResponseMessage> Import(Guid target, JsonNode body, string type = "application/json") => await client.PostAsync(
            Url(target, "import"), new StringContent(type == "application/json" ? body.ToJsonString() : body.GetValue<string>(), Encoding.UTF8, type));
        Assert.Equal(HttpStatusCode.OK, (await Import(projectId, document)).StatusCode);
        Assert.Equal(exported, await client.GetStringAsync(Url(projectId, "export") + "?format=json"));

        // Into another project, as JSON and as YAML: the same bindings.
        var (_, copyProjectId) = await TestHost.CreateProjectAsync(client);
        Assert.Equal(HttpStatusCode.OK, (await Import(copyProjectId, document)).StatusCode);
        Assert.Equal(expected, await client.GetStringAsync(Url(copyProjectId, "bindings")));
        var (_, yamlProjectId) = await TestHost.CreateProjectAsync(client);
        var yaml = await client.GetStringAsync(Url(projectId, "export") + "?format=yaml");
        Assert.Equal(HttpStatusCode.OK, (await Import(yamlProjectId, JsonValue.Create(yaml), "application/x-yaml")).StatusCode);
        Assert.Equal(expected, await client.GetStringAsync(Url(yamlProjectId, "bindings")));

        // A document written before bindings existed has no such member. It imports, with none.
        var (_, olderProjectId) = await TestHost.CreateProjectAsync(client);
        var older = document.DeepClone().AsObject();
        Assert.True(older.Remove("bindings"));
        Assert.Equal(HttpStatusCode.OK, (await Import(olderProjectId, older)).StatusCode);
        Assert.Empty(await ListAsync<BindingDto>(client, olderProjectId, "bindings"));
        Assert.Equal(3, (await ListAsync<ChannelDto>(client, olderProjectId, "channels")).Count);

        // A binding that breaks a rule refuses the whole document.
        var (_, refusedProjectId) = await TestHost.CreateProjectAsync(client);
        var invalid = document.DeepClone().AsObject();
        invalid["bindings"]![0]!["sourceChannelId"] = queue;
        Assert.Contains("Binding", await RefusedAsync(Import(refusedProjectId, invalid), HttpStatusCode.BadRequest));
        Assert.Empty(await ListAsync<ChannelDto>(client, refusedProjectId, "channels"));
    }
}
