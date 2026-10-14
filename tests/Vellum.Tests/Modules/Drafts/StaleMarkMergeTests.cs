using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Vellum.Modules.Modelling;
using Vellum.Tests.Modules.Modelling;

namespace Vellum.Tests.Modules.Drafts;

/// <summary>
/// The stale mark belongs to main. A merge brings the draft's values, status and provenance
/// to main, and leaves every mark on main as it was before the merge.
/// </summary>
public class StaleMarkMergeTests(IntegrationFixture fixture) : IClassFixture<IntegrationFixture>
{
    private static readonly StaleMark Mark = StaleMarkScenario.Mark;

    [Fact]
    public async Task A_merge_of_edits_to_other_fields_leaves_the_marks_on_main()
    {
        var factory = fixture.Factory;
        var s = await StaleMarkScenario.CreateAsync(factory);
        await s.MarkAllAsync();
        var draft = await DraftAsync(s);

        await EditEveryFactAsync(s, draft.StreamId);
        await MergeAsync(s, draft.Id, expectedChanges: 6);

        Assert.Equal("Order Service", (string)(await s.FactAsync("elements", s.Orders))["name"]!);
        Assert.Equal(500, (int)(await s.FactAsync("operations", s.PlaceOrder))["timeoutMs"]!);
        Assert.Equal(StaleMarkScenario.All(Mark), await s.StateMarksAsync());
        Assert.Equal(StaleMarkScenario.All(Mark), await s.ApiMarksAsync());
    }

    [Fact]
    public async Task A_draft_cannot_clear_a_mark_on_main_and_its_status_change_still_merges()
    {
        var factory = fixture.Factory;
        var s = await StaleMarkScenario.CreateAsync(factory);
        await s.MarkAllAsync();
        var draft = await DraftAsync(s);

        // On the draft a person states a new status for every fact. That clears the draft's marks.
        foreach (var (path, id) in s.Facts)
            await s.PatchAsync($"{path}/{id}", new { factStatus = "unknown" }, draft.StreamId);
        Assert.Equal(StaleMarkScenario.All(null), await s.ApiMarksAsync(draft.StreamId));

        await MergeAsync(s, draft.Id, expectedChanges: 6);

        foreach (var (path, id) in s.Facts)
            Assert.Equal("unknown", (string)(await s.FactAsync(path, id))["factStatus"]!);
        Assert.Equal(StaleMarkScenario.All(Mark), await s.StateMarksAsync());
        Assert.Equal(StaleMarkScenario.All(Mark), await s.ApiMarksAsync());
    }

    [Fact]
    public async Task A_draft_cannot_set_a_mark_on_main()
    {
        var factory = fixture.Factory;
        var s = await StaleMarkScenario.CreateAsync(factory);
        var draft = await DraftAsync(s);

        // The marks are on the draft's stream only, and the draft also changes each fact.
        await s.MarkAllAsync(draft.StreamId);
        await EditEveryFactAsync(s, draft.StreamId);
        foreach (var (path, id) in s.Facts)
            await s.PatchAsync($"{path}/{id}", new { factStatus = "unknown" }, draft.StreamId);
        await s.MarkAllAsync(draft.StreamId);
        Assert.Equal(StaleMarkScenario.All(Mark), await s.ApiMarksAsync(draft.StreamId));

        var preview = await MergeAsync(s, draft.Id, expectedChanges: 6);

        // The preview shows what main will hold, so it shows no mark.
        Assert.All(preview["autoResolved"]!.AsArray(), change => Assert.Null(change!["resolvedValue"]!["stale"]));
        Assert.Equal("unknown", (string)(await s.FactAsync("elements", s.Orders))["factStatus"]!);
        Assert.Equal(StaleMarkScenario.All(null), await s.StateMarksAsync());
        Assert.Equal(StaleMarkScenario.All(null), await s.ApiMarksAsync());
    }

