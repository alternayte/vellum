using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Deedbox;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vellum.Modules.Modelling.Model;
using Vellum.Modules.Workspaces;
using Vellum.Tests.Shared.Outbound;
using Vellum.Shared;

namespace Vellum.Tests.Modules.Import;

/// <summary>
/// What a rescan may conclude from a missing fact. A partial scan concludes nothing. A
/// complete scan proposes a removal and marks the accepted fact stale, and does no more:
/// the fact keeps its values, a declared fact gets no mark, and nothing is accepted.
/// </summary>
public class ImportStalenessTests(ImportHost import) : IClassFixture<ImportHost>
{
    private static SortedDictionary<string, byte[]> WithoutBilling()
    {
        var files = FixtureGitHub.Load("orders");
        foreach (var path in files.Keys.Where(p => p.StartsWith("billing/", StringComparison.Ordinal)).ToList())
            files.Remove(path);
        return files;
    }

    private Task SettingsAsync(ImportScenario s, JsonObject? importSettings = null)
    {
        var overrides = new JsonObject { ["github"] = new JsonObject { ["apiBaseUrl"] = $"https://{s.GitHubHost}" } };
        if (importSettings is not null)
            overrides["import"] = importSettings;
        return OutboundHost.PutSettingsAsync(s.Owner, s.WorkspaceId, overrides);
    }

    private static async Task<string> ModelAsync(ImportScenario s)
    {
        var parts = new List<string>();
        foreach (var path in new[] { "elements", "relationships", "messages", "operations", "channels", "subscriptions", "schemas" })
            parts.Add(await s.Owner.GetStringAsync(s.Model(path)));
        return string.Join("\n", parts);
    }

    private static JsonObject? Stale(JsonObject entity) => entity["stale"] as JsonObject;

    private async Task<(Guid StreamId, long Version)> StreamAsync(ImportScenario s)
    {
        using var scope = import.Host.Factory.Services.CreateScope();
        var streamId = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Projects
            .Where(p => p.Id == s.ProjectId).Select(p => p.StreamId).FirstAsync();
        var (_, version) = await scope.ServiceProvider.GetRequiredService<IEventStore>().Load<ModelState>(StreamId.From(streamId));
        return (streamId, version);
    }

    [Fact]
    public async Task A_partial_scan_and_a_truncated_tree_propose_no_removal_and_mark_nothing_stale()
    {
        var s = await ImportScenario.CreateAsync(import);
        await s.ScanAndAcceptAllAsync();
        var accepted = await ModelAsync(s);

        // The next commit holds the same files, and the scan may read five of them only.
        s.GitHub.Commit(ImportScenario.Commit2, FixtureGitHub.Load("orders"));
        await SettingsAsync(s, new JsonObject { ["maxFiles"] = 5 });
        var partial = await s.ScanAsync();
        Assert.Equal(("completed", false), (partial.Text("status"), partial["isComplete"]!.GetValue<bool>()));
        Assert.Equal(5, partial["progress"]!["filesRead"]!.GetValue<int>());
        Assert.Contains(partial["notes"]!.AsArray(), n => n!["code"]!.GetValue<string>() == "limit:files" && n["message"]!.GetValue<string>().Contains("import.maxFiles"));

        // Most accepted facts are missing from this snapshot. The scan did not look at
        // their files, so that proves nothing, and the scan says how many and why.
        var proposals = await s.ProposalsAsync(partial.Id());
        Assert.DoesNotContain(proposals, p => p.Text("action") == "remove");
        var linked = partial["coverage"]!["linkedFacts"]!;
        Assert.True(linked["limit"]!.GetValue<int>() > 0);
        Assert.Equal(linked["limit"]!.GetValue<int>(), linked["notCovered"]!.GetValue<int>());
        Assert.Equal(5, partial["coverage"]!["filesExtracted"]!.GetValue<int>());
        Assert.All(proposals, p => Assert.True(p["partial"]!.GetValue<bool>()));
        Assert.Equal(accepted, await ModelAsync(s));

        // GitHub cuts the file list: the scan has no result and says so.
        await SettingsAsync(s);
        s.GitHub.Truncated = true;
        var cut = await s.ScanAsync();
        Assert.Equal(("failed", false, "tree_truncated"), (cut.Text("status"), cut["isComplete"]!.GetValue<bool>(), cut["error"]!["code"]!.GetValue<string>()));
        Assert.Contains("No file was read", cut["error"]!["message"]!.GetValue<string>());
        Assert.Empty(await s.ProposalsAsync(cut.Id()));
        Assert.Equal(accepted, await ModelAsync(s));
        Assert.DoesNotContain("\"stale\":{", accepted);
    }

