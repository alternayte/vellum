using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Deedbox;
using Microsoft.Extensions.DependencyInjection;
using Vellum.Modules.Modelling.Model;

namespace Vellum.Tests.Modules.Modelling;

/// <summary>
/// A stale mark is a scan's note on a fact. An edit of the fact's values leaves it; a person
/// clears it by stating the fact's status or provenance. It travels with the model through an
/// export, an import and a fork.
/// </summary>
[Collection("Integration")]
public class StaleMarkLifecycleTests(IntegrationFixture fixture)
{
    private static readonly StaleMark Mark = StaleMarkScenario.Mark;

    private static readonly string[] FactLists = ["elements", "relationships", "messages", "operations", "channels", "subscriptions"];

    [Fact]
    public async Task An_edit_of_a_marked_fact_keeps_the_mark_in_the_state_and_in_the_read_model()
    {
        using var factory = TestHost.Create(fixture);
        var s = await StaleMarkScenario.CreateAsync(factory);
        await s.MarkAllAsync();

        // One PATCH per field, so each edit event is on the stream.
        await s.PatchAsync($"elements/{s.Orders}", new { description = "Takes orders" });
        await s.PatchAsync($"elements/{s.Orders}", new { tags = new[] { "core" } });
        await s.PatchAsync($"elements/{s.Orders}", new { name = "Order Service" });
        await s.PatchAsync($"elements/{s.Orders}", new { technology = "dotnet" });
        await s.PatchAsync($"relationships/{s.Reserves}", new { label = "reserves stock" });
        await s.PatchAsync($"relationships/{s.Reserves}", new { technology = "grpc" });
        await s.PatchAsync($"messages/{s.OrderPlaced}", new { description = "An order was placed", setDescription = true });
        await s.PatchAsync($"messages/{s.OrderPlaced}", new { name = "OrderPlacedV2" });
        await s.PatchAsync($"operations/{s.PlaceOrder}", new { timeoutMs = 500 });
        await s.PatchAsync($"operations/{s.PlaceOrder}", new { retry = new { maxAttempts = 3, backoffMs = 250 } });
        await s.PatchAsync($"channels/{s.OrderEvents}", new { name = "orders.events" });
        await s.PatchAsync($"subscriptions/{s.InventorySub}", new { retry = new { maxAttempts = 5, backoffMs = 10 } });

        // The edits did land.
        Assert.Equal("Order Service", (string)(await s.FactAsync("elements", s.Orders))["name"]!);
        Assert.Equal(500, (int)(await s.FactAsync("operations", s.PlaceOrder))["timeoutMs"]!);

        Assert.Equal(StaleMarkScenario.All(Mark), await s.StateMarksAsync());
        Assert.Equal(StaleMarkScenario.All(Mark), await s.ApiMarksAsync());

        // A rebuild replays the same events into the read model.
        await ProjectionRebuildTests.RebuildAsync(factory.Services.GetRequiredService<IEventStoreAdmin>(), "model_read_model");
        Assert.Equal(StaleMarkScenario.All(Mark), await s.ApiMarksAsync());
    }

    [Fact]
    public async Task A_person_who_states_the_status_or_the_provenance_clears_the_mark()
    {
        using var factory = TestHost.Create(fixture);
        var s = await StaleMarkScenario.CreateAsync(factory);
        await s.MarkAllAsync();

        // The status the fact already has is still a statement, and so is the provenance.
        await s.PatchAsync($"elements/{s.Orders}", new { name = "Order Service", factStatus = "declared" });
        await s.PatchAsync($"relationships/{s.Reserves}", new { factStatus = "declared" });
        await s.PatchAsync($"messages/{s.OrderPlaced}", new { setProvenance = true, provenance = new { source = "repo@abc:Events.cs:3" } });
        await s.PatchAsync($"operations/{s.PlaceOrder}", new { timeoutMs = 500, factStatus = "declared" });
        await s.PatchAsync($"channels/{s.OrderEvents}", new { factStatus = "unknown" });
        await s.PatchAsync($"subscriptions/{s.InventorySub}", new { factStatus = "declared" });

        Assert.Equal(StaleMarkScenario.All(null), await s.StateMarksAsync());
        Assert.Equal(StaleMarkScenario.All(null), await s.ApiMarksAsync());
    }

    [Fact]
    public async Task A_mark_on_a_marked_fact_and_a_clear_of_an_unmarked_fact_append_no_event()
    {
        using var factory = TestHost.Create(fixture);
        var s = await StaleMarkScenario.CreateAsync(factory);

        foreach (var (_, id) in s.Facts)
            Assert.Equal(0, await s.MarkAsync(id, null));

        await s.MarkAllAsync();

        // The next complete scan reads a later commit and still finds no evidence.
        var later = new StaleMark("9d0e7b3", StaleDecider.EvidenceNotFound);
        foreach (var (_, id) in s.Facts)
        {
            Assert.Equal(0, await s.MarkAsync(id, later));
            Assert.Equal(0, await s.MarkAsync(id, Mark));
        }

        // The mark names the first scan that did not find the evidence.
        Assert.Equal(StaleMarkScenario.All(Mark), await s.StateMarksAsync());

        foreach (var (_, id) in s.Facts)
            Assert.Equal(1, await s.MarkAsync(id, null));
        Assert.Equal(StaleMarkScenario.All(null), await s.ApiMarksAsync());
    }

