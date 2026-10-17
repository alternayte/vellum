using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;

namespace Vellum.Tests.Modules.Import;

/// <summary>
/// The step 25 gate findings on the review of many Sources: an accept of one Source re-plans
/// the others, so no two Sources add one thing twice; a declared dependency that an observing
/// Source does not see is listed; identical values of two Sources are no conflict.
/// </summary>
public class SourceGateTests(ImportHost import) : IClassFixture<ImportHost>
{
    [Fact]
    public async Task Two_Sources_pending_at_once_add_one_element_for_one_name()
    {
        var s = await ImportScenario.CreateAsync(import, fixture: null);
        var catalog = await UploadModelFileAsync(s, "catalog-info.yaml", Fixture("ModelFiles", "catalog-info.yaml"));
        var traces = await TracesAsync(s);
        Assert.Contains(await s.ProposalsAsync(catalog.ScanId), p => p.Text("action") == "add" && p.Text("name") == "payments");
        Assert.Contains(await s.ProposalsAsync(traces.ScanId), p => p.Text("action") == "add" && p.Text("name") == "payments");

        await s.AutoApplyAsync(catalog.ScanId);
        // The traces were planned again: payments is a question now, not a second addition.
        var pending = (await s.ProposalsAsync(traces.ScanId)).Where(p => p.Text("status") == "pending").ToList();
        Assert.DoesNotContain(pending, p => p.Text("action") == "add" && p.Text("entityKind") == "element"
            && p.Text("name")!.Equals("payments", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(pending, p => p.Text("action") == "bind" && p.Text("name") == "payments");

        await s.AutoApplyAsync(traces.ScanId);
        var elements = await s.ElementsAsync();
        Assert.Single(elements, e => e.Text("name")!.Equals("payments", StringComparison.OrdinalIgnoreCase));
        Assert.Single(elements, e => e.Text("name")!.Equals("orders", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task An_addition_whose_name_an_element_took_since_the_plan_is_refused_and_asked_instead()
    {
        var s = await ImportScenario.CreateAsync(import);
        var scan = await s.ScanAsync();
        var billing = (await s.ProposalsAsync(scan.Id())).Proposal("element", "Billing");
        Assert.Equal("add", billing.Text("action"));
        // An element of that kind and name appears after the plan.
        var parent = Guid.NewGuid();
        await TestHost.Created(s.Owner.PostAsJsonAsync(s.Model("elements"), new { id = parent, kind = "system", name = "Payments platform" }));
        await TestHost.Created(s.Owner.PostAsJsonAsync(s.Model("elements"), new { id = Guid.NewGuid(), kind = "app", name = "billing", parentId = parent }));

        var decided = await s.DecideAsync(scan.Id(), accept: [billing.Id(), .. billing["dependsOn"]!.AsArray().Select(d => d!.GetValue<Guid>())]);
        Assert.Contains(decided["failed"]!.AsArray(), f => f!["id"]!.GetValue<Guid>() == billing.Id());
        Assert.Single(await s.ElementsAsync(), e => e.Text("name")!.Equals("billing", StringComparison.OrdinalIgnoreCase));
        var asked = (await s.ProposalsAsync(scan.Id())).Where(p => p.Text("status") == "pending").Single(p => p.Text("name") == "Billing");
        Assert.Equal("bind", asked.Text("action"));
    }

    [Fact]
    public async Task A_declared_dependency_that_an_observing_Source_does_not_see_is_listed_for_both_Sources()
    {
        var s = await ImportScenario.CreateAsync(import, fixture: null);
        var catalog = await UploadModelFileAsync(s, "catalog-info.yaml", Fixture("ModelFiles", "catalog-info.yaml"));
        await s.AutoApplyAsync(catalog.ScanId);
        var traces = await TracesAsync(s);
        var question = (await s.ProposalsAsync(traces.ScanId)).Single(p => p.Text("action") == "bind" && p.Text("name") == "orders");
        var answered = await s.Owner.PostAsJsonAsync(s.Url($"/{traces.ScanId}/proposals/decide"), new { bind = new[] { new { id = question.Id(), same = true } } });
        Assert.Equal(HttpStatusCode.OK, answered.StatusCode);

        var sources = await SourcesAsync(s);
        foreach (var id in new[] { catalog.SourceId, traces.SourceId })
        {
            var listed = sources.Single(x => x.Id() == id)["disagreements"]!.AsArray().Select(d => d!.AsObject()).ToList();
            var dependency = listed.Single(d => d.Text("toName")!.Contains("orders-db", StringComparison.OrdinalIgnoreCase)
                || d.Text("toName")!.Contains("Orders DB", StringComparison.OrdinalIgnoreCase));
            Assert.Equal((catalog.SourceId, traces.SourceId), (dependency.Id("declaredBySourceId"), dependency.Id("unconfirmedBySourceId")));
            Assert.Contains("catalog-info.yaml:", dependency.Text("citation"));
            Assert.Equal("measured", dependency.Text("unconfirmedLabel"));
        }
        Assert.True(sources.Single(x => x.Id() == traces.SourceId)["review"]!["unconfirmed"]!.GetValue<int>() > 0);
    }

    [Fact]
    public async Task Identical_values_of_two_Sources_are_no_conflict_and_auto_apply_still_leaves_them_to_a_person()
    {
        var s = await ImportScenario.CreateAsync(import);
        await s.ScanAndAcceptAllAsync();
        s.GitHub.Commit(ImportScenario.Commit2, FixtureGitHub.Load("orders"));
        s.GitHub.Head = ImportScenario.Commit1;
        s.GitHub.Branches["mirror"] = ImportScenario.Commit2;
        var created = await s.Owner.PostAsJsonAsync($"/api/projects/{s.ProjectId}/sources", new { kind = "github", repository = "acme/orders-mirror" });
        var mirror = (await created.Content.ReadFromJsonAsync<JsonObject>())!.Id();
        s.Import.Time.Advance(TimeSpan.FromSeconds(1));
        var started = await s.Owner.PostAsJsonAsync($"/api/projects/{s.ProjectId}/sources/{mirror}/scans", new { @ref = "mirror" });
        var scanB = (await started.Content.ReadFromJsonAsync<JsonObject>())!.Id();
        await s.DrainAsync();

        var channel = (await s.ProposalsAsync(scanB)).Single(p => p.Text("entityKind") == "channel" && p.Text("name") == "billing.order-created");
        Assert.Equal("change", channel.Text("action"));
        Assert.NotEmpty(channel["otherSources"]!.AsArray());
        Assert.DoesNotContain(channel.Id(), await s.AutoApplyAsync(scanB));
    }

    private sealed record Uploaded(Guid SourceId, Guid ScanId);

    private async Task<Uploaded> UploadModelFileAsync(ImportScenario s, string path, string content)
    {
        var response = await s.Owner.PostAsJsonAsync($"/api/projects/{s.ProjectId}/sources/upload", new { path, content });
        Assert.True(response.StatusCode == HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync());
        var body = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
        await s.DrainAsync();
        Assert.Equal("completed", (await s.GetAsync(body.Id("scanId"))).Text("status"));
        return new Uploaded(body.Id("sourceId"), body.Id("scanId"));
    }

    private async Task<Uploaded> TracesAsync(ImportScenario s)
    {
        var created = await s.Owner.PostAsJsonAsync($"/api/projects/{s.ProjectId}/sources", new { kind = "traces", name = "Checkout traces" });
        Assert.True(created.StatusCode == HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        var sourceId = (await created.Content.ReadFromJsonAsync<JsonObject>())!.Id();
        var upload = await s.Owner.PostAsync($"/api/projects/{s.ProjectId}/sources/{sourceId}/traces?fileName=otlp.json",
            new StringContent(Fixture("Runtime", "otlp.json"), Encoding.UTF8, "application/json"));
        Assert.True(upload.StatusCode == HttpStatusCode.Accepted, await upload.Content.ReadAsStringAsync());
        var scanId = (await upload.Content.ReadFromJsonAsync<JsonObject>())!.Id("scanId");
        await s.DrainAsync();
        Assert.Equal("completed", (await s.GetAsync(scanId)).Text("status"));
        return new Uploaded(sourceId, scanId);
    }


    private static async Task<List<JsonObject>> SourcesAsync(ImportScenario s) =>
        [.. (await s.Owner.GetFromJsonAsync<JsonArray>($"/api/projects/{s.ProjectId}/sources"))!.Select(x => x!.AsObject())];

    private static string Fixture(string folder, string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", folder, name));
}
