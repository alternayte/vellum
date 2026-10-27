using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;

namespace Vellum.Tests.Examples;

/// <summary>
/// <c>examples/orders</c> is a file that people import, and it and the other examples are the
/// templates of an empty project. These tests import the committed files through the real
/// endpoints, play their scenarios and run their stress profiles, so a change to the model
/// document format, the scenario format, the trace engine or the stress engine that breaks
/// an example fails here.
/// </summary>
public class ExampleTests(IntegrationFixture fixture) : IClassFixture<IntegrationFixture>
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

    /// <summary>
    /// The templates that an empty project offers (src/Vellum.Web/src/lib/templates.ts), besides
    /// Orders, which has a test of its own. One instance imports each template once: a second
    /// import of the same schema IDs gives the schemas new IDs, and the export then differs.
    /// </summary>
    public static TheoryData<string> Templates => new() { "url-shortener", "chat-service", "payments" };

    private static StringContent Json(string text) => new(text, Encoding.UTF8, "application/json");

    [Fact]
    public async Task The_orders_example_imports_into_an_empty_project_and_its_scenarios_and_stress_profile_hold()
    {
        var (exported, runs) = await OpenTemplateAsync("orders");
        Assert.Equal((8, 5, 5, 3, 4, 2, 2, 1), (
            exported["elements"]!.AsArray().Count, exported["relationships"]!.AsArray().Count, exported["schemas"]!.AsArray().Count,
            exported["operations"]!.AsArray().Count, exported["channels"]!.AsArray().Count, exported["bindings"]!.AsArray().Count,
            exported["subscriptions"]!.AsArray().Count, exported["messages"]!.AsArray().Count));

        var happy = runs["place-an-order.json"];
        var kinds = happy["events"]!.AsArray().Select(e => e!["kind"]!.GetValue<string>()).ToList();
        Assert.Equal(1, kinds.Count(k => k == "Published"));
        Assert.Equal(2, kinds.Count(k => k == "Routed"));
        Assert.Equal(2, kinds.Count(k => k == "Delivered"));
        Assert.DoesNotContain("Dropped", kinds);

        // The failure branch: Inventory is down, the order waits for stock and no event is published.
        var down = runs["place-an-order-inventory-down.json"];
        var downKinds = down["events"]!.AsArray().Select(e => e!["kind"]!.GetValue<string>()).ToList();
        Assert.Equal(3, downKinds.Count(k => k == "RequestFailed"));
        Assert.DoesNotContain("Published", downKinds);
        Assert.Equal("Failed", down["outcomes"]!["reserve"]!.GetValue<string>());
        Assert.Equal("Succeeded", down["outcomes"]!["mark-waiting-for-stock"]!.GetValue<string>());
    }

    [Theory]
    [MemberData(nameof(Templates))]
    public async Task A_template_opens_in_an_empty_project_and_its_scenarios_and_stress_profile_hold(string template)
    {
        var (exported, runs) = await OpenTemplateAsync(template);
        Assert.InRange(exported["elements"]!.AsArray().Count, 8, 15);
        Assert.NotEmpty(exported["operations"]!.AsArray());
        Assert.NotEmpty(runs);
    }

    /// <summary>
    /// Opens the template as the empty project does: the model through the import, each
    /// scenario with a new ID, and the stress profile on the new ID of its scenario. The export
    /// is the committed model, every scenario plays with its expectations held, and the profile
    /// runs at once, twice with the same hash. Returns the export and the run of each scenario file.
    /// </summary>
    private async Task<(JsonObject Exported, Dictionary<string, JsonObject> Runs)> OpenTemplateAsync(string template)
    {
        var directory = Path.Combine(RepositoryRoot(), "examples", template);
        var client = await TestHost.SignInAsync(fixture.Factory);
        var (_, projectId) = await TestHost.CreateProjectAsync(client);
        string Url(string path) => $"/api/projects/{projectId}/{path}";

        var model = await File.ReadAllTextAsync(Path.Combine(directory, "model.json"));
        var imported = await client.PostAsync(Url("import"), Json(model));
        Assert.True(imported.StatusCode == HttpStatusCode.OK, await imported.Content.ReadAsStringAsync());
        Assert.Equal(0, (await imported.Content.ReadFromJsonAsync<JsonObject>())!["refused"]!.GetValue<int>());

        // The export of the imported project is the committed file: nothing was dropped or changed.
        var exported = JsonNode.Parse(await client.GetStringAsync(Url("export")))!.AsObject();
        var committed = JsonNode.Parse(model)!.AsObject();
        // The project name is the name of the project that holds the model, not a part of the model.
        exported.Remove("project");
        committed.Remove("project");
        Assert.Equal(committed.ToJsonString(), exported.ToJsonString());

        var newIds = new Dictionary<Guid, Guid>();
        var runs = new Dictionary<string, JsonObject>();
        foreach (var file in Directory.GetFiles(Path.Combine(directory, "scenarios"), "*.json").Order(StringComparer.Ordinal))
        {
            var scenario = JsonNode.Parse(await File.ReadAllTextAsync(file))!.AsObject();
            var id = Guid.NewGuid();
            newIds[scenario["id"]!.GetValue<Guid>()] = id;
            scenario["id"] = id;
            await TestHost.Created(client.PostAsync(Url("scenarios"), Json(scenario.ToJsonString())));

            var response = await client.PostAsync(Url($"scenarios/{id}/run"), null);
            Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
            var run = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
            Assert.Empty(run["issues"]!.AsArray());
            Assert.NotEmpty(run["assertions"]!.AsArray());
            Assert.All(run["assertions"]!.AsArray(), a => Assert.True(a!["passed"]!.GetValue<bool>(), $"{Path.GetFileName(file)}: {a["detail"]}"));
            runs[Path.GetFileName(file)] = run;
        }

        var profile = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "stress-profile.json")))!.AsObject();
        profile["scenarioId"] = newIds[profile["scenarioId"]!.GetValue<Guid>()];
        var saved = await TestHost.Created(client.PostAsync(Url("stress/profiles"), Json(profile.ToJsonString())));
        var profileId = (await saved.Content.ReadFromJsonAsync<JsonObject>())!["id"]!.GetValue<Guid>();

        async Task<JsonObject> StressAsync()
        {
            var response = await client.PostAsync(Url($"stress/profiles/{profileId}/runs"), null);
            Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
            return (await response.Content.ReadFromJsonAsync<JsonObject>())!;
        }
        var first = await StressAsync();
        var flow = first["result"]!["flow"]!;
        Assert.True(flow["arrivals"]!.GetValue<long>() > 0);
        Assert.NotEmpty(first["result"]!["elements"]!.AsArray());
        // The same profile, model and scenario give the same run: the template is deterministic.
        var second = await StressAsync();
        Assert.Equal(first["run"]!["hash"]!.GetValue<string>(), second["run"]!["hash"]!.GetValue<string>());
        return (exported, runs);
    }
}
