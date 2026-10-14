using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Vellum.Modules.Trace.Engine;
using Vellum.Modules.Trace.Scenarios;
using Vellum.Modules.Trace.Scenarios.Entities;
using Vellum.Shared;

namespace Vellum.Tests.Modules.Trace;

/// <summary>
/// A scenario document is untrusted input that is stored whole on every save. A save is
/// refused for structure and size only (400 or 413), never with a 500, and a document that
/// names things that do not exist is still saved.
/// </summary>
public class ScenarioLimitsTests(IntegrationFixture fixture) : IClassFixture<IntegrationFixture>
{
    private static readonly Guid Any = new("00000000-0000-0000-0000-000000000001");

    private static string Url(Guid projectId, string path = "") => $"/api/projects/{projectId}/scenarios{path}";

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    private static string Document(Guid id, string steps, string assertions = "[]", string name = "Limits") =>
        $$"""{ "id": "{{id}}", "name": "{{name}}", "steps": {{steps}}, "faults": [], "assertions": {{assertions}} }""";

    private static string Write(string id, string change = "null", string dataset = "orders") =>
        $$"""{ "type": "write", "id": "{{id}}", "by": "{{Any}}", "store": "{{Any}}", "dataset": "{{dataset}}", "change": {{change}} }""";

    /// <summary>A value inside <paramref name="levels"/> arrays.</summary>
    private static string Nested(int levels) => new string('[', levels) + "1" + new string(']', levels);

    private async Task<(HttpClient Client, Guid ProjectId)> StartAsync()
    {
        var client = await TestHost.SignInAsync(fixture.Factory);
        var (_, projectId) = await TestHost.CreateProjectAsync(client);
        return (client, projectId);
    }