    [Fact]
    public async Task A_mark_that_main_cleared_after_the_fork_does_not_come_back_with_the_merge()
    {
        var factory = fixture.Factory;
        var s = await StaleMarkScenario.CreateAsync(factory);
        await s.MarkAllAsync();
        var draft = await DraftAsync(s);

        // A later scan finds the evidence again and clears the marks on main.
        foreach (var (_, id) in s.Facts)
            Assert.Equal(1, await s.MarkAsync(id, null));

        await EditEveryFactAsync(s, draft.StreamId);
        foreach (var (path, id) in s.Facts)
            await s.PatchAsync($"{path}/{id}", new { factStatus = "unknown" }, draft.StreamId);
        await s.MarkAllAsync(draft.StreamId);

        var preview = await MergeAsync(s, draft.Id, expectedChanges: 6);

        Assert.All(preview["autoResolved"]!.AsArray(), change => Assert.Null(change!["resolvedValue"]!["stale"]));
        Assert.Equal("Order Service", (string)(await s.FactAsync("elements", s.Orders))["name"]!);
        Assert.Equal(StaleMarkScenario.All(null), await s.StateMarksAsync());
        Assert.Equal(StaleMarkScenario.All(null), await s.ApiMarksAsync());
    }

    [Fact]
    public async Task A_mark_alone_is_no_change_and_no_conflict()
    {
        var factory = fixture.Factory;
        var s = await StaleMarkScenario.CreateAsync(factory);
        var draft = await DraftAsync(s);

        // Main is marked after the fork, and the draft is marked with another revision.
        await s.MarkAllAsync();
        foreach (var (_, id) in s.Facts)
            Assert.Equal(1, await s.MarkAsync(id, new StaleMark("9d0e7b3", StaleDecider.EvidenceNotFound), draft.StreamId));

        await MergeAsync(s, draft.Id, expectedChanges: 0);

        Assert.Equal(StaleMarkScenario.All(Mark), await s.StateMarksAsync());
        Assert.Equal(StaleMarkScenario.All(Mark), await s.ApiMarksAsync());
    }

    /// <summary>One edit per fact, of a field that says nothing about status or provenance.</summary>
    private static async Task EditEveryFactAsync(StaleMarkScenario s, Guid branchId)
    {
        await s.PatchAsync($"elements/{s.Orders}", new { name = "Order Service", technology = "dotnet" }, branchId);
        await s.PatchAsync($"relationships/{s.Reserves}", new { label = "reserves stock" }, branchId);
        await s.PatchAsync($"messages/{s.OrderPlaced}", new { description = "An order was placed", setDescription = true }, branchId);
        await s.PatchAsync($"operations/{s.PlaceOrder}", new { timeoutMs = 500, retry = new { maxAttempts = 3, backoffMs = 250 } }, branchId);
        await s.PatchAsync($"channels/{s.OrderEvents}", new { name = "orders.events" }, branchId);
        await s.PatchAsync($"subscriptions/{s.InventorySub}", new { retry = new { maxAttempts = 5, backoffMs = 10 } }, branchId);
    }

    private static async Task<(Guid Id, Guid StreamId)> DraftAsync(StaleMarkScenario s)
    {
        var response = await TestHost.Created(s.Client.PostAsJsonAsync(s.Url("drafts"), new { id = Guid.NewGuid(), name = $"Draft {Guid.NewGuid():N}" }));
        var draft = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
        return ((Guid)draft["id"]!, (Guid)draft["streamId"]!);
    }

    private static async Task<JsonObject> MergeAsync(StaleMarkScenario s, Guid draftId, int expectedChanges)
    {
        var previewed = await s.Client.PostAsync(s.Url($"drafts/{draftId}/merge/preview"), null);
        Assert.True(previewed.StatusCode == HttpStatusCode.OK, await previewed.Content.ReadAsStringAsync());
        var preview = (await previewed.Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.Empty(preview["conflicts"]!.AsArray());
        Assert.Equal(expectedChanges, preview["autoResolved"]!.AsArray().Count);

        var merged = await s.Client.PostAsJsonAsync(s.Url($"drafts/{draftId}/merge/execute"),
            new { resolutions = Array.Empty<object>(), expectedMainVersion = (int)preview["mainVersion"]! });
        Assert.True(merged.StatusCode == HttpStatusCode.OK, await merged.Content.ReadAsStringAsync());
        return preview;
    }
}
