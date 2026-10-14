using System.Net;
using System.Net.Http.Json;
using System.Text;
using Vellum.Modules.Drafts;

namespace Vellum.Tests.Modules;

/// <summary>
/// Two users, each alone in a workspace. Nothing of the first is reachable by the second:
/// not through the first user's project, and not through the second user's own project with
/// an ID or a <c>?branchId=</c> of the first.
/// </summary>
public class WorkspaceIsolationTests(IntegrationFixture fixture) : IClassFixture<IntegrationFixture>
{
    private static string Url(Guid projectId, string path, Guid? branchId = null) =>
        $"/api/projects/{projectId}/{path}" + (branchId is null ? "" : $"?branchId={branchId}");

    private static StringContent Scenario(Guid id, string name = "Private") => new(
        $$"""{ "id": "{{id}}", "name": "{{name}}", "steps": [], "faults": [], "assertions": [] }""", Encoding.UTF8, "application/json");

    private static async Task AssertStatus(HttpStatusCode expected, Task<HttpResponseMessage> request, string what)
    {
        var response = await request;
        Assert.True(response.StatusCode == expected, $"{what}: expected {expected}, got {response.StatusCode} {await response.Content.ReadAsStringAsync()}");
    }

    [Fact]
    public async Task Contracts_scenarios_and_runs_of_one_workspace_are_closed_to_a_user_of_another()
    {
        var factory = fixture.Factory;

        // The owner's workspace: a model with contracts on main and on a draft, and a scenario.
        using var owner = await TestHost.SignInAsync(factory);
        var (_, theirs) = await TestHost.CreateProjectAsync(owner);
        Guid element = Guid.NewGuid(), operation = Guid.NewGuid(), channel = Guid.NewGuid(), subscription = Guid.NewGuid(), scenario = Guid.NewGuid();
        Guid queue = Guid.NewGuid(), binding = Guid.NewGuid();
        await TestHost.Created(owner.PostAsJsonAsync(Url(theirs, "elements"), new { id = element, kind = "system", name = "Secret system" }));
        await TestHost.Created(owner.PostAsJsonAsync(Url(theirs, "operations"), new { id = operation, ownerId = element, name = "Secret operation", transport = "http" }));
        await TestHost.Created(owner.PostAsJsonAsync(Url(theirs, "channels"), new { id = channel, name = "secret-channel", kind = "topic" }));
        await TestHost.Created(owner.PostAsJsonAsync(Url(theirs, "subscriptions"), new { id = subscription, channelId = channel, consumerId = element }));
        await TestHost.Created(owner.PostAsJsonAsync(Url(theirs, "channels"), new { id = queue, name = "secret-queue", kind = "queue" }));
        await TestHost.Created(owner.PostAsJsonAsync(Url(theirs, "bindings"), new { id = binding, sourceChannelId = channel, targetChannelId = queue, routingKey = "secret.key" }));
        await TestHost.Created(owner.PostAsync(Url(theirs, "scenarios"), Scenario(scenario)));
        var draft = (await (await TestHost.Created(owner.PostAsJsonAsync(Url(theirs, "drafts"), new { id = Guid.NewGuid(), name = "Secret draft" })))
            .Content.ReadFromJsonAsync<DraftDto>())!;
        await TestHost.Created(owner.PostAsJsonAsync(Url(theirs, "channels", draft.StreamId), new { id = Guid.NewGuid(), name = "secret-draft-channel", kind = "queue" }));

        string[] reads = ["operations", "channels", "subscriptions", "bindings", "scenarios", $"scenarios/{scenario}"];
        var before = new List<string>();
        foreach (var path in reads)
            before.Add(await owner.GetStringAsync(Url(theirs, path)));
        var draftBefore = await owner.GetStringAsync(Url(theirs, "channels", draft.StreamId));

        // The stranger's workspace, with a scenario of their own.
        using var stranger = await TestHost.SignInAsync(factory);
        var (_, mine) = await TestHost.CreateProjectAsync(stranger);
        var ownScenario = Guid.NewGuid();
        await TestHost.Created(stranger.PostAsync(Url(mine, "scenarios"), Scenario(ownScenario, "Mine")));

        // 1. The owner's project: every read and every write is forbidden, with or without the branch.
        foreach (var branch in new Guid?[] { null, draft.StreamId })
        {
            foreach (var path in new[] { "operations", "channels", "subscriptions", "bindings" })
                await AssertStatus(HttpStatusCode.Forbidden, stranger.GetAsync(Url(theirs, path, branch)), $"GET {path}");
            await AssertStatus(HttpStatusCode.Forbidden, stranger.PostAsJsonAsync(Url(theirs, "bindings", branch), new { id = Guid.NewGuid(), sourceChannelId = channel, targetChannelId = queue, routingKey = "planted" }), "POST bindings");
            await AssertStatus(HttpStatusCode.Forbidden, stranger.PatchAsJsonAsync(Url(theirs, $"bindings/{binding}", branch), new { routingKey = "renamed" }), "PATCH binding");
            await AssertStatus(HttpStatusCode.Forbidden, stranger.DeleteAsync(Url(theirs, $"bindings/{binding}", branch)), "DELETE binding");

            await AssertStatus(HttpStatusCode.Forbidden, stranger.PostAsJsonAsync(Url(theirs, "channels", branch), new { id = Guid.NewGuid(), name = "planted", kind = "topic" }), "POST channels");
            await AssertStatus(HttpStatusCode.Forbidden, stranger.PostAsJsonAsync(Url(theirs, "operations", branch), new { id = Guid.NewGuid(), ownerId = element, name = "planted", transport = "http" }), "POST operations");
            await AssertStatus(HttpStatusCode.Forbidden, stranger.PostAsJsonAsync(Url(theirs, "subscriptions", branch), new { id = Guid.NewGuid(), channelId = channel, consumerId = element }), "POST subscriptions");
            await AssertStatus(HttpStatusCode.Forbidden, stranger.PatchAsJsonAsync(Url(theirs, $"operations/{operation}", branch), new { name = "renamed" }), "PATCH operation");
            await AssertStatus(HttpStatusCode.Forbidden, stranger.PatchAsJsonAsync(Url(theirs, $"channels/{channel}", branch), new { name = "renamed" }), "PATCH channel");
            await AssertStatus(HttpStatusCode.Forbidden, stranger.PatchAsJsonAsync(Url(theirs, $"subscriptions/{subscription}", branch), new { retry = new { maxAttempts = 2, backoffMs = 1 } }), "PATCH subscription");
            await AssertStatus(HttpStatusCode.Forbidden, stranger.DeleteAsync(Url(theirs, $"operations/{operation}", branch)), "DELETE operation");
            await AssertStatus(HttpStatusCode.Forbidden, stranger.DeleteAsync(Url(theirs, $"subscriptions/{subscription}", branch)), "DELETE subscription");
            await AssertStatus(HttpStatusCode.Forbidden, stranger.DeleteAsync(Url(theirs, $"channels/{channel}", branch)), "DELETE channel");
            await AssertStatus(HttpStatusCode.Forbidden, stranger.PostAsync(Url(theirs, $"scenarios/{scenario}/run", branch), null), "POST run");
        }

        await AssertStatus(HttpStatusCode.Forbidden, stranger.GetAsync(Url(theirs, "scenarios")), "GET scenarios");
        await AssertStatus(HttpStatusCode.Forbidden, stranger.GetAsync(Url(theirs, $"scenarios/{scenario}")), "GET scenario");
        await AssertStatus(HttpStatusCode.Forbidden, stranger.PostAsync(Url(theirs, "scenarios"), Scenario(Guid.NewGuid())), "POST scenario");
        await AssertStatus(HttpStatusCode.Forbidden, stranger.PutAsync(Url(theirs, $"scenarios/{scenario}"), Scenario(scenario, "Taken over")), "PUT scenario");
        await AssertStatus(HttpStatusCode.Forbidden, stranger.DeleteAsync(Url(theirs, $"scenarios/{scenario}")), "DELETE scenario");

        // 2. The stranger's own project with the owner's branch: the branch is not one of this project.
        foreach (var path in new[] { "operations", "channels", "subscriptions", "bindings" })
            await AssertStatus(HttpStatusCode.BadRequest, stranger.GetAsync(Url(mine, path, draft.StreamId)), $"GET {path} on a foreign branch");
        await AssertStatus(HttpStatusCode.BadRequest, stranger.PostAsJsonAsync(Url(mine, "channels", draft.StreamId), new { id = Guid.NewGuid(), name = "planted", kind = "topic" }), "POST channel on a foreign branch");
        await AssertStatus(HttpStatusCode.BadRequest, stranger.PatchAsJsonAsync(Url(mine, $"channels/{channel}", draft.StreamId), new { name = "renamed" }), "PATCH channel on a foreign branch");
        await AssertStatus(HttpStatusCode.BadRequest, stranger.DeleteAsync(Url(mine, $"operations/{operation}", draft.StreamId)), "DELETE operation on a foreign branch");
        await AssertStatus(HttpStatusCode.BadRequest, stranger.PostAsync(Url(mine, $"scenarios/{ownScenario}/run", draft.StreamId), null), "run on a foreign branch");

        // 3. The stranger's own project with the owner's IDs: they name nothing here.
        await AssertStatus(HttpStatusCode.NotFound, stranger.PatchAsJsonAsync(Url(mine, $"operations/{operation}"), new { name = "renamed" }), "PATCH foreign operation");
        await AssertStatus(HttpStatusCode.NotFound, stranger.PatchAsJsonAsync(Url(mine, $"channels/{channel}"), new { name = "renamed" }), "PATCH foreign channel");
        await AssertStatus(HttpStatusCode.NotFound, stranger.DeleteAsync(Url(mine, $"subscriptions/{subscription}")), "DELETE foreign subscription");
        await AssertStatus(HttpStatusCode.NotFound, stranger.DeleteAsync(Url(mine, $"bindings/{binding}")), "DELETE foreign binding");
        await AssertStatus(HttpStatusCode.NotFound, stranger.PatchAsJsonAsync(Url(mine, $"bindings/{binding}"), new { routingKey = "renamed" }), "PATCH foreign binding");
        await AssertStatus(HttpStatusCode.BadRequest, stranger.PostAsJsonAsync(Url(mine, "bindings"), new { id = Guid.NewGuid(), sourceChannelId = channel, targetChannelId = queue }), "binding between foreign channels");
        await AssertStatus(HttpStatusCode.NotFound, stranger.GetAsync(Url(mine, $"scenarios/{scenario}")), "GET foreign scenario");
        await AssertStatus(HttpStatusCode.NotFound, stranger.PutAsync(Url(mine, $"scenarios/{scenario}"), Scenario(scenario, "Taken over")), "PUT foreign scenario");
        await AssertStatus(HttpStatusCode.NotFound, stranger.DeleteAsync(Url(mine, $"scenarios/{scenario}")), "DELETE foreign scenario");
        await AssertStatus(HttpStatusCode.NotFound, stranger.PostAsync(Url(mine, $"scenarios/{scenario}/run"), null), "run foreign scenario");
        await AssertStatus(HttpStatusCode.BadRequest, stranger.PostAsJsonAsync(Url(mine, "subscriptions"), new { id = Guid.NewGuid(), channelId = channel, consumerId = element }), "subscription to a foreign channel");

        // A run of the stranger's own scenario labels nothing of the owner's model.
        var run = await stranger.PostAsync(Url(mine, $"scenarios/{ownScenario}/run"), null);
        Assert.Equal(HttpStatusCode.OK, run.StatusCode);
        Assert.DoesNotContain("Secret", await run.Content.ReadAsStringAsync());
        foreach (var path in new[] { "operations", "channels", "subscriptions", "bindings", "scenarios" })
            Assert.DoesNotContain("ecret", await stranger.GetStringAsync(Url(mine, path)));

        // Nothing of the owner's changed.
        for (var i = 0; i < reads.Length; i++)
            Assert.Equal(before[i], await owner.GetStringAsync(Url(theirs, reads[i])));
        Assert.Equal(draftBefore, await owner.GetStringAsync(Url(theirs, "channels", draft.StreamId)));
    }

