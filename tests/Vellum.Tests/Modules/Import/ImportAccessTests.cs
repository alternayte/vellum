using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Vellum.Modules.Import.Proposals;
using Vellum.Modules.Modelling.Model;

namespace Vellum.Tests.Modules.Import;

/// <summary>
/// Every import endpoint under project authorization: a user of another workspace reaches
/// nothing, a Viewer reads and writes nothing, and an import of one project is not reachable
/// through another project.
/// </summary>
[Collection("Integration")]
public class ImportAccessTests(ImportHost import) : IClassFixture<ImportHost>
{
    private static async Task AssertStatus(HttpStatusCode expected, Task<HttpResponseMessage> request, string what)
    {
        var response = await request;
        Assert.True(response.StatusCode == expected, $"{what}: expected {expected}, got {response.StatusCode} {await response.Content.ReadAsStringAsync()}");
    }

    [Fact]
    public async Task Imports_are_closed_to_another_workspace_and_a_viewer_only_reads()
    {
        var s = await ImportScenario.CreateAsync(import);
        var scan = await s.ScanAsync();
        var id = scan.Id();
        var proposals = await s.ProposalsAsync(id);
        var one = proposals.Proposal("element", "orders").Id();
        var before = await s.Owner.GetStringAsync(s.Url($"/{id}/proposals?limit=200"));

        // The repository of the fixture is public, so a Viewer may read the lines of its evidence.
        var (sourcePath, sourceStart, sourceEnd) = (await s.EvidenceWindowsAsync(id))[0];
        string[] reads =
        [
            "", $"/{id}", $"/{id}/proposals", $"/{id}/proposals/{one}", $"/{id}/evidence",
            $"/{id}/source?path={Uri.EscapeDataString(sourcePath)}&startLine={sourceStart}&endLine={sourceEnd}",
        ];
        (string Path, object Body)[] writes =
        [
            ("", new { repository = "acme/other" }), ($"/{id}/cancel", new { }), ($"/{id}/resume", new { }), ($"/{id}/track", new { }),
            ($"/{id}/proposals/decide", new { accept = new[] { one } }),
        ];

        // A user of another workspace, with a project of their own.
        using var stranger = await TestHost.SignInAsync(import.Host.Factory);
        var (_, mine) = await TestHost.CreateProjectAsync(stranger);
        foreach (var path in reads)
        {
            await AssertStatus(HttpStatusCode.Forbidden, stranger.GetAsync(s.Url(path)), $"stranger GET {path}");
            if (path.Length > 0)
                await AssertStatus(HttpStatusCode.NotFound, stranger.GetAsync($"/api/projects/{mine}/imports{path}"), $"stranger GET {path} through their own project");
        }
        foreach (var (path, body) in writes)
        {
            await AssertStatus(HttpStatusCode.Forbidden, stranger.PostAsJsonAsync(s.Url(path), body), $"stranger POST {path}");
            if (path.Length > 0)
                await AssertStatus(HttpStatusCode.NotFound, stranger.PostAsJsonAsync($"/api/projects/{mine}/imports{path}", body), $"stranger POST {path} through their own project");
        }
        Assert.Empty((await stranger.GetFromJsonAsync<JsonObject>($"/api/projects/{mine}/imports"))!["items"]!.AsArray());

        // A Viewer of the workspace reads everything and decides nothing.
        using var viewer = await TestHost.SignInMemberAsync(import.Host.Factory, s.Owner, s.WorkspaceId, "Viewer");
        foreach (var path in reads)
            await AssertStatus(HttpStatusCode.OK, viewer.GetAsync(s.Url(path)), $"viewer GET {path}");
        foreach (var (path, body) in writes)
            await AssertStatus(HttpStatusCode.Forbidden, viewer.PostAsJsonAsync(s.Url(path), body), $"viewer POST {path}");

        // Nobody without a session.
        using var anonymous = TestHost.Client(import.Host.Factory);
        await AssertStatus(HttpStatusCode.Unauthorized, anonymous.GetAsync(s.Url($"/{id}/proposals")), "anonymous GET");
        await AssertStatus(HttpStatusCode.Unauthorized, anonymous.PostAsJsonAsync(s.Url($"/{id}/proposals/decide"), new { accept = new[] { one } }), "anonymous POST");

        // None of it changed anything: every proposal is pending and the model is empty.
        Assert.Equal(before, await s.Owner.GetStringAsync(s.Url($"/{id}/proposals?limit=200")));
        Assert.Empty(await s.ElementsAsync());
        Assert.Single(await s.AllAsync(s.Url()));

        // An Editor decides.
        using var editor = await TestHost.SignInMemberAsync(import.Host.Factory, s.Owner, s.WorkspaceId, "Editor");
        Assert.Empty((await s.DecideAsync(id, accept: [one], client: editor))["failed"]!.AsArray());
        var editorId = (await editor.GetFromJsonAsync<JsonObject>("/api/auth/me"))!.Text("id");
        Assert.Equal(editorId, Assert.Single(await s.ElementsAsync())["provenance"]!["acceptedBy"]!.GetValue<string>());
    }

