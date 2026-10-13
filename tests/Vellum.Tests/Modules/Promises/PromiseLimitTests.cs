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

}
