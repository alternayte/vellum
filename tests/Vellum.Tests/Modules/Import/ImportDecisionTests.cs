using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace Vellum.Tests.Modules.Import;

/// <summary>
/// What a person decides, and what a scan may not decide for them: conflicts with a
/// hand-edited or declared fact, the memory of a rejection, links to hand-made entities,
/// dependencies between proposals, and the all-or-nothing rule of one decision.
/// </summary>
[Collection("Integration")]
public class ImportDecisionTests(ImportHost import) : IClassFixture<ImportHost>
{
    private static async Task PatchAsync(ImportScenario s, string path, object body)
    {
        var response = await s.Owner.PatchAsJsonAsync(s.Model(path), body);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    private static string[] Fields(JsonObject proposal) =>
        [.. proposal["changes"]!.AsArray().Select(c => c!["field"]!.GetValue<string>()).Order(StringComparer.Ordinal)];

    [Fact]
    public async Task A_fact_that_a_person_edited_or_declared_gets_a_conflict_and_stays_until_it_is_accepted()
    {
        var s = await ImportScenario.CreateAsync(import);
        await s.ScanAndAcceptAllAsync();
        var api = (await s.ElementsAsync()).Single("name", "Orders.Api");
        var billing = (await s.ElementsAsync()).Single("name", "Billing");

        // A person renames one imported app and writes intent on it, and takes over another as declared.
        await PatchAsync(s, $"elements/{api.Id()}", new { name = "Orders API", description = "Takes orders", tags = new[] { "core" } });
        await PatchAsync(s, $"elements/{billing.Id()}", new { technology = "java", factStatus = "declared" });
        var edited = await s.Owner.GetStringAsync(s.Model("elements"));

        var second = await s.ScanAsync();
        var conflicts = await s.ProposalsAsync(second.Id());
        Assert.Equal(["service:billing", "service:orders-api"], conflicts.Values("factKey"));
        Assert.All(conflicts, c => Assert.Equal(("conflict", "pending"), (c.Text("action"), c.Text("status"))));

        // Both values and both sources are shown; intent fields are never part of a proposal.
        var apiConflict = conflicts.Single("factKey", "service:orders-api");
        Assert.Equal(["name"], Fields(apiConflict));
        Assert.Equal(("Orders API", "Orders.Api", "observed-in-source"),
            (apiConflict["current"]!["name"]!.GetValue<string>(), apiConflict["proposed"]!["name"]!.GetValue<string>(), apiConflict["current"]!["factStatus"]!.GetValue<string>()));
        Assert.Equal("orders-api/Orders.Api.csproj", apiConflict["evidence"]![0]!["path"]!.GetValue<string>());
        Assert.DoesNotContain("Takes orders", apiConflict.ToJsonString());
        var billingConflict = conflicts.Single("factKey", "service:billing");
        Assert.Equal(["factStatus", "technology"], Fields(billingConflict));
        Assert.Equal(("java", "dotnet", "declared"),
            (billingConflict["current"]!["technology"]!.GetValue<string>(), billingConflict["proposed"]!["technology"]!.GetValue<string>(), billingConflict["current"]!["factStatus"]!.GetValue<string>()));

        // The scan resolved nothing: the model is what the person left.
        Assert.Equal(edited, await s.Owner.GetStringAsync(s.Model("elements")));

        // A rejected proposal is not made again while it says the same thing.
        var rejected = await s.DecideAsync(second.Id(), reject: [apiConflict.Id()]);
        Assert.Equal([apiConflict.Id()], rejected["rejected"]!.AsArray().Select(r => r!.GetValue<Guid>()));
        var third = await s.ScanAsync();
        Assert.Equal(["service:billing"], (await s.ProposalsAsync(third.Id())).Values("factKey"));
        Assert.Equal("superseded", (await s.ProposalsAsync(second.Id())).Single("factKey", "service:billing").Text("status"));
        Assert.Equal((0, 0, 1), Counts(await s.GetAsync(second.Id())));

        // A changed fact is proposed again: here the person edits the entity once more.
        await PatchAsync(s, $"elements/{api.Id()}", new { name = "Orders HTTP API" });
        var fourth = await s.ScanAsync();
        var again = await s.ProposalsAsync(fourth.Id());
        Assert.Equal(["service:billing", "service:orders-api"], again.Values("factKey"));

        // Accepting a conflict is an explicit act for that item. It writes the observed
        // values and the source label, and leaves the intent a person wrote.
        Assert.Empty((await s.DecideAsync(fourth.Id(), accept: again.Select(p => p.Id())))["failed"]!.AsArray());
        var elements = await s.ElementsAsync();
        var apiNow = elements.Single("id", api.Text("id")!);
        Assert.Equal(("Orders.Api", "Takes orders", "core"), (apiNow.Text("name"), apiNow.Text("description"), apiNow["tags"]![0]!.GetValue<string>()));
        var billingNow = elements.Single("id", billing.Text("id")!);
        Assert.Equal(("dotnet", "observed-in-source"), (billingNow.Text("technology"), billingNow.Text("factStatus")));
        Assert.Empty(await s.ProposalsAsync((await s.ScanAsync()).Id()));
    }

    private static (int Pending, int Accepted, int Rejected) Counts(JsonObject scan) => (
        scan["proposalCounts"]!["pending"]!.GetValue<int>(),
        scan["proposalCounts"]!["accepted"]!.GetValue<int>(),
        scan["proposalCounts"]!["rejected"]!.GetValue<int>());

    [Fact]
    public async Task A_decision_links_hand_made_entities_enforces_dependencies_and_is_applied_whole_or_not_at_all()
    {
        var s = await ImportScenario.CreateAsync(import);

        // Hand-made entities with the same kind and the same normalised name as two facts.
        var system = Guid.NewGuid();
        var channel = Guid.NewGuid();
        await TestHost.Created(s.Owner.PostAsJsonAsync(s.Model("elements"), new { id = system, kind = "system", name = " ORDERS " }));
        await TestHost.Created(s.Owner.PostAsJsonAsync(s.Model("elements"), new { id = Guid.NewGuid(), kind = "system", name = "Billing" }));
        await TestHost.Created(s.Owner.PostAsJsonAsync(s.Model("channels"), new { id = channel, name = "order-events", kind = "topic" }));

        var scan = await s.ScanAsync();
        var proposals = await s.ProposalsAsync(scan.Id());
        Assert.Equal(20, proposals.Count);

        // A match is a proposal to link, never a silent link, and only for the same kind:
        // the hand-made system "Billing" is not the app "Billing".
        var repository = proposals.Single("factKey", $"repository:{s.GitHubHost}/acme/orders");
        Assert.Equal(("conflict", system.ToString()), (repository.Text("action"), repository.Text("entityId")));
        Assert.Equal(["factStatus", "name"], Fields(repository));
        Assert.Equal((" ORDERS ", "orders"), (repository["current"]!["name"]!.GetValue<string>(), repository["proposed"]!["name"]!.GetValue<string>()));
        var events = proposals.Single("factKey", "channel:order-events");
        Assert.Equal(("conflict", channel.ToString()), (events.Text("action"), events.Text("entityId")));
        Assert.Equal(["factStatus", "kind"], Fields(events));
        Assert.Equal("add", proposals.Single("factKey", "service:billing").Text("action"));
        var service = proposals.Single("factKey", "service:orders-api");
        Assert.Equal(system.ToString(), service["proposed"]!["parentId"]!.GetValue<string>());
        Assert.Empty(service["dependsOn"]!.AsArray());
        var before = await s.Owner.GetStringAsync(s.Model("elements")) + await s.Owner.GetStringAsync(s.Model("channels"));

        // An operation cannot be accepted before its service.
        var operation = proposals.Single("factKey", "operation:orders-api:POST /orders");
        var alone = await s.DecideAsync(scan.Id(), accept: [operation.Id()]);
        Assert.Empty(alone["accepted"]!.AsArray());
        var failure = Assert.Single(alone["failed"]!.AsArray())!;
        Assert.Equal(operation.Id(), failure["id"]!.GetValue<Guid>());
        Assert.Contains("depends on proposal", failure["reason"]!.GetValue<string>());
        Assert.Contains("Accept both in one decision", failure["reason"]!.GetValue<string>());

        // Nor in a decision that rejects its service. The rejection is not applied either.
        var contradiction = await s.DecideAsync(scan.Id(), accept: [operation.Id()], reject: [service.Id()]);
        Assert.Empty(contradiction["rejected"]!.AsArray());
        Assert.Contains("which this decision rejects", Assert.Single(contradiction["failed"]!.AsArray())!["reason"]!.GetValue<string>());

        // The model refuses one item: its ID was taken after the scan. Nothing of the decision is applied.
        var billing = proposals.Single("factKey", "service:billing");
        await TestHost.Created(s.Owner.PostAsJsonAsync(s.Model("channels"), new { id = billing.Id("entityId"), name = "squatter", kind = "queue" }));
        var refused = await s.DecideAsync(scan.Id(), accept: proposals.Select(p => p.Id()));
        Assert.Empty(refused["accepted"]!.AsArray());
        Assert.Contains(refused["failed"]!.AsArray(), f =>
            f!["id"]!.GetValue<Guid>() == billing.Id() && f["reason"]!.GetValue<string>().Contains("already used by a channel"));
        Assert.Equal(20, Counts(await s.GetAsync(scan.Id())).Pending);
        Assert.Equal(2, (await s.ElementsAsync()).Count);
        Assert.Empty(await s.AllAsync(s.Model("operations")));
        Assert.Equal(HttpStatusCode.NoContent, (await s.Owner.DeleteAsync(s.Model($"channels/{billing.Id("entityId")}"))).StatusCode);
        Assert.Equal(before, await s.Owner.GetStringAsync(s.Model("elements")) + await s.Owner.GetStringAsync(s.Model("channels")));

        // Requests that name nothing usable.
        var stranger = Guid.NewGuid();
        Assert.Contains("not a proposal of this import",
            Assert.Single((await s.DecideAsync(scan.Id(), accept: [stranger]))["failed"]!.AsArray())!["reason"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Owner.PostAsJsonAsync(s.Url($"/{scan.Id()}/proposals/decide"),
            new { accept = new[] { operation.Id() }, reject = new[] { operation.Id() } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Owner.PostAsJsonAsync(s.Url($"/{scan.Id()}/proposals/decide"), new { })).StatusCode);

        // The whole scan in one decision.
        var applied = await s.DecideAsync(scan.Id(), accept: proposals.Select(p => p.Id()));
        Assert.Empty(applied["failed"]!.AsArray());
        var linked = (await s.ElementsAsync()).Single("id", system.ToString());
        Assert.Equal(("orders", "observed-in-source"), (linked.Text("name"), linked.Text("factStatus")));
        Assert.Equal(("exchange", "observed-in-source"),
            ((await s.AllAsync(s.Model("channels"))).Single("id", channel.ToString()).Text("kind"),
             (await s.AllAsync(s.Model("channels"))).Single("id", channel.ToString()).Text("factStatus")));
        Assert.Equal(6, (await s.ElementsAsync()).Count);

        // A proposal is decided once.
        Assert.Contains("already accepted",
            Assert.Single((await s.DecideAsync(scan.Id(), accept: [operation.Id()]))["failed"]!.AsArray())!["reason"]!.GetValue<string>());
        Assert.Empty(await s.ProposalsAsync((await s.ScanAsync()).Id()));
    }

    [Fact]
    public async Task A_rejected_addition_blocks_what_depends_on_it_and_is_not_proposed_again()
    {
        var s = await ImportScenario.CreateAsync(import);
        var scan = await s.ScanAsync();
        var proposals = await s.ProposalsAsync(scan.Id());
        var worker = proposals.Single("factKey", "service:inventory-worker");
        Assert.Equal([worker.Id()], (await s.DecideAsync(scan.Id(), reject: [worker.Id()]))["rejected"]!.AsArray().Select(r => r!.GetValue<Guid>()));
        Assert.Empty(await s.ElementsAsync());

        // The next scan does not propose the service again, and says why its operation and
        // its subscription are not proposals.
        var next = await s.ScanAsync();
        var keys = (await s.ProposalsAsync(next.Id())).Values("factKey");
        Assert.Equal(17, keys.Length);
        Assert.DoesNotContain(keys, key => key.Contains("inventory-worker", StringComparison.Ordinal));
        var blocked = (await s.EvidenceAsync(next.Id())).Where(e => e.Text("reason") == "dependency-rejected").ToList();
        Assert.Equal(["operation:inventory-worker:GET /healthz", "subscription:inventory-worker:amqp:inventory.order-created"], blocked.Values("factKey"));
    }
}