    [Fact]
    public async Task The_project_its_revisions_and_its_comparisons_are_closed_to_a_user_of_another_workspace()
    {
        var factory = fixture.Factory;

        // The owner's project: an element, a named revision and a draft.
        using var owner = await TestHost.SignInAsync(factory);
        var (_, theirs) = await TestHost.CreateProjectAsync(owner);
        await TestHost.Created(owner.PostAsJsonAsync(Url(theirs, "elements"), new { id = Guid.NewGuid(), kind = "system", name = "Secret system" }));
        var revision = Guid.NewGuid();
        await TestHost.Created(owner.PostAsJsonAsync(Url(theirs, "revisions"), new { id = revision, name = "Secret revision" }));
        var draft = (await (await TestHost.Created(owner.PostAsJsonAsync(Url(theirs, "drafts"), new { id = Guid.NewGuid(), name = "Secret draft" })))
            .Content.ReadFromJsonAsync<DraftDto>())!;
        var compare = $"model/compare?left=revision:{revision}&right={draft.StreamId}";
        Assert.Contains("Secret system", await owner.GetStringAsync(Url(theirs, $"revisions/{revision}/model")));
        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync(Url(theirs, compare))).StatusCode);

        using var stranger = await TestHost.SignInAsync(factory);
        var (_, mine) = await TestHost.CreateProjectAsync(stranger);

        async Task AssertClosed(HttpStatusCode expected, string url, string what)
        {
            var response = await stranger.GetAsync(url);
            var body = await response.Content.ReadAsStringAsync();
            Assert.True(response.StatusCode == expected, $"{what}: expected {expected}, got {response.StatusCode} {body}");
            Assert.DoesNotContain("ecret", body);
            Assert.DoesNotContain(theirs.ToString(), body);
        }

        // 1. The owner's project: it reads as a project that does not exist, and its models are forbidden.
        await AssertClosed(HttpStatusCode.NotFound, $"/api/projects/{theirs}", "GET project");
        await AssertClosed(HttpStatusCode.Forbidden, Url(theirs, $"revisions/{revision}/model"), "GET revision model");
        await AssertClosed(HttpStatusCode.Forbidden, Url(theirs, compare), "GET compare");
        await AssertClosed(HttpStatusCode.Forbidden, Url(theirs, "model/compare?left=main&right=main"), "GET compare of main");

        // 2. The stranger's own project with the owner's revision and draft: they name nothing here.
        await AssertClosed(HttpStatusCode.NotFound, Url(mine, $"revisions/{revision}/model"), "GET foreign revision model");
        await AssertClosed(HttpStatusCode.BadRequest, Url(mine, $"model/compare?left=revision:{revision}&right=main"), "compare with a foreign revision");
        await AssertClosed(HttpStatusCode.BadRequest, Url(mine, $"model/compare?left=main&right={draft.StreamId}"), "compare with a foreign draft");
    }
}
