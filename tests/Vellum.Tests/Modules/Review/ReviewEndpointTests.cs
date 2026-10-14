using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vellum.Modules.Review;
using Vellum.Tests.Modules.Promises;
using Vellum.Shared;

namespace Vellum.Tests.Modules.Review;

/// <summary>
/// The review through the API on the Orders reference case: the finding set, what a person
/// does about a finding, decisions, Build Ready, pinned bundles, who may do what, and what a
/// project keeps when it is deleted.
/// </summary>
public class ReviewEndpointTests(IntegrationFixture fixture) : IClassFixture<IntegrationFixture>
{
    private sealed record Seeded(HttpClient Owner, Guid WorkspaceId, OrdersReference Orders, Guid Ord014)
    {
        public string Url(string path) => Orders.Url(path);
    }

    /// <summary>The Orders reference case with the outbox, ORD-014 approved and its scenario check run.</summary>
    private static async Task<Seeded> SeedAsync(WebApplicationFactory<Program> factory)
    {
        var owner = await TestHost.SignInAsync(factory);
        var (workspaceId, projectId) = await TestHost.CreateProjectAsync(owner);
        var orders = await OrdersReference.SeedAsync(owner, projectId);
        await orders.UseOutboxAsync();
        var ord014 = (await orders.CreateOrd014Async())["id"]!.GetValue<Guid>();
        await orders.ApproveAsync(ord014);
        await orders.RunAsync(ord014, "O1");
        return new Seeded(owner, workspaceId, orders, ord014);
    }

