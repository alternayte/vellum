using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;

namespace Vellum.Tests.Examples;

/// <summary>
/// <c>examples/orders</c> is a file that people import. This test imports the committed file
/// through the real endpoint and plays its scenarios, so a change to the model document
/// format, the scenario format or the trace engine that breaks the example fails here.
/// </summary>
public class OrdersExampleTests(IntegrationFixture fixture) : IClassFixture<IntegrationFixture>
{
    private static readonly string Directory = Path.Combine(RepositoryRoot(), "examples", "orders");

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Vellum.slnx")))
                return directory.FullName;
        }
        throw new InvalidOperationException("Vellum.slnx was not found above the test directory.");
    }

    private static StringContent Json(string text) => new(text, Encoding.UTF8, "application/json");

    [Fact]
    public async Task The_orders_example_imports_into_an_empty_project_and_its_scenarios_hold()
    {
        var factory = fixture.Factory;
        var client = await TestHost.SignInAsync(factory);
        var (_, projectId) = await TestHost.CreateProjectAsync(client);
        string Url(string path) => $"/api/projects/{projectId}/{path}";

        var model = await File.ReadAllTextAsync(Path.Combine(Directory, "model.json"));
        var imported = await client.PostAsync(Url("import"), Json(model));
        Assert.True(imported.StatusCode == HttpStatusCode.OK, await imported.Content.ReadAsStringAsync());

        // The export of the imported project is the committed file: nothing was dropped or changed.
        var exported = JsonNode.Parse(await client.GetStringAsync(Url("export")))!.AsObject();
        var committed = JsonNode.Parse(model)!.AsObject();
        // The project name is the name of the project that holds the model, not a part of the model.
        exported.Remove("project");
        committed.Remove("project");
        Assert.Equal(committed.ToJsonString(), exported.ToJsonString());
        Assert.Equal((8, 5, 5, 3, 4, 2, 2, 1), (
            exported["elements"]!.AsArray().Count, exported["relationships"]!.AsArray().Count, exported["schemas"]!.AsArray().Count,
            exported["operations"]!.AsArray().Count, exported["channels"]!.AsArray().Count, exported["bindings"]!.AsArray().Count,
            exported["subscriptions"]!.AsArray().Count, exported["messages"]!.AsArray().Count));

        var happy = await PlayAsync(client, Url, "place-an-order.json");
        var kinds = happy["events"]!.AsArray().Select(e => e!["kind"]!.GetValue<string>()).ToList();
        Assert.Equal(1, kinds.Count(k => k == "Published"));
        Assert.Equal(2, kinds.Count(k => k == "Routed"));
        Assert.Equal(2, kinds.Count(k => k == "Delivered"));
        Assert.DoesNotContain("Dropped", kinds);

        // The failure branch: Inventory is down, the order waits for stock and no event is published.
        var down = await PlayAsync(client, Url, "place-an-order-inventory-down.json");
        var downKinds = down["events"]!.AsArray().Select(e => e!["kind"]!.GetValue<string>()).ToList();
        Assert.Equal(3, downKinds.Count(k => k == "RequestFailed"));
        Assert.DoesNotContain("Published", downKinds);
        Assert.Equal("Failed", down["outcomes"]!["reserve"]!.GetValue<string>());
        Assert.Equal("Succeeded", down["outcomes"]!["mark-waiting-for-stock"]!.GetValue<string>());
    }

    /// <summary>Saves a scenario of the example and plays it. Every expectation of the file must hold.</summary>
    private static async Task<JsonObject> PlayAsync(HttpClient client, Func<string, string> url, string file)
    {
        var document = await File.ReadAllTextAsync(Path.Combine(Directory, "scenarios", file));
        var id = JsonNode.Parse(document)!["id"]!.GetValue<Guid>();
        await TestHost.Created(client.PostAsync(url("scenarios"), Json(document)));

        var response = await client.PostAsync(url($"scenarios/{id}/run"), null);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var run = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.Empty(run["issues"]!.AsArray());
        Assert.NotEmpty(run["assertions"]!.AsArray());
        Assert.All(run["assertions"]!.AsArray(), a => Assert.True(a!["passed"]!.GetValue<bool>(), a["detail"]!.GetValue<string>()));
        return run;
    }
}