    [Theory]
    [InlineData("json")]
    [InlineData("yaml")]
    public async Task An_export_carries_the_marks_and_an_import_into_a_new_project_sets_them(string format)
    {
        using var factory = TestHost.Create(fixture);
        var s = await StaleMarkScenario.CreateAsync(factory);
        await s.MarkAllAsync();

        var document = await s.Client.GetStringAsync(s.Url($"export?format={format}"));
        var (_, target) = await TestHost.CreateProjectAsync(s.Client);
        await ImportedAsync(s.Client, target, document, format);

        Assert.Equal(StaleMarkScenario.All(Mark), await s.ApiMarksAsync(s.Client, target));

        // The second export differs from the first in the project name only.
        var again = await s.Client.GetStringAsync($"/api/projects/{target}/export?format={format}");
        Assert.Equal(FactLists.Length, again.Split(Mark.SinceRevision).Length - 1);
    }

    [Fact]
    public async Task A_document_without_the_stale_member_imports_with_no_mark_and_leaves_a_mark_that_is_there()
    {
        using var factory = TestHost.Create(fixture);
        var s = await StaleMarkScenario.CreateAsync(factory);

        // The document as an older Vellum wrote it: no fact has a `stale` member.
        var old = JsonNode.Parse(await s.Client.GetStringAsync(s.Url("export?format=json")))!.AsObject();
        foreach (var list in FactLists)
        {
            foreach (var fact in old[list]!.AsArray())
                fact!.AsObject().Remove("stale");
        }

        var (_, target) = await TestHost.CreateProjectAsync(s.Client);
        await ImportedAsync(s.Client, target, old.ToJsonString(), "json");
        Assert.Equal(StaleMarkScenario.All(null), await s.ApiMarksAsync(s.Client, target));

        // An import is not a scan and not a statement about one fact: it clears no mark.
        await s.MarkAllAsync();
        await ImportedAsync(s.Client, s.ProjectId, old.ToJsonString(), "json");
        Assert.Equal(StaleMarkScenario.All(Mark), await s.StateMarksAsync());
        Assert.Equal(StaleMarkScenario.All(Mark), await s.ApiMarksAsync());
    }

    [Theory]
    [InlineData("reason", "evidence\u0007not-found", "Control characters")]
    [InlineData("sinceRevision", "", "stale.sinceRevision")]
    [InlineData("reason", null, "stale.reason")]
    public async Task An_import_refuses_a_mark_that_is_not_bounded_plain_text(string member, string? value, string expected)
    {
        using var factory = TestHost.Create(fixture);
        var s = await StaleMarkScenario.CreateAsync(factory);
        await s.MarkAllAsync();
        var document = JsonNode.Parse(await s.Client.GetStringAsync(s.Url("export?format=json")))!.AsObject();

        foreach (var list in FactLists)
        {
            var bad = document.DeepClone().AsObject();
            MarkedFact(bad, list)["stale"]![member] = value;
            await RefusedAsync(s, bad, expected);
        }

        var tooLong = document.DeepClone().AsObject();
        MarkedFact(tooLong, "elements")["stale"]!["sinceRevision"] = new string('a', 201);
        await RefusedAsync(s, tooLong, "At most 200 characters");
    }

    [Fact]
    public async Task A_variant_and_a_draft_start_with_the_marks_of_main()
    {
        using var factory = TestHost.Create(fixture);
        var s = await StaleMarkScenario.CreateAsync(factory);
        await s.MarkAllAsync();

        var variant = await BranchAsync(s.Client.PostAsJsonAsync(s.Url("variants"), new { id = Guid.NewGuid(), name = "event-driven" }));
        var draft = await BranchAsync(s.Client.PostAsJsonAsync(s.Url("drafts"), new { id = Guid.NewGuid(), name = "Rework" }));

        // A variant from a named revision starts from the state the revision stored.
        var revisionId = Guid.NewGuid();
        await TestHost.Created(s.Client.PostAsJsonAsync(s.Url("revisions"), new { id = revisionId, name = "v1" }));
        var fromRevision = await BranchAsync(s.Client.PostAsJsonAsync(s.Url("variants"),
            new { id = Guid.NewGuid(), name = "from-v1", baseRevisionId = revisionId }));

        foreach (var branch in new[] { variant, draft, fromRevision })
        {
            Assert.Equal(StaleMarkScenario.All(Mark), await s.StateMarksAsync(branch));
            Assert.Equal(StaleMarkScenario.All(Mark), await s.ApiMarksAsync(branch));
        }
    }

    private static JsonObject MarkedFact(JsonObject document, string list) =>
        document[list]!.AsArray().Select(fact => fact!.AsObject()).Single(fact => fact["stale"] is not null);

    private static async Task<Guid> BranchAsync(Task<HttpResponseMessage> request) =>
        (Guid)(await (await TestHost.Created(request)).Content.ReadFromJsonAsync<JsonObject>())!["streamId"]!;

    private static async Task ImportedAsync(HttpClient client, Guid projectId, string document, string format)
    {
        var response = await client.PostAsync($"/api/projects/{projectId}/import",
            new StringContent(document, Encoding.UTF8, format == "yaml" ? "application/yaml" : "application/json"));
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
    }

    private static async Task RefusedAsync(StaleMarkScenario s, JsonObject document, string expected)
    {
        var (_, target) = await TestHost.CreateProjectAsync(s.Client);
        var response = await s.Client.PostAsync($"/api/projects/{target}/import",
            new StringContent(document.ToJsonString(), Encoding.UTF8, "application/json"));
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.BadRequest, $"{response.StatusCode}: {body}");
        Assert.Contains(expected, body);
        Assert.Empty((await s.Client.GetFromJsonAsync<JsonObject>($"/api/projects/{target}/elements"))!["items"]!.AsArray());
    }
}