    private static async Task<ErrorResponse> RefusedAsync(HttpResponseMessage response, HttpStatusCode status)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == status, $"{response.StatusCode}: {body[..Math.Min(body.Length, 500)]}");
        return (await response.Content.ReadFromJsonAsync<ErrorResponse>())!;
    }

    private static async Task<string> FieldAsync(Task<HttpResponseMessage> request) =>
        Assert.Single((await RefusedAsync(await request, HttpStatusCode.BadRequest)).Errors!).Field;

    [Fact]
    public async Task A_payload_nests_at_most_32_levels()
    {
        var (client, projectId) = await StartAsync();
        using var _ = client;

        await TestHost.Created(client.PostAsync(Url(projectId), Json(Document(Guid.NewGuid(), $"[{Write("w", Nested(ScenarioShape.MaxPayloadDepth))}]"))));

        // 61 levels once reached the event store and failed there with a 500.
        foreach (var levels in new[] { ScenarioShape.MaxPayloadDepth + 1, 61, 62, 100 })
        {
            Assert.Equal("steps[0].change",
                await FieldAsync(client.PostAsync(Url(projectId), Json(Document(Guid.NewGuid(), $"[{Write("w", Nested(levels))}]")))));
        }

        // Deeper than any reader allows: refused when the body is read.
        var tooDeep = await client.PostAsync(Url(projectId), Json(Document(Guid.NewGuid(), $"[{Write("w", Nested(200))}]")));
        Assert.Equal("invalid_scenario", (await RefusedAsync(tooDeep, HttpStatusCode.BadRequest)).Type);
    }

    [Fact]
    public async Task The_deepest_document_the_limits_allow_is_saved_read_replaced_and_run()
    {
        var (client, projectId) = await StartAsync();
        using var _ = client;

        // Step lists nested to the engine's limit through publish handlers, the deepest way to
        // nest in JSON, with a payload of the deepest allowed nesting at the bottom.
        string Publish(int list) => list == TraceLimits.MaxDepth
            ? Write("bottom", Nested(ScenarioShape.MaxPayloadDepth))
            : $$"""{ "type": "publish", "id": "p{{list}}", "from": "{{Any}}", "channel": "{{Any}}", "message": "{{Any}}", "payload": null, "headers": {}, "handlers": { "{{Any}}": [ {{Publish(list + 1)}} ] } }""";

        var id = Guid.NewGuid();
        var document = Document(id, $"[{Publish(1)}]");

        await TestHost.Created(client.PostAsync(Url(projectId), Json(document)));
        var read = await client.GetAsync(Url(projectId, $"/{id}"));
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.Contains(Nested(ScenarioShape.MaxPayloadDepth), (await read.Content.ReadAsStringAsync()).Replace(" ", ""));
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsync(Url(projectId, $"/{id}"), Json(document))).StatusCode);

        // Nothing it names exists, so the run reports issues. It does not fail.
        var run = await client.PostAsync(Url(projectId, $"/{id}/run"), null);
        Assert.Equal(HttpStatusCode.OK, run.StatusCode);
        Assert.NotEmpty((await run.Content.ReadFromJsonAsync<JsonObject>())!["issues"]!.AsArray());

        // One more level of step lists is refused, with the list that is too deep named.
        string Call(int list) => list > TraceLimits.MaxDepth
            ? Write("bottom")
            : $$"""{ "type": "call", "id": "c{{list}}", "from": "{{Any}}", "operation": "{{Any}}", "request": null, "response": null, "then": [ {{Call(list + 1)}} ], "onError": [] }""";
        var field = await FieldAsync(client.PostAsync(Url(projectId), Json(Document(Guid.NewGuid(), $"[{Call(1)}]"))));
        Assert.EndsWith(".then", field);
    }

    [Fact]
    public async Task A_scenario_has_at_most_the_steps_the_engine_plays()
    {
        var (client, projectId) = await StartAsync();
        using var _ = client;

        // The step list counts as one, as it does for the engine.
        string Steps(int count) => "[" + string.Join(",", Enumerable.Range(0, count).Select(i => Write($"w{i}"))) + "]";

        await TestHost.Created(client.PostAsync(Url(projectId), Json(Document(Guid.NewGuid(), Steps(TraceLimits.MaxSteps - 1)))));
        Assert.Equal("steps", await FieldAsync(client.PostAsync(Url(projectId), Json(Document(Guid.NewGuid(), Steps(TraceLimits.MaxSteps))))));
    }

    [Fact]
    public async Task A_document_or_a_payload_that_is_too_large_is_refused()
    {
        var (client, projectId) = await StartAsync();
        using var _ = client;

        // A 40 MB dataset name was stored, and stored again on every save.
        var huge = await client.PostAsync(Url(projectId), Json(Document(Guid.NewGuid(), $"[{Write("w", dataset: new string('d', 40 * 1024 * 1024))}]")));
        Assert.Equal("payload_too_large", (await RefusedAsync(huge, HttpStatusCode.RequestEntityTooLarge)).Type);

        // One payload over the engine's limit.
        var payload = $"\"{new string('p', TraceLimits.MaxPayloadBytes)}\"";
        Assert.Equal("steps[0].change", await FieldAsync(client.PostAsync(Url(projectId), Json(Document(Guid.NewGuid(), $"[{Write("w", payload)}]")))));

        // Many payloads inside the limit: the request fits, the stored document does not.
        var allowed = $"\"{new string('p', TraceLimits.MaxPayloadBytes - 100)}\"";
        var steps = "[" + string.Join(",", Enumerable.Range(0, 10).Select(i => Write($"w{i}", allowed))) + "]";
        var id = Guid.NewGuid();
        var tooLarge = await client.PostAsync(Url(projectId), Json(Document(id, steps)));
        Assert.Equal("scenario_too_large", (await RefusedAsync(tooLarge, HttpStatusCode.RequestEntityTooLarge)).Type);

        // A replace is held to the same limits as a create.
        await TestHost.Created(client.PostAsync(Url(projectId), Json(Document(id, "[]"))));
        var replaced = await client.PutAsync(Url(projectId, $"/{id}"), Json(Document(id, steps)));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, replaced.StatusCode);
        Assert.Equal("steps[0].change", await FieldAsync(client.PutAsync(Url(projectId, $"/{id}"), Json(Document(id, $"[{Write("w", Nested(40))}]")))));
    }

    [Theory]
    [InlineData("""{ "note": "a\u0000b" }""")]
    [InlineData("""{ "a\u0000b": 1 }""")]
    [InlineData("""[[{ "deep": ["\u0000"] }]]""")]
    [InlineData("\"\\ud83d\"")]
    public async Task A_payload_with_text_the_event_store_cannot_keep_is_refused(string payload)
    {
        var (client, projectId) = await StartAsync();
        using var _ = client;

        Assert.Equal("steps[0].change",
            await FieldAsync(client.PostAsync(Url(projectId), Json(Document(Guid.NewGuid(), $"[{Write("w", payload)}]")))));
    }

    [Fact]
    public async Task Identifiers_and_names_inside_a_scenario_follow_the_text_rule()
    {
        var (client, projectId) = await StartAsync();
        using var _ = client;

        Task<string> Field(string steps, string assertions = "[]") =>
            FieldAsync(client.PostAsync(Url(projectId), Json(Document(Guid.NewGuid(), steps, assertions))));

        Assert.Equal("steps[0].id", await Field($"[{Write("a\\u0000b")}]"));
        Assert.Equal("steps[0].id", await Field($"[{Write(new string('i', TextRule.MaxIdentifier + 1))}]"));
        Assert.Equal("steps[0].dataset", await Field($"[{Write("w", dataset: "a\\u0000b")}]"));
        Assert.Equal("steps[0].dataset", await Field($"[{Write("w", dataset: new string('d', TextRule.MaxName + 1))}]"));
        Assert.Equal("assertions[0].id", await Field("[]", """[ { "type": "outcome", "id": "a\u0000b", "step": "w", "expected": "Succeeded" } ]"""));
        Assert.Equal("assertions[0].id", await Field("[]", $$"""[ { "type": "outcome", "id": "{{new string('i', TextRule.MaxIdentifier + 1)}}", "step": "w", "expected": "Succeeded" } ]"""));
        Assert.Equal("steps[0].headers.x-trace", await Field(
            $$"""[ { "type": "publish", "id": "p", "from": "{{Any}}", "channel": "{{Any}}", "message": "{{Any}}", "payload": null, "headers": { "x-trace": "a\u0000b" }, "handlers": {} } ]"""));

        // The limits themselves are allowed, and so is a draft with no step IDs at all.
        var atLimit = Document(Guid.NewGuid(),
            $"[{Write(new string('i', TextRule.MaxIdentifier), dataset: new string('d', TextRule.MaxName))}, {{ \"type\": \"write\" }}]",
            name: new string('n', TextRule.MaxName));
        await TestHost.Created(client.PostAsync(Url(projectId), Json(atLimit)));
    }

    [Fact]
    public async Task The_type_of_a_step_or_an_assertion_may_stand_anywhere_in_its_object()
    {
        var (client, projectId) = await StartAsync();
        using var _ = client;

        var id = Guid.NewGuid();
        var document = $$"""
            {
              "name": "Type last", "id": "{{id}}",
              "steps": [
                { "id": "c", "from": "{{Any}}", "operation": "{{Any}}", "request": null, "response": null, "onError": [],
                  "then": [ { "id": "w", "by": "{{Any}}", "store": "{{Any}}", "dataset": "orders", "change": { "type": "not a step" }, "type": "write" } ],
                  "type": "call" }
              ],
              "faults": [],
              "assertions": [ { "id": "a", "step": "w", "expected": "Succeeded", "type": "outcome" } ]
            }
            """;

        await TestHost.Created(client.PostAsync(Url(projectId), Json(document)));

        // The stored event is read back with the same freedom.
        var read = (await client.GetFromJsonAsync<JsonObject>(Url(projectId, $"/{id}")))!;
        Assert.Equal("call", (string)read["steps"]![0]!["type"]!);
        Assert.Equal("write", (string)read["steps"]![0]!["then"]![0]!["type"]!);
        Assert.Equal("outcome", (string)read["assertions"]![0]!["type"]!);
    }

    [Theory]
    [InlineData("""[ { "id": "w" } ]""", "[]", "steps[0]", "no \"type\"")]
    [InlineData("""[ { "type": "call", "id": "c", "then": [ { "id": "w", "type": "teleport" } ], "onError": [] } ]""", "[]", "steps[0].then[0]", "not known")]
    [InlineData("""[ { "type": "parallel", "id": "p", "branches": [ [], [ { "id": "w", "type": 7 } ] ] } ]""", "[]", "steps[0].branches[1][0]", "not known")]
    [InlineData("[]", """[ { "id": "a", "step": "w", "expected": "Succeeded" } ]""", "assertions[0]", "no \"type\"")]
    public async Task A_missing_or_unknown_type_is_a_400_that_says_where_and_what_is_allowed(
        string steps, string assertions, string where, string what)
    {
        var (client, projectId) = await StartAsync();
        using var _ = client;

        var response = await client.PostAsync(Url(projectId), Json(Document(Guid.NewGuid(), steps, assertions)));

        var error = await RefusedAsync(response, HttpStatusCode.BadRequest);
        Assert.Equal("invalid_scenario", error.Type);
        Assert.StartsWith($"{where}:", error.Detail);
        Assert.Contains(what, error.Detail);
        Assert.Contains(where.StartsWith("steps") ? "call, publish, write, parallel, branch" : "outcome, event-count", error.Detail);
    }

    [Fact]
    public async Task A_taken_ID_gets_one_answer_whatever_holds_it()
    {
        var factory = fixture.Factory;
        using var owner = await TestHost.SignInAsync(factory);
        var (_, ownerProject) = await TestHost.CreateProjectAsync(owner);

        // What the other workspace holds: a scenario, a deleted scenario, a schema and a model branch.
        Guid scenario = Guid.NewGuid(), deleted = Guid.NewGuid(), schema = Guid.NewGuid();
        await TestHost.Created(owner.PostAsync(Url(ownerProject), Json(Document(scenario, "[]"))));
        await TestHost.Created(owner.PostAsync(Url(ownerProject), Json(Document(deleted, "[]"))));
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync(Url(ownerProject, $"/{deleted}"))).StatusCode);
        await TestHost.Created(owner.PostAsJsonAsync($"/api/projects/{ownerProject}/schemas", new { id = schema, name = "S", content = """{"type":"object"}""" }));
        var draft = (await (await TestHost.Created(owner.PostAsJsonAsync($"/api/projects/{ownerProject}/drafts", new { id = Guid.NewGuid(), name = "D" })))
            .Content.ReadFromJsonAsync<Vellum.Modules.Drafts.DraftDto>())!;

        // A stranger in another workspace tries each ID, and one of their own.
        using var stranger = await TestHost.SignInAsync(factory);
        var (_, strangerProject) = await TestHost.CreateProjectAsync(stranger);
        var own = Guid.NewGuid();
        await TestHost.Created(stranger.PostAsync(Url(strangerProject), Json(Document(own, "[]"))));

        var answers = new List<string>();
        foreach (var id in new[] { scenario, deleted, schema, draft.StreamId, own })
        {
            var response = await stranger.PostAsync(Url(strangerProject), Json(Document(id, "[]")));
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            answers.Add(await response.Content.ReadAsStringAsync());
        }

        Assert.Single(answers.Distinct());
        Assert.Contains(ScenarioDecider.IdInUse, answers[0]);
    }

    [Fact]
    public async Task A_project_holds_at_most_2000_scenarios()
    {
        var factory = fixture.Factory;
        using var client = await TestHost.SignInAsync(factory);
        var (_, projectId) = await TestHost.CreateProjectAsync(client);

        // The limit counts the rows of the list. They are written here directly: two thousand
        // requests would test nothing more.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ScenariosDbContext>();
            db.Scenarios.AddRange(Enumerable.Range(0, ScenarioDecider.MaxPerProject - 1)
                .Select(i => new ScenarioEntity { Id = Guid.NewGuid(), ProjectId = projectId, Name = $"Scenario {i}" }));
            await db.SaveChangesAsync();
        }

        await TestHost.Created(client.PostAsync(Url(projectId), Json(Document(Guid.NewGuid(), "[]"))));

        var refused = await RefusedAsync(await client.PostAsync(Url(projectId), Json(Document(Guid.NewGuid(), "[]"))), HttpStatusCode.Conflict);
        Assert.Contains("2000", refused.Title);

        // Another project is not affected.
        var (_, other) = await TestHost.CreateProjectAsync(client);
        await TestHost.Created(client.PostAsync(Url(other), Json(Document(Guid.NewGuid(), "[]"))));
    }
}