    [Fact]
    public async Task A_complete_rescan_without_the_evidence_marks_the_fact_stale_and_proposes_its_removal()
    {
        var s = await ImportScenario.CreateAsync(import);
        await s.ScanAndAcceptAllAsync();
        var me = (await s.Owner.GetFromJsonAsync<JsonObject>("/api/auth/me"))!.Text("id");

        // A person takes over one imported fact: the queue of the billing service is now declared.
        var queue = (await s.AllAsync(s.Model("channels"))).Single("name", "billing.order-created");
        var patched = await s.Owner.PatchAsJsonAsync(s.Model($"channels/{queue.Id()}"), new { factStatus = "declared" });
        Assert.Equal(HttpStatusCode.OK, patched.StatusCode);
        var before = await ModelAsync(s);
        var (streamId, versionBefore) = await StreamAsync(s);

        // A complete scan of a commit that has no billing service.
        s.GitHub.Commit(ImportScenario.Commit2, WithoutBilling());
        var scan = await s.ScanAsync();
        Assert.Equal(("completed", true), (scan.Text("status"), scan["isComplete"]!.GetValue<bool>()));

        var proposals = await s.ProposalsAsync(scan.Id());
        var removals = proposals.Where(p => p.Text("action") == "remove").ToList();
        Assert.Equal(["channel", "element", "subscription"], removals.Values("entityKind"));
        Assert.All(proposals, p => Assert.Equal(("pending", false), (p.Text("status"), p["partial"]!.GetValue<bool>())));
        var message = Assert.Single(proposals, p => p.Text("action") != "remove");
        Assert.Equal(("message", "OrderCreated", "change"), (message.Text("entityKind"), message.Text("name"), message.Text("action")));
        Assert.Empty(message["proposed"]!["consumerIds"]!.AsArray());

        // The evidence of a removal is what the fact was accepted with, at the commit it came from.
        var removal = removals.Single("entityKind", "element");
        Assert.Null(removal["proposed"]);
        Assert.Equal("Billing", removal["current"]!["name"]!.GetValue<string>());
        Assert.Contains($"@{ImportScenario.Commit1}:billing/Billing.csproj:1-1", removal["evidence"]![0]!["pointer"]!.GetValue<string>());
        Assert.Contains(removals.Single("entityKind", "subscription").Id(),
            removal["dependsOn"]!.AsArray().Select(d => d!.GetValue<Guid>()));
        Assert.Contains(message.Id(), removal["dependsOn"]!.AsArray().Select(d => d!.GetValue<Guid>()));

        // The stale mark is not a deletion and changes no value of the fact.
        var elements = await s.ElementsAsync();
        var billing = elements.Single("name", "Billing");
        Assert.Equal((ImportScenario.Commit2, "evidence-not-found"), (Stale(billing)!.Text("sinceRevision"), Stale(billing)!.Text("reason")));
        Assert.Equal(("observed-in-source", ImportScenario.Commit1, me),
            (billing.Text("factStatus"), billing["provenance"]!["revision"]!.GetValue<string>(), billing["provenance"]!["acceptedBy"]!.GetValue<string>()));
        Assert.All(elements.Where(e => e.Text("name") != "Billing"), e => Assert.Null(Stale(e)));
        Assert.NotNull(Stale((await s.Owner.GetFromJsonAsync<JsonObject>(s.Model($"elements/{billing.Id()}")))!));
        var subscriptions = await s.AllAsync(s.Model("subscriptions"));
        Assert.Equal(ImportScenario.Commit2, Stale(subscriptions.Single("consumerId", billing.Text("id")!))!.Text("sinceRevision"));
        Assert.Null(Stale(subscriptions.Single(sub => sub.Text("consumerId") != billing.Text("id"))));

        // The declared fact got no mark and no change: it rests on a person's word.
        var declared = (await s.AllAsync(s.Model("channels"))).Single("id", queue.Text("id")!);
        Assert.Equal(("declared", "billing.order-created"), (declared.Text("factStatus"), declared.Text("name")));
        Assert.Null(Stale(declared));

        // Apart from the two marks, the model is what it was. Nothing was accepted.
        var marked = await ModelAsync(s);
        Assert.Equal(before, marked.Replace($$"""{"sinceRevision":"{{ImportScenario.Commit2}}","reason":"evidence-not-found"}""", "null"));

        // The scan wrote two events, both stale marks, as itself and not as a person.
        using (var scope = import.Host.Factory.Services.CreateScope())
        {
            var written = await scope.ServiceProvider.GetRequiredService<IEventStore>().ReadStream(StreamId.From(streamId), afterVersion: versionBefore, limit: 100);
            Assert.Equal(["model.element_provenance_recorded", "model.subscription_provenance_recorded"], written.Select(e => e.EventType).Order());
            Assert.All(written, e => Assert.Equal("system:import-scan", e.Metadata.Actor));
        }

        // A rebuild of the read model keeps the marks.
        await RebuildAsync("model_read_model");
        Assert.Equal(marked, await ModelAsync(s));

        // The evidence is back: the mark goes, and nothing is proposed.
        s.GitHub.Head = ImportScenario.Commit1;
        var back = await s.ScanAsync();
        Assert.Empty(await s.ProposalsAsync(back.Id()));
        Assert.Equal(before, await ModelAsync(s));
        Assert.All(await s.ProposalsAsync(scan.Id()), p => Assert.Equal("superseded", p.Text("status")));

        // Stale again. An edit of a value does not clear the mark. A person who states the
        // status and the provenance of the fact does.
        s.GitHub.Head = ImportScenario.Commit2;
        await s.ScanAsync();
        Assert.NotNull(Stale((await s.ElementsAsync()).Single("name", "Billing")));
        Assert.Equal(HttpStatusCode.OK, (await s.Owner.PatchAsJsonAsync(s.Model($"elements/{billing.Id()}"), new { name = "Billing (legacy)" })).StatusCode);
        Assert.NotNull(Stale((await s.ElementsAsync()).Single("name", "Billing (legacy)")));
        var stated = await s.Owner.PatchAsJsonAsync(s.Model($"elements/{billing.Id()}"), new
        {
            factStatus = "observed-in-source",
            provenance = new { source = billing["provenance"]!["source"]!.GetValue<string>(), revision = ImportScenario.Commit1 },
        });
        Assert.True(stated.StatusCode == HttpStatusCode.OK, await stated.Content.ReadAsStringAsync());
        Assert.Null(Stale((await s.ElementsAsync()).Single("name", "Billing (legacy)")));

        // The removal is a decision like any other. Named in any order, it is applied in the
        // order the model needs: the message lets go of its consumer, the subscription goes,
        // then the element and the queue.
        var last = await s.ScanAsync();
        var final = await s.ProposalsAsync(last.Id());
        Assert.Equal(4, final.Count);
        Assert.Empty((await s.DecideAsync(last.Id(), accept: final.OrderBy(p => p.Text("entityKind")).Select(p => p.Id())))["failed"]!.AsArray());
        Assert.Equal(["Orders.Api", "inventory-worker", "orders", "postgres"], (await s.ElementsAsync()).Values("name"));
        Assert.Single(await s.AllAsync(s.Model("subscriptions")));
        Assert.DoesNotContain(await s.AllAsync(s.Model("channels")), c => c.Text("name") == "billing.order-created");
        Assert.Empty(Assert.Single(await s.AllAsync(s.Model("messages")))["consumerIds"]!.AsArray());
        Assert.Empty(await s.ProposalsAsync((await s.ScanAsync()).Id()));
    }

    /// <summary>Queues the rebuild, then waits until the projection runs inline again.</summary>
    private async Task RebuildAsync(string projection)
    {
        var admin = import.Host.Factory.Services.GetRequiredService<IEventStoreAdmin>();
        var jobId = await admin.RebuildAsync(projection);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        while (true)
        {
            var job = await admin.GetJobAsync(jobId, timeout.Token);
            Assert.NotEqual(JobState.Failed, job!.Status);
            if (job.Status == JobState.Done
                && (await admin.GetStatusAsync(timeout.Token)).Consumers.Single(c => c.Name == projection).Status == ConsumerState.Running)
                return;
            await Task.Delay(100, timeout.Token);
        }
    }
}