    [Fact]
    public void A_route_matches_whatever_its_parameters_are_called()
    {
        Assert.Equal(ModelIndex.NormalRoute("GET /orders/{id}"), ModelIndex.NormalRoute("get  /orders/{orderId:guid}/"));
        Assert.Equal("POST /orders", ModelIndex.NormalRoute("post /orders/"));
        Assert.Equal("GET /", ModelIndex.NormalRoute("GET /"));
        Assert.NotEqual(ModelIndex.NormalRoute("GET /orders/{id}"), ModelIndex.NormalRoute("GET /orders/{id}/lines"));
        Assert.NotEqual(ModelIndex.NormalRoute("GET /orders/{id}"), ModelIndex.NormalRoute("DELETE /orders/{id}"));
        Assert.NotEqual(ModelIndex.NormalRoute("GET /Orders"), ModelIndex.NormalRoute("GET /orders"));
    }

    [Fact]
    public void A_stale_mark_is_set_and_cleared_by_events_and_is_not_part_of_what_a_fact_says()
    {
        var id = new ElementId(Guid.NewGuid());
        var provenance = new Provenance("github.com/acme/orders@abc:a.cs:1-2", "abc", null, Guid.NewGuid(), DateTimeOffset.UnixEpoch);
        var state = new object[]
        {
            new ModelEvent.ElementAdded(id, ElementKind.System, "Orders", null, null, null, ElementStatus.Current, null, []),
            new ModelEvent.ElementProvenanceRecorded(id, FactStatus.ObservedInSource, provenance),
        }.Aggregate(ModelState.Initial, ModelState.Evolve);
        var clean = state.Elements[id];

        // The mark travels on the provenance event with the status and provenance the fact has.
        var mark = new StaleMark("def", StaleDecider.EvidenceNotFound);
        var marking = Assert.IsType<ModelEvent.ElementProvenanceRecorded>(Assert.Single(StaleDecider.Mark(state, id, mark)));
        Assert.Equal((FactStatus.ObservedInSource, provenance, mark), (marking.Status, marking.Provenance, marking.Stale));
        var marked = ModelState.Evolve(state, marking);
        Assert.Equal(mark, marked.Elements[id].Stale);
        Assert.Empty(StaleDecider.Mark(marked, id, mark));

        // Two copies that differ in the mark alone are the same fact: a merge sees no edit.
        Assert.Equal(clean, marked.Elements[id]);

        // Recording the same claim again is a re-acceptance, and a scan can take the mark
        // away. Each clears it. An edit of a value, of any field, does not.
        var again = ModelDecider.RecordElementProvenance(marked, id, FactStatus.ObservedInSource, provenance);
        var reaccepted = Assert.IsType<Vellum.Shared.CommandResult<IReadOnlyList<object>>.Success>(again).Value;
        Assert.Null(reaccepted.Aggregate(marked, ModelState.Evolve).Elements[id].Stale);
        Assert.Equal(mark, ModelState.Evolve(marked, new ModelEvent.ElementRenamed(id, "Orders 2")).Elements[id].Stale);
        Assert.Null(StaleDecider.Mark(marked, id, null).Aggregate(marked, ModelState.Evolve).Elements[id].Stale);

        // A description is intent. Writing one says nothing about the evidence.
        Assert.Equal(mark, ModelState.Evolve(marked, new ModelEvent.ElementDescriptionChanged(id, "text")).Elements[id].Stale);
    }
}
