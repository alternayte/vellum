using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Vellum.Modules.Promises;

namespace Vellum.Tests.Modules.Promises;

/// <summary>The bounds on verification runs, on a host that is given small ones.</summary>
[Collection("Integration")]
public class PromiseLimitTests(IntegrationFixture fixture)
{
    private static string Url(Guid projectId, string path = "") => $"/api/projects/{projectId}/promises{path}";

    private static Task<HttpResponseMessage> Post(HttpClient client, string url, JsonNode? body = null) =>
        client.PostAsync(url, body is null ? null : OrdersReference.Json(body));

    [Fact]
    public async Task Runs_of_one_obligation_are_bounded_and_rate_limited()
    {
        using var factory = TestHost.Create(fixture.ConnectionString,
            configure: services => services.AddSingleton(new PromiseLimits(MaxRunsPerObligation: 3, RunsPerMinute: 6)));
        var owner = await TestHost.SignInAsync(factory);
        var (_, projectId) = await TestHost.CreateProjectAsync(owner);
        var orders = await OrdersReference.SeedAsync(owner, projectId);
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync(Url(projectId, "/prefix"), new { prefix = "ORD" })).StatusCode);
        var created = await TestHost.Created(Post(owner, Url(projectId), orders.Ord014(requiresPattern: false)));
        var id = (await created.Content.ReadFromJsonAsync<JsonObject>())!["id"]!.GetValue<Guid>();
        await TestHost.Created(Post(owner, Url(projectId, $"/{id}/obligations"), new JsonObject
        {
            ["kind"] = "scenario-check",
            ["title"] = "The order is accepted",
            ["recipe"] = new JsonObject
            {
                ["text"] = "Play the scenario.",
                ["scenarioCheck"] = new JsonObject
                {
                    ["scenarioId"] = orders.ScenarioId.ToString(), ["faults"] = new JsonArray(),
                    ["expect"] = new JsonArray(new JsonObject { ["type"] = "outcome", ["id"] = "order-accepted", ["step"] = "place", ["expected"] = "Succeeded" }),
                },
            },
        }));
        await orders.ApproveAsync(id);

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 8; i++)
            statuses.Add((await Post(owner, Url(projectId, $"/{id}/obligations/O1/run"))).StatusCode);

        // Three runs are recorded; the next are refused because the obligation is full; then the caller is over its rate.
        Assert.Equal(
            [HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.Conflict, HttpStatusCode.Conflict, HttpStatusCode.Conflict,
                HttpStatusCode.TooManyRequests, HttpStatusCode.TooManyRequests],
            statuses);
        var limited = await Post(owner, Url(projectId, $"/{id}/obligations/O1/run"));
        Assert.Equal("rate_limited", (await limited.Content.ReadFromJsonAsync<JsonObject>())!["type"]!.GetValue<string>());
        Assert.NotNull(limited.Headers.RetryAfter);
        Assert.Equal(3, (await owner.GetFromJsonAsync<JsonObject>(Url(projectId, $"/{id}/runs")))!["items"]!.AsArray().Count);
    }

    [Fact]
    public async Task Draft_writes_of_a_person_and_of_the_tokens_of_that_person_share_one_rate_window()
    {
        using var factory = TestHost.Create(fixture.ConnectionString,
            configure: services => services.AddSingleton(new PromiseLimits(DraftWritesPerMinute: 4)));
        var owner = await TestHost.SignInAsync(factory);
        var (workspaceId, projectId) = await TestHost.CreateProjectAsync(owner);
        var orders = await OrdersReference.SeedAsync(owner, projectId);
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync(Url(projectId, "/prefix"), new { prefix = "ORD" })).StatusCode);
        var agent = await TestHost.CreateAgentAsync(factory, owner, workspaceId, ["propose"]);

        // A create and a change by the person, a create and a generation by the token: the window is full.
        var created = await TestHost.Created(Post(owner, Url(projectId), orders.Ord014(requiresPattern: false)));
        var id = (await created.Content.ReadFromJsonAsync<JsonObject>())!["id"]!.GetValue<Guid>();
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsync(Url(projectId, $"/{id}"), OrdersReference.Json(orders.Ord014(requiresPattern: false)))).StatusCode);
        await TestHost.Created(Post(agent.Client, Url(projectId), orders.Ord014(requiresPattern: false)));
        Assert.Equal(HttpStatusCode.OK, (await Post(agent.Client, Url(projectId, $"/{id}/obligations/generate"))).StatusCode);

        // Every draft write is refused then, for the token and for the person, and nothing is written.
        foreach (var refused in new[]
        {
            await Post(agent.Client, Url(projectId), orders.Ord014(requiresPattern: false)),
            await agent.Client.PutAsync(Url(projectId, $"/{id}"), OrdersReference.Json(orders.Ord014(requiresPattern: false))),
            await Post(agent.Client, Url(projectId, $"/{id}/obligations/generate")),
            await Post(owner, Url(projectId, $"/{id}/obligations"), new JsonObject { ["kind"] = "contract-test", ["title"] = "A test", ["recipe"] = new JsonObject { ["text"] = "Run it." } }),
            await Post(owner, Url(projectId), orders.Ord014(requiresPattern: false)),
        })
        {
            Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
            Assert.Equal("rate_limited", (await refused.Content.ReadFromJsonAsync<JsonObject>())!["type"]!.GetValue<string>());
            Assert.NotNull(refused.Headers.RetryAfter);
        }
        Assert.Equal(2, (await owner.GetFromJsonAsync<JsonObject>(Url(projectId)))!["items"]!.AsArray().Count);
    }

}