    private static async Task<JsonObject> Ok(Task<HttpResponseMessage> request, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = await request;
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode}: {text}");
        return JsonNode.Parse(text)!.AsObject();
    }

    private static async Task<JsonObject> FindingAsync(Seeded s, string ruleId, Guid subject) =>
        (await Ok(s.Owner.GetAsync(s.Url("review/findings"))))["findings"]!.AsArray()
            .Single(f => (string)f!["finding"]!["ruleId"]! == ruleId && (Guid)f["finding"]!["subject"]!["id"]! == subject)!.AsObject();

    private static async Task<List<(string Code, string Subject)>> ReviewBlockersAsync(Seeded s) =>
        [.. (await Ok(s.Owner.GetAsync(s.Url("build/manifest"))))["blockers"]!.AsArray()
            .Select(b => ((string)b!["code"]!, (string)b["subject"]!)).Where(b => b.Item1.StartsWith("review-", StringComparison.Ordinal))];

    private static Task<HttpResponseMessage> DisposeAsync(HttpClient client, Seeded s, string ruleId, Guid subject, string status, Guid? decisionId = null, string? reason = null) =>
        client.PutAsJsonAsync(s.Url("review/dispositions"), new { ruleId, subjectId = subject, status, decisionId, reason });

    /// <summary>Accepts or rejects a decision as a person does: reads it, then names the content hash that was read.</summary>
    private static async Task<HttpResponseMessage> DecideAsync(HttpClient client, Seeded s, JsonObject decision, string act, string? reason = null, Guid? branchId = null)
    {
        var read = await Ok(s.Owner.GetAsync(s.Url($"decisions/{decision["id"]}")));
        return await client.PostAsJsonAsync(s.Url($"decisions/{decision["id"]}/{act}"), new { contentHash = (string)read["contentHash"]!, reason, branchId });
    }

    private static object Decision(string title, (string, Guid)[]? settles = null, Guid? supersedes = null, string? origin = null) => new
    {
        title,
        context = "The relay runs a stored command. It has no payload of its own.",
        decision = "The relay operation has no schema. Its contract is the outbox row it reads.",
        alternatives = new[] { new { title = "Give it an empty schema", whyNot = "It says nothing a reader can use." } },
        consequences = "A reader finds the contract in the outbox dataset.",
        affectedIds = new[] { OrdersReference.RelayOutbox },
        settles = (settles ?? []).Select(x => new { ruleId = x.Item1, subjectId = x.Item2 }),
        supersedes,
        origin,
    };

    [Fact]
    public async Task The_orders_reference_case_gives_this_finding_set_and_the_same_bytes_each_time()
    {
        var factory = fixture.Factory;
        var s = await SeedAsync(factory);

        var first = await (await s.Owner.GetAsync(s.Url("review/findings"))).Content.ReadAsStringAsync();
        var second = await (await s.Owner.GetAsync(s.Url("review/findings"))).Content.ReadAsStringAsync();
        Assert.Equal(first, second);

        var report = JsonNode.Parse(first)!.AsObject();
        Assert.Equal(("review-rules/2", 64, 64), ((string)report["ruleSetVersion"]!, ((string)report["hash"]!).Length, ((string)report["inputsFingerprint"]!).Length));
        var fired = report["findings"]!.AsArray().Select(f => f!["finding"]!)
            .Where(f => (string)f["result"]! is "concern" or "unknown")
            .Select(f => ((string)f["ruleId"]!, (string)f["result"]!, (string)f["severity"]!, (Guid)f["subject"]!["id"]!)).ToList();
        Assert.Equal(
            [
                // Billing consumes order-events with one attempt and no dead-letter channel.
                ("REL-001", "concern", "warning", OrdersReference.BillingSub),
                // The relay retries, and the model cannot say that a repeated relay is harmless.
                ("REL-003", "unknown", "warning", OrdersReference.RelayOutbox),
                // Inventory dead-letters to a queue that nothing consumes.
                ("REL-005", "concern", "note", OrdersReference.DeadLetters),
                // The relay operation has no schema, and the scenario of ORD-014 calls it.
                ("CON-001", "concern", "blocking", OrdersReference.RelayOutbox),
                // The model says the Orders API produces OrderCreated; the scenario publishes it from the relay.
                ("CON-004", "unknown", "warning", OrdersReference.OrderCreated),
                // Three obligations of ORD-014 have no result.
                ("TRU-002", "concern", "warning", s.Ord014),
                // Four apps against one store and one actor.
                ("OVR-003", "concern", "note", OrdersReference.Shop),
                ("OVR-004", "concern", "warning", OrdersReference.DeadLetters),
            ],
            fired);
        Assert.Equal((1, 6, 2), ((int)report["counts"]!["blocking"]!, (int)report["counts"]!["concerns"]!, (int)report["counts"]!["unknown"]!));
        // Every fact on the path of ORD-014 is declared, and the rule says exactly that and no more.
        var truth = report["findings"]!.AsArray().Select(f => f!["finding"]!).Where(f => (string)f["ruleId"]! == "TRU-001").ToList();
        Assert.NotEmpty(truth);
        Assert.All(truth, f => Assert.StartsWith("The declared model satisfies the rule", (string)f["reason"]!));

        // The rule list is the same list the reference page documents.
        var rules = JsonNode.Parse(await (await s.Owner.GetAsync(s.Url("review/rules"))).Content.ReadAsStringAsync())!.AsArray();
        Assert.Equal(
            ["REL-001", "REL-002", "REL-003", "REL-004", "REL-005", "REL-006", "CON-001", "CON-002", "CON-003", "CON-004", "CON-005", "TRU-001", "TRU-002", "OVR-001", "OVR-002", "OVR-003", "OVR-004", "OVR-005"],
            rules.Select(r => (string)r!["id"]!));

        // A snapshot keeps the findings of this state under the same hash.
        var snapshot = await Ok(s.Owner.PostAsJsonAsync(s.Url("review/snapshots"), new { note = "Before the review meeting" }), HttpStatusCode.Created);
        Assert.Equal(((string)report["hash"]!, (string)report["inputsFingerprint"]!, (long)report["modelVersion"]!),
            ((string)snapshot["hash"]!, (string)snapshot["inputsFingerprint"]!, (long)snapshot["modelVersion"]!));
        Assert.Equal(report["findings"]!.AsArray().Count, snapshot["findings"]!.AsArray().Count);
        var listed = (await Ok(s.Owner.GetAsync(s.Url("review/snapshots"))))["items"]!.AsArray();
        Assert.Equal((Guid)snapshot["id"]!, (Guid)Assert.Single(listed)!["id"]!);
        Assert.Equal((string)snapshot["hash"]!, (string)(await Ok(s.Owner.GetAsync(s.Url($"review/snapshots/{snapshot["id"]}"))))["hash"]!);
    }

    [Fact]
    public async Task A_blocking_finding_prevents_build_ready_until_an_accepted_decision_settles_it_and_only_while_that_decision_is_in_force()
    {
        var factory = fixture.Factory;
        var s = await SeedAsync(factory);
        var relay = OrdersReference.RelayOutbox;
        (string, string)[] blocked = [("review-CON-001", relay.ToString())];
        Assert.Equal(blocked, await ReviewBlockersAsync(s));

        // A wont-fix never settles a blocking finding. A draft and a save are never blocked: the model was saved all along.
        var refused = await Ok(DisposeAsync(s.Owner, s, "CON-001", relay, "wont-fix", reason: "Not now"), HttpStatusCode.Conflict);
        Assert.Contains("accepted decision only", (string)refused["title"]!);

        // A proposed decision settles nothing.
        var adr1 = await Ok(s.Owner.PostAsJsonAsync(s.Url("decisions"), Decision("The relay has no schema", [("CON-001", relay)])), HttpStatusCode.Created);
        Assert.Equal(("ADR-0001", "proposed", "human"), ((string)adr1["number"]!, (string)adr1["status"]!, (string)adr1["origin"]!));
        Assert.Equal(blocked, await ReviewBlockersAsync(s));
        Assert.Equal("open", (string)(await FindingAsync(s, "CON-001", relay))["status"]!);

        // A person with the Owner role accepts it. An Editor does not.
        var editor = await TestHost.SignInMemberAsync(factory, s.Owner, s.WorkspaceId, "Editor");
        Assert.Equal(HttpStatusCode.Forbidden, (await DecideAsync(editor, s, adr1, "accept")).StatusCode);
        var accepted = await Ok(DecideAsync(s.Owner, s, adr1, "accept"));
        Assert.Equal("accepted", (string)accepted["status"]!);
        Assert.NotNull(accepted["acceptedBy"]);
        Assert.Equal(64, ((string)accepted["stamps"]![0]!["fingerprint"]!).Length);

        // The finding is settled, not deleted: it is there with the decision that settles it.
        var settled = await FindingAsync(s, "CON-001", relay);
        Assert.Equal(("accepted-by-decision", "ADR-0001", true, false, false),
            ((string)settled["status"]!, (string)settled["decision"]!, (bool)settled["settled"]!, (bool)settled["blocks"]!, (bool)settled["needsReReview"]!));
        Assert.Equal("concern", (string)settled["finding"]!["result"]!);
        Assert.Empty(await ReviewBlockersAsync(s));

        // The bundle holds the decision, the SDD lists it under the blockers, and the bundle verifies.
        var zip = await (await s.Owner.PostAsJsonAsync(s.Url("build/export"), new { format = "bundle" })).Content.ReadAsByteArrayAsync();
        var files = Unzip(zip);
        var manifest = JsonNode.Parse(files["manifest.json"])!;
        Assert.Equal("vellum-bundle/4", (string)manifest["format"]!);
        Assert.Equal((Guid)adr1["id"]!, (Guid)Assert.Single(manifest["pinned"]!["decisions"]!.AsArray())!["id"]!);
        var adr = Encoding.UTF8.GetString(files["decisions/ADR-0001.md"]);
        Assert.StartsWith("# ADR-0001: The relay has no schema\n", adr);
        Assert.Contains("- Status: accepted. It is in force.", adr);
        Assert.Contains($"- `CON-001` (An operation and a message have a schema) for Relay outbox (`{relay}`)", adr);
        var sdd = Encoding.UTF8.GetString(files["SDD.md"]);
        var decisions = sdd[sdd.IndexOf("### Decisions", StringComparison.Ordinal)..];
        Assert.True(sdd.IndexOf("### Decisions", StringComparison.Ordinal) > sdd.IndexOf("## 12. Unresolved blockers", StringComparison.Ordinal));
        Assert.Contains("| ADR-0001 |", decisions);
        Assert.Contains($"| `CON-001` | blocking | concern | Relay outbox (`{relay}`) | ADR-0001 | settled |", decisions);
        Assert.Equal("ADR-0001", (string)JsonNode.Parse(files["architecture.json"])!["decisions"]![0]!["number"]!);
        var verified = await Ok(VerifyZipAsync(s, zip));
        Assert.Equal((true, true, true), ((bool)verified["identical"]!, (bool)verified["matchesPinned"]!, (bool)verified["current"]!));

        // A decision that supersedes it, once accepted, takes it out of force: the finding is open and blocks again.
        var adr2 = await Ok(s.Owner.PostAsJsonAsync(s.Url("decisions"), Decision("The relay gets a schema later", supersedes: (Guid)adr1["id"]!)), HttpStatusCode.Created);
        Assert.Equal("ADR-0002", (string)adr2["number"]!);
        Assert.Empty(await ReviewBlockersAsync(s));
        await Ok(DecideAsync(s.Owner, s, adr2, "accept"));
        var superseded = await Ok(s.Owner.GetAsync(s.Url($"decisions/{adr1["id"]}")));
        Assert.Equal(("superseded", (Guid)adr2["id"]!), ((string)superseded["status"]!, (Guid)superseded["supersededBy"]!));
        Assert.Equal(blocked, await ReviewBlockersAsync(s));
        Assert.Equal(("open", true), ((string)(await FindingAsync(s, "CON-001", relay))["status"]!, (bool)(await FindingAsync(s, "CON-001", relay))["blocks"]!));

        // The bundle that was exported before is still what its pinned versions generate. The project moved on: that is drift.
        var drifted = await Ok(VerifyZipAsync(s, zip));
        Assert.Equal((true, false), ((bool)drifted["matchesPinned"]!, (bool)drifted["current"]!));
        Assert.Contains(drifted["moved"]!.AsArray(), m => (string)m!["kind"]! == "decision");

        // The superseding decision is withdrawn: the first one is in force again and settles the finding again.
        await Ok(DecideAsync(s.Owner, s, adr2, "reject", "We keep the first decision."));
        Assert.Empty(await ReviewBlockersAsync(s));
        // The first decision is rejected too: nothing settles the finding.
        Assert.Equal("rejected", (string)(await Ok(DecideAsync(s.Owner, s, adr1, "reject")))["status"]!);
        Assert.Equal(blocked, await ReviewBlockersAsync(s));
        // An accepted decision is a record. It is not deleted, and its text is not changed.
        Assert.Equal(HttpStatusCode.Conflict, (await s.Owner.DeleteAsync(s.Url($"decisions/{adr1["id"]}"))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await s.Owner.PutAsJsonAsync(s.Url($"decisions/{adr1["id"]}"), Decision("Changed"))).StatusCode);

        // A person settles the finding from another accepted decision. Then the subject changes.
        var adr3 = await Ok(s.Owner.PostAsJsonAsync(s.Url("decisions"), Decision("The relay has no schema, again")), HttpStatusCode.Created);
        await Ok(DecideAsync(s.Owner, s, adr3, "accept"));
        Assert.Equal(HttpStatusCode.Forbidden, (await DisposeAsync(editor, s, "CON-001", relay, "accepted-by-decision", (Guid)adr3["id"]!)).StatusCode);
        var again = await Ok(DisposeAsync(s.Owner, s, "CON-001", relay, "accepted-by-decision", (Guid)adr3["id"]!));
        Assert.Equal(("accepted-by-decision", "ADR-0003", true), ((string)again["status"]!, (string)again["decision"]!, (bool)again["settled"]!));
        Assert.Empty(await ReviewBlockersAsync(s));

        await Ok(s.Owner.PatchAsJsonAsync(s.Url($"operations/{relay}"), new { name = "Relay the outbox" }));
        var changed = await FindingAsync(s, "CON-001", relay);
        Assert.Equal(("accepted-by-decision", true, false, true), ((string)changed["status"]!, (bool)changed["needsReReview"]!, (bool)changed["settled"]!, (bool)changed["blocks"]!));
        Assert.Equal(blocked, await ReviewBlockersAsync(s));
        Assert.Contains("the finding changed since", (string)(await Ok(s.Owner.GetAsync(s.Url("build/manifest"))))["blockers"]!.AsArray().Last()!["message"]!);

        // The person reads the finding as it is now and settles it again with the same decision.
        var reviewed = await Ok(DisposeAsync(s.Owner, s, "CON-001", relay, "accepted-by-decision", (Guid)adr3["id"]!));
        Assert.Equal((false, true), ((bool)reviewed["needsReReview"]!, (bool)reviewed["settled"]!));
        Assert.Empty(await ReviewBlockersAsync(s));

        // Set to open: the decision no longer names the finding.
        Assert.Equal("open", (string)(await Ok(DisposeAsync(s.Owner, s, "CON-001", relay, "open")))["status"]!);
        Assert.Empty((await Ok(s.Owner.GetAsync(s.Url($"decisions/{adr3["id"]}"))))["settles"]!.AsArray());
        Assert.Equal(blocked, await ReviewBlockersAsync(s));

        // All decisions as one Markdown document, by number.
        var all = await (await s.Owner.GetAsync(s.Url("decisions/export"))).Content.ReadAsStringAsync();
        Assert.True(all.IndexOf("## ADR-0001:", StringComparison.Ordinal) < all.IndexOf("## ADR-0003:", StringComparison.Ordinal));
        Assert.Contains("- Status: rejected. It is not in force. A person rejected it.", all);
    }

    [Fact]
    public async Task A_wont_fix_needs_a_reason_and_needs_a_new_review_when_the_subject_changes()
    {
        var factory = fixture.Factory;
        var s = await SeedAsync(factory);
        var billing = OrdersReference.BillingSub;

        Assert.Equal(HttpStatusCode.BadRequest, (await DisposeAsync(s.Owner, s, "REL-001", billing, "wont-fix")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await DisposeAsync(s.Owner, s, "REL-001", billing, "done", reason: "x")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await DisposeAsync(s.Owner, s, "XYZ-001", billing, "wont-fix", reason: "x")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await DisposeAsync(s.Owner, s, "REL-001", Guid.NewGuid(), "wont-fix", reason: "x")).StatusCode);
        // A verified result has nothing to answer.
        Assert.Equal(HttpStatusCode.Conflict, (await DisposeAsync(s.Owner, s, "REL-001", OrdersReference.InventorySub, "wont-fix", reason: "x")).StatusCode);

        var hashBefore = (string)(await Ok(s.Owner.GetAsync(s.Url("review/findings"))))["hash"]!;
        var answered = await Ok(DisposeAsync(s.Owner, s, "REL-001", billing, "wont-fix", reason: "Billing reads the ledger again each night."));
        Assert.Equal(("wont-fix", "Billing reads the ledger again each night.", true, false),
            ((string)answered["status"]!, (string)answered["reason"]!, (bool)answered["settled"]!, (bool)answered["needsReReview"]!));
        // The finding is still a concern, and the hash of the findings does not depend on a disposition.
        Assert.Equal("concern", (string)answered["finding"]!["result"]!);
        Assert.Equal(hashBefore, (string)(await Ok(s.Owner.GetAsync(s.Url("review/findings"))))["hash"]!);

        // The channel is renamed: the sentence of the finding names it, so the finding is not the one that was answered.
        await Ok(s.Owner.PatchAsJsonAsync(s.Url($"channels/{OrdersReference.OrderEvents}"), new { name = "order-events-v2" }));
        var changed = await FindingAsync(s, "REL-001", billing);
        Assert.Equal(("wont-fix", true, false), ((string)changed["status"]!, (bool)changed["needsReReview"]!, (bool)changed["settled"]!));

        Assert.Equal("open", (string)(await Ok(DisposeAsync(s.Owner, s, "REL-001", billing, "open")))["status"]!);
        Assert.Equal("open", (string)(await FindingAsync(s, "REL-001", billing))["status"]!);
    }

    [Fact]
    public async Task An_agent_token_reads_and_proposes_and_does_nothing_else()
    {
        var factory = fixture.Factory;
        var s = await SeedAsync(factory);
        var agent = await TestHost.CreateAgentAsync(factory, s.Owner, s.WorkspaceId, ["read", "propose"]);
        var reader = await TestHost.CreateAgentAsync(factory, s.Owner, s.WorkspaceId, ["read"]);

        // read: findings, rules, decisions and questions.
        foreach (var path in new[] { "review/findings", "review/rules", "decisions", "decisions/export", "questions" })
            Assert.Equal(HttpStatusCode.OK, (await reader.Client.GetAsync(s.Url(path))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.Client.PostAsJsonAsync(s.Url("decisions"), Decision("From a reader"))).StatusCode);

        // propose: a decision and a question. The origin comes from who calls, whatever the body says.
        var proposed = await Ok(agent.Client.PostAsJsonAsync(s.Url("decisions"),
            Decision("The relay has no schema", [("CON-001", OrdersReference.RelayOutbox)], origin: "human")), HttpStatusCode.Created);
        Assert.Equal(("proposed", "agent-suggested"), ((string)proposed["status"]!, (string)proposed["origin"]!));
        var asked = await Ok(agent.Client.PostAsJsonAsync(s.Url("questions"), new { text = "Who reads the dead-letter queue?", about = new[] { OrdersReference.DeadLetters }, origin = "human" }), HttpStatusCode.Created);
        Assert.Equal(("open", "agent-suggested"), ((string)asked["status"]!, (string)asked["origin"]!));

        // Nothing else: no accept, no reject, no change, no delete, no disposition, no snapshot, no answer, no critique.
        async Task Refused(string type, Task<HttpResponseMessage> request)
        {
            var refusal = await Ok(request, HttpStatusCode.Forbidden);
            Assert.Equal(type, (string)refusal["type"]!);
        }
        await Refused("human_only", agent.Client.PostAsync(s.Url($"decisions/{proposed["id"]}/accept"), null));
        await Refused("human_only", agent.Client.PostAsync(s.Url($"decisions/{proposed["id"]}/reject"), null));
        await Refused("agent_not_allowed", agent.Client.PutAsJsonAsync(s.Url($"decisions/{proposed["id"]}"), Decision("Changed")));
        await Refused("agent_not_allowed", agent.Client.DeleteAsync(s.Url($"decisions/{proposed["id"]}")));
        await Refused("human_only", DisposeAsync(agent.Client, s, "REL-001", OrdersReference.BillingSub, "wont-fix", reason: "An agent says so"));
        await Refused("human_only", DisposeAsync(agent.Client, s, "CON-001", OrdersReference.RelayOutbox, "accepted-by-decision", (Guid)proposed["id"]!));
        await Refused("human_only", agent.Client.PostAsJsonAsync(s.Url("review/snapshots"), new { }));
        await Refused("human_only", agent.Client.PostAsJsonAsync(s.Url($"questions/{asked["id"]}/answer"), new { answer = "Nobody" }));
        await Refused("human_only", agent.Client.PostAsync(s.Url($"questions/{asked["id"]}/drop"), null));
        await Refused("human_only", agent.Client.PostAsJsonAsync(s.Url("review/critiques"), new { }));

        // What the agent wrote changed nothing: the decision is a proposal and the finding blocks.
        Assert.Equal("proposed", (string)(await Ok(s.Owner.GetAsync(s.Url($"decisions/{proposed["id"]}"))))["status"]!);
        Assert.Equal(("open", true), ((string)(await FindingAsync(s, "CON-001", OrdersReference.RelayOutbox))["status"]!, (bool)(await FindingAsync(s, "CON-001", OrdersReference.RelayOutbox))["blocks"]!));
        Assert.Empty((await Ok(s.Owner.GetAsync(s.Url("review/snapshots"))))["items"]!.AsArray());

        // A person deletes the proposal. Its number is not issued again.
        Assert.Equal(HttpStatusCode.NoContent, (await s.Owner.DeleteAsync(s.Url($"decisions/{proposed["id"]}"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await s.Owner.GetAsync(s.Url($"decisions/{proposed["id"]}"))).StatusCode);
        Assert.Equal("ADR-0002", (string)(await Ok(s.Owner.PostAsJsonAsync(s.Url("decisions"), Decision("Next")), HttpStatusCode.Created))["number"]!);

        // A person answers the question; another is dropped.
        var answered = await Ok(s.Owner.PostAsJsonAsync(s.Url($"questions/{asked["id"]}/answer"), new { answer = "The support team, by hand." }));
        Assert.Equal(("answered", "The support team, by hand."), ((string)answered["status"]!, (string)answered["answer"]!));
        Assert.NotNull(answered["answeredBy"]);
        var other = await Ok(s.Owner.PostAsJsonAsync(s.Url("questions"), new { text = "Is the relay one process?" }), HttpStatusCode.Created);
        Assert.Equal("human", (string)other["origin"]!);
        Assert.Equal("dropped", (string)(await Ok(s.Owner.PostAsync(s.Url($"questions/{other["id"]}/drop"), null)))["status"]!);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Owner.PostAsJsonAsync(s.Url("questions"), new { text = " " })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Owner.PostAsJsonAsync(s.Url("questions"), new { text = new string('q', 4_001) })).StatusCode);
        Assert.Equal(2, (await Ok(s.Owner.GetAsync(s.Url("questions"))))["items"]!.AsArray().Count);
    }

    [Fact]
    public async Task A_pinned_state_without_decisions_is_not_exported_and_a_bundle_of_that_format_still_verifies()
    {
        var factory = fixture.Factory;
        var s = await SeedAsync(factory);
        var exporter = await TestHost.CreateAgentAsync(factory, s.Owner, s.WorkspaceId, ["read", "export"]);
        var viewer = await TestHost.SignInMemberAsync(factory, s.Owner, s.WorkspaceId, "Viewer");

        // A token that reads and exports reads the manifest. The state has a review blocker.
        var manifest = await Ok(exporter.Client.GetAsync(s.Url("build/manifest")));
        Assert.False((bool)manifest["buildReady"]!);
        Assert.Contains(manifest["blockers"]!.AsArray(), b => (string)b!["code"]! == "review-CON-001");

        // It takes the decisions out of the pinned state: the state of a vellum-bundle/2 manifest, which holds no review.
        var pinned = manifest["pinned"]!.DeepClone().AsObject();
        Assert.True(pinned.Remove("decisions"));
        foreach (var client in new[] { exporter.Client, viewer, s.Owner })
        {
            foreach (var format in new[] { "bundle", "sdd" })
            {
                var response = await client.PostAsJsonAsync(s.Url("build/export"), new { format, pinned });
                var refusal = await Ok(Task.FromResult(response), HttpStatusCode.Conflict);
                Assert.Equal("pinned_format_not_exported", (string)refusal["type"]!);
                Assert.Contains("vellum-bundle/4", (string)refusal["detail"]!);
                Assert.False(response.Headers.Contains("X-Vellum-Build-Ready"));
            }
        }

        // The pinned state as the manifest names it is exported again, with its review.
        var again = await exporter.Client.PostAsJsonAsync(s.Url("build/export"), new { format = "bundle", pinned = manifest["pinned"] });
        Assert.Equal((HttpStatusCode.OK, "false"), (again.StatusCode, again.Headers.GetValues("X-Vellum-Build-Ready").Single()));
        var current = JsonNode.Parse(Unzip(await again.Content.ReadAsByteArrayAsync())["manifest.json"])!;
        Assert.Equal("vellum-bundle/4", (string)current["format"]!);
        Assert.Contains(current["blockers"]!.AsArray(), b => (string)b!["code"]! == "review-CON-001");

        // A bundle of the old format that someone holds is still what its pinned versions generate.
        byte[] zip;
        using (var scope = factory.Services.CreateScope())
        {
            var pin = System.Text.Json.JsonSerializer.Deserialize<Vellum.Modules.Build.PinnedState>(pinned.ToJsonString(), Vellum.Modules.Promises.PromiseJson.Options)!;
            var store = scope.ServiceProvider.GetRequiredService<Deedbox.IEventStore>();
            var model = await Vellum.Modules.Promises.PinnedModel.AtVersionAsync(store, pin.Branch, pin.StreamVersion, CancellationToken.None);
            var input = await Vellum.Modules.Build.BuildInput.LoadAsync(
                scope.ServiceProvider, s.Orders.ProjectId,
                new Vellum.Modules.Promises.PinnedModel(new Vellum.Modules.Promises.ModelPin(pin.Branch, pin.StreamVersion, null, null), model!),
                Vellum.Modules.Build.BuildLimits.Default, CancellationToken.None, pin);
            zip = Vellum.Modules.Build.Bundle.Build(input, Vellum.Modules.Build.BuildLimits.Default).Zip();
        }
        var files = Unzip(zip);
        var old = JsonNode.Parse(files["manifest.json"])!.AsObject();
        Assert.Equal(("vellum-bundle/2", "2"), ((string)old["format"]!, (string)old["generator"]!["version"]!));
        Assert.False(old["pinned"]!.AsObject().ContainsKey("decisions"));
        Assert.DoesNotContain(files.Keys, path => path.StartsWith("decisions/", StringComparison.Ordinal));
        Assert.DoesNotContain("### Decisions", Encoding.UTF8.GetString(files["SDD.md"]));
        Assert.False(JsonNode.Parse(files["architecture.json"])!.AsObject().ContainsKey("decisions"));
        var verified = await Ok(VerifyZipAsync(s, zip));
        Assert.Equal((true, true), ((bool)verified["identical"]!, (bool)verified["matchesPinned"]!));
    }

    [Fact]
    public async Task An_acceptance_and_a_rejection_bind_the_decision_that_was_read()
    {
        var factory = fixture.Factory;
        var s = await SeedAsync(factory);
        var relay = OrdersReference.RelayOutbox;
        var editor = await TestHost.SignInMemberAsync(factory, s.Owner, s.WorkspaceId, "Editor");
        var adr = await Ok(s.Owner.PostAsJsonAsync(s.Url("decisions"), Decision("The relay has no schema", [("CON-001", relay)])), HttpStatusCode.Created);
        var read = (string)adr["contentHash"]!;
        string Url(string act) => s.Url($"decisions/{adr["id"]}/{act}");

        // An acceptance names what was read.
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Owner.PostAsJsonAsync(Url("accept"), new { })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Owner.PostAsJsonAsync(Url("reject"), new { reason = "No" })).StatusCode);

        // After the owner read it, an editor makes it answer one more finding.
        await Ok(editor.PutAsJsonAsync(s.Url($"decisions/{adr["id"]}"), Decision("The relay has no schema", [("CON-001", relay), ("REL-001", OrdersReference.BillingSub)])));
        var refused = await Ok(s.Owner.PostAsJsonAsync(Url("accept"), new { contentHash = read }), HttpStatusCode.Conflict);
        var now = await Ok(s.Owner.GetAsync(s.Url($"decisions/{adr["id"]}")));
        Assert.Equal(("decision_changed", (string)now["contentHash"]!), ((string)refused["type"]!, (string)refused["contentHash"]!));
        Assert.NotEqual(read, (string)now["contentHash"]!);
        // Nothing was accepted: the decision is a proposal, and neither finding is settled.
        Assert.Equal("proposed", (string)now["status"]!);
        Assert.Equal("open", (string)(await FindingAsync(s, "REL-001", OrdersReference.BillingSub))["status"]!);
        Assert.Equal(HttpStatusCode.Conflict, (await s.Owner.PostAsJsonAsync(Url("reject"), new { contentHash = read })).StatusCode);

        // The owner reads it again and accepts what is there.
        var proposed = (string)now["contentHash"]!;
        Assert.Equal("accepted", (string)(await Ok(s.Owner.PostAsJsonAsync(Url("accept"), new { contentHash = proposed })))["status"]!);

        // A person who still looks at the proposal does not withdraw the accepted decision with one click.
        var stale = await Ok(s.Owner.PostAsJsonAsync(Url("reject"), new { contentHash = proposed, reason = "Not yet" }), HttpStatusCode.Conflict);
        Assert.Equal("decision_changed", (string)stale["type"]!);
        Assert.Equal("accepted", (string)(await Ok(s.Owner.GetAsync(s.Url($"decisions/{adr["id"]}"))))["status"]!);
        Assert.Equal("rejected", (string)(await Ok(s.Owner.PostAsJsonAsync(Url("reject"), new { contentHash = stale["contentHash"], reason = "Withdrawn" })))["status"]!);
    }

    [Fact]
    public async Task An_acceptance_records_the_findings_of_the_model_the_person_read()
    {
        var factory = fixture.Factory;
        var s = await SeedAsync(factory);
        var relay = OrdersReference.RelayOutbox;
        var draft = (Guid)(await Ok(s.Owner.PostAsJsonAsync(s.Url("drafts"), new { id = Guid.NewGuid(), name = "Rename the relay" }), HttpStatusCode.Created))["streamId"]!;
        await Ok(s.Owner.PatchAsJsonAsync(s.Url($"operations/{relay}") + $"?branchId={draft}", new { name = "Relay the outbox" }));

        async Task<string> FingerprintAsync(Guid? branchId) =>
            (string)(await Ok(s.Owner.GetAsync(s.Url("review/findings") + (branchId is null ? "" : $"?branchId={branchId}"))))["findings"]!.AsArray()
                .Single(f => (string)f!["finding"]!["ruleId"]! == "CON-001" && (Guid)f["finding"]!["subject"]!["id"]! == relay)!["finding"]!["fingerprint"]!;
        var (onMain, onDraft) = (await FingerprintAsync(null), await FingerprintAsync(draft));
        Assert.NotEqual(onMain, onDraft);

        // The person reads the findings of the draft and accepts there: the stamp is the finding of the draft.
        var first = await Ok(s.Owner.PostAsJsonAsync(s.Url("decisions"), Decision("The relay has no schema", [("CON-001", relay)])), HttpStatusCode.Created);
        var accepted = await Ok(DecideAsync(s.Owner, s, first, "accept", branchId: draft));
        Assert.Equal(onDraft, (string)accepted["stamps"]![0]!["fingerprint"]!);
        // On the main model the finding is another one, so the decision does not settle it there.
        Assert.Equal((true, true), ((bool)(await FindingAsync(s, "CON-001", relay))["needsReReview"]!, (bool)(await FindingAsync(s, "CON-001", relay))["blocks"]!));

        // With no branch named, the stamp is the finding of the main model.
        await Ok(DecideAsync(s.Owner, s, first, "reject"));
        var second = await Ok(s.Owner.PostAsJsonAsync(s.Url("decisions"), Decision("The relay has no schema, on main", [("CON-001", relay)])), HttpStatusCode.Created);
        Assert.Equal(onMain, (string)(await Ok(DecideAsync(s.Owner, s, second, "accept")))["stamps"]![0]!["fingerprint"]!);
        Assert.False((bool)(await FindingAsync(s, "CON-001", relay))["blocks"]!);
    }

    [Fact]
    public async Task A_question_is_answered_or_dropped_once()
    {
        var factory = fixture.Factory;
        var s = await SeedAsync(factory);
        var question = await Ok(s.Owner.PostAsJsonAsync(s.Url("questions"), new { text = "Who reads the dead-letter queue?" }), HttpStatusCode.Created);
        string Url(string act) => s.Url($"questions/{question["id"]}/{act}");

        // Many people answer at the same time: one answer is stored and every other is refused.
        var answers = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => s.Owner.PostAsJsonAsync(Url("answer"), new { answer = $"Answer {i}" })));
        var stored = await Ok(Task.FromResult(Assert.Single(answers, a => a.StatusCode == HttpStatusCode.OK)));
        Assert.Equal(7, answers.Count(a => a.StatusCode == HttpStatusCode.Conflict));

        // An answered question is not answered again and is not dropped: the answer stands.
        Assert.Equal(HttpStatusCode.Conflict, (await s.Owner.PostAsJsonAsync(Url("answer"), new { answer = "Another answer" })).StatusCode);
        var refused = await Ok(s.Owner.PostAsync(Url("drop"), null), HttpStatusCode.Conflict);
        Assert.Contains("answered", (string)refused["title"]!);
        var kept = (await Ok(s.Owner.GetAsync(s.Url("questions"))))["items"]!.AsArray().Single()!;
        Assert.Equal(("answered", (string)stored["answer"]!), ((string)kept["status"]!, (string)kept["answer"]!));
    }

    [Fact]
    public async Task Two_people_who_answer_one_finding_at_the_same_time_both_get_an_answer()
    {
        var factory = fixture.Factory;
        var s = await SeedAsync(factory);

        var answers = await Task.WhenAll(Enumerable.Range(0, 12).Select(i =>
            DisposeAsync(s.Owner, s, "REL-001", OrdersReference.BillingSub, "wont-fix", reason: $"Reason {i}")));
        Assert.All(answers, a => Assert.Equal(HttpStatusCode.OK, a.StatusCode));
        Assert.Equal("wont-fix", (string)(await FindingAsync(s, "REL-001", OrdersReference.BillingSub))["status"]!);

        // And to open again, at the same time.
        var opened = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => DisposeAsync(s.Owner, s, "REL-001", OrdersReference.BillingSub, "open")));
        Assert.All(opened, a => Assert.Equal(HttpStatusCode.OK, a.StatusCode));
        Assert.Equal("open", (string)(await FindingAsync(s, "REL-001", OrdersReference.BillingSub))["status"]!);
    }

    [Fact]
    public async Task Another_workspace_reaches_nothing_and_a_deleted_project_keeps_nothing()
    {
        var factory = fixture.Factory;
        var s = await SeedAsync(factory);
        var decision = await Ok(s.Owner.PostAsJsonAsync(s.Url("decisions"), Decision("The relay has no schema", [("CON-001", OrdersReference.RelayOutbox)])), HttpStatusCode.Created);
        await Ok(DecideAsync(s.Owner, s, decision, "accept"));
        var question = await Ok(s.Owner.PostAsJsonAsync(s.Url("questions"), new { text = "Who reads the dead-letter queue?" }), HttpStatusCode.Created);
        var snapshot = await Ok(s.Owner.PostAsJsonAsync(s.Url("review/snapshots"), new { }), HttpStatusCode.Created);
        await Ok(DisposeAsync(s.Owner, s, "REL-001", OrdersReference.BillingSub, "wont-fix", reason: "Accepted"));

        // A member of another workspace, with a project of their own.
        var stranger = await TestHost.SignInAsync(factory);
        var (_, theirs) = await TestHost.CreateProjectAsync(stranger);
        async Task Status(HttpStatusCode expected, Task<HttpResponseMessage> request, string what) =>
            Assert.True((await request).StatusCode == expected, what);

        foreach (var path in new[] { "review/findings", "review/snapshots", $"review/snapshots/{snapshot["id"]}", "review/critiques", "decisions", $"decisions/{decision["id"]}", $"decisions/{decision["id"]}/export", "decisions/export", "questions" })
            await Status(HttpStatusCode.Forbidden, stranger.GetAsync(s.Url(path)), $"GET {path}");
        await Status(HttpStatusCode.Forbidden, stranger.PostAsJsonAsync(s.Url("decisions"), Decision("Planted")), "POST decisions");
        await Status(HttpStatusCode.Forbidden, stranger.PostAsync(s.Url($"decisions/{decision["id"]}/reject"), null), "reject");
        await Status(HttpStatusCode.Forbidden, stranger.PostAsJsonAsync(s.Url("questions"), new { text = "Planted" }), "POST questions");
        await Status(HttpStatusCode.Forbidden, stranger.PostAsJsonAsync(s.Url("review/snapshots"), new { }), "POST snapshot");
        await Status(HttpStatusCode.Forbidden, DisposeAsync(stranger, s, "REL-001", OrdersReference.BillingSub, "open"), "PUT disposition");

        // The IDs of this project name nothing in the stranger's own project.
        string Theirs(string path) => $"/api/projects/{theirs}/{path}";
        await Status(HttpStatusCode.NotFound, stranger.GetAsync(Theirs($"decisions/{decision["id"]}")), "GET foreign decision");
        await Status(HttpStatusCode.NotFound, stranger.PostAsJsonAsync(Theirs($"decisions/{decision["id"]}/reject"), new { contentHash = decision["contentHash"] }), "reject foreign decision");
        await Status(HttpStatusCode.NotFound, stranger.PutAsJsonAsync(Theirs($"decisions/{decision["id"]}"), Decision("Taken over")), "PUT foreign decision");
        await Status(HttpStatusCode.NotFound, stranger.DeleteAsync(Theirs($"decisions/{decision["id"]}")), "DELETE foreign decision");
        await Status(HttpStatusCode.NotFound, stranger.GetAsync(Theirs($"review/snapshots/{snapshot["id"]}")), "GET foreign snapshot");
        await Status(HttpStatusCode.NotFound, stranger.PostAsJsonAsync(Theirs($"questions/{question["id"]}/answer"), new { answer = "Taken over" }), "answer foreign question");
        await Status(HttpStatusCode.BadRequest, stranger.PostAsJsonAsync(Theirs("decisions"), Decision("Supersede a foreign one", supersedes: (Guid)decision["id"]!)), "supersede foreign decision");
        Assert.Empty((await Ok(stranger.GetAsync(Theirs("decisions"))))["items"]!.AsArray());
        Assert.Equal("accepted", (string)(await Ok(s.Owner.GetAsync(s.Url($"decisions/{decision["id"]}"))))["status"]!);

        // Delete the project: the decisions with their streams, the numbering, the dispositions, the snapshots and the questions go.
        async Task<int[]> RowsAsync()
        {
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var projectId = s.Orders.ProjectId;
            return
            [
                await db.Decisions.CountAsync(d => d.ProjectId == projectId), await db.DecisionNumbering.CountAsync(n => n.ProjectId == projectId),
                await db.Dispositions.CountAsync(d => d.ProjectId == projectId), await db.ReviewSnapshots.CountAsync(x => x.ProjectId == projectId),
                await db.Questions.CountAsync(q => q.ProjectId == projectId), await db.Critiques.CountAsync(c => c.ProjectId == projectId),
            ];
        }
        Assert.Equal("1,1,1,1,1,0", string.Join(",", await RowsAsync()));
        Assert.Equal(HttpStatusCode.NoContent, (await s.Owner.DeleteAsync($"/api/projects/{s.Orders.ProjectId}")).StatusCode);
        Assert.Equal("0,0,0,0,0,0", string.Join(",", await RowsAsync()));

        // The stream of the decision is deleted too, so its ID is not a decision of any project.
        using (var scope = factory.Services.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<Deedbox.IEventStore>();
            Assert.Null(await Vellum.Modules.Promises.StreamAt.FoldAsync<Vellum.Modules.Review.DecisionState>(store, (Guid)decision["id"]!, null, CancellationToken.None));
        }
    }

    private static Task<HttpResponseMessage> VerifyZipAsync(Seeded s, byte[] zip)
    {
        var content = new ByteArrayContent(zip);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        return s.Owner.PostAsync(s.Url("build/verify"), content);
    }

    private static Dictionary<string, byte[]> Unzip(byte[] zip)
    {
        using var archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read);
        return archive.Entries.ToDictionary(e => e.FullName, e =>
        {
            using var stream = e.Open();
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            return buffer.ToArray();
        }, StringComparer.Ordinal);
    }
}
