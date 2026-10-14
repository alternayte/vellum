using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vellum.Modules.Import;
using Vellum.Modules.Import.Sources;
using Vellum.Shared;

namespace Vellum.Tests.Modules.Import;

/// <summary>
/// Sources and Snapshots: a scan of a repository is a scan of its github Source; a Snapshot is
/// identified by its manifest; citations keep their form; staleness stays within one Source;
/// and an element of another Source is never taken by its name.
/// </summary>
public class SourceTests(ImportHost import) : IClassFixture<ImportHost>
{
    [Fact]
    public void A_blob_SHA_is_the_one_git_computes_and_a_Snapshot_ID_is_the_SHA256_of_the_sorted_manifest()
    {
        // `git hash-object` of an empty file and of "hello\n".
        Assert.Equal("e69de29bb2d1d6434b8b29ae775ad8c2e48c5391", GitBlob.Sha([]));
        Assert.Equal("ce013625030ba8dba906f756967f9e9ca394464a", GitBlob.Sha("hello\n"u8));

        var a = new ManifestEntry("src/a.cs", 6, GitBlob.Sha("hello\n"u8));
        var b = new ManifestEntry("README.md", 0, GitBlob.Sha([]));
        // Ordered by the UTF-8 bytes of the path: U+E000 (EE 80 80) before U+1F600 (F0 9F 98 80),
        // which UTF-16 order would put the other way round.
        var c = new ManifestEntry("\U0001F600.txt", 6, GitBlob.Sha("hello\n"u8));
        var d = new ManifestEntry(".txt", 0, GitBlob.Sha([]));

        var id = SnapshotManifest.Id([a, b, c, d]);
        Assert.Equal(id, SnapshotManifest.Id([d, c, b, a]));
        var expected = string.Concat(
            "README.md\0" + "0\0" + "e69de29bb2d1d6434b8b29ae775ad8c2e48c5391\n",
            "src/a.cs\0" + "6\0" + "ce013625030ba8dba906f756967f9e9ca394464a\n",
            ".txt\0" + "0\0" + "e69de29bb2d1d6434b8b29ae775ad8c2e48c5391\n",
            "\U0001F600.txt\0" + "6\0" + "ce013625030ba8dba906f756967f9e9ca394464a\n");
        Assert.Equal(Encoding.UTF8.GetBytes(expected), SnapshotManifest.Canonical([c, a, d, b]));
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(expected))), id);

        // Any change of path, size or content is another Snapshot.
        Assert.NotEqual(id, SnapshotManifest.Id([a with { Size = 7 }, b, c, d]));
        Assert.NotEqual(id, SnapshotManifest.Id([a with { Path = "src/b.cs" }, b, c, d]));
        Assert.NotEqual(id, SnapshotManifest.Id([a with { BlobSha = GitBlob.Sha("hello"u8) }, b, c, d]));
        Assert.NotEqual(id, SnapshotManifest.Id([a, b, c]));
    }

    [Fact]
    public async Task A_scan_of_a_repository_is_a_scan_of_its_github_Source_with_a_manifest_Snapshot_and_the_same_citations()
    {
        var s = await ImportScenario.CreateAsync(import);
        var scan = await s.ScanAsync();
        Assert.Equal("completed", scan.Text("status"));

        // The existing route made the Source.
        var source = Assert.Single(await SourcesAsync(s));
        Assert.Equal(("github", "acme/orders", $"{s.GitHubHost}/acme/orders", scan.Text("sourceId")),
            (source.Text("kind"), source.Text("name"), source.Text("citation"), source.Text("id")));

        // The Snapshot is the manifest of the files the scan read, with the blob SHAs git gives
        // those bytes, and the commit SHA is its revision.
        var snapshot = source["latestSnapshot"]!.AsObject();
        var read = await ReadPathsAsync(s, scan.Id());
        var files = s.GitHub.Files(ImportScenario.Commit1);
        var manifest = read.Select(path => new ManifestEntry(path, files[path].Length, GitBlob.Sha(files[path]))).ToList();
        Assert.Equal((SnapshotManifest.Id(manifest), manifest.Count), (snapshot.Text("snapshotId"), snapshot["files"]!.GetValue<int>()));
        Assert.Equal((ImportScenario.Commit1, ImportScenario.Commit1), (snapshot.Text("commitSha"), snapshot.Text("revision")));
        Assert.Equal(snapshot.Text("snapshotId"), scan.Text("snapshotId"));

        // A GitHub citation keeps its form: <host>/<owner>/<name>@<sha>:path:start-end.
        var proposals = await s.ProposalsAsync(scan.Id());
        Assert.All(proposals.SelectMany(p => p["evidence"]!.AsArray()), e => Assert.Equal(
            $"{s.GitHubHost}/acme/orders@{ImportScenario.Commit1}:{e!["path"]}:{e["startLine"]}-{e["endLine"]}", e["pointer"]!.GetValue<string>()));
        Assert.All(proposals, p => Assert.Equal(source.Text("id"), p.Text("sourceId")));

        // A scan through the Source route reads the same commit: the same Snapshot.
        var again = await StartSourceScanAsync(s, source.Id(), new { });
        await s.DrainAsync();
        Assert.Equal(snapshot.Text("snapshotId"), (await s.GetAsync(again)).Text("snapshotId"));
        var snapshots = await s.Owner.GetFromJsonAsync<JsonArray>($"/api/projects/{s.ProjectId}/sources/{source.Id()}/snapshots");
        Assert.Equal(2, snapshots!.Count);
        var same = await s.Owner.GetFromJsonAsync<JsonObject>($"/api/projects/{s.ProjectId}/sources/{source.Id()}/snapshots/compare?base={scan.Id()}&head={again}");
        Assert.True(same!["sameSnapshot"]!.GetValue<bool>());
        Assert.Empty(same["files"]!["changed"]!.AsArray());
        Assert.Empty(same["facts"]!["removed"]!.AsArray());

        // One Source per place; a kind this instance cannot read is refused by name.
        var twice = await s.Owner.PostAsJsonAsync($"/api/projects/{s.ProjectId}/sources", new { kind = "github", repository = "acme/orders" });
        Assert.Equal((HttpStatusCode.Conflict, "source_exists"), (twice.StatusCode, await CodeAsync(twice)));
        var archive = await s.Owner.PostAsJsonAsync($"/api/projects/{s.ProjectId}/sources", new { kind = "archive", repository = "acme/orders" });
        Assert.Equal((HttpStatusCode.BadRequest, "source_kind_unavailable"), (archive.StatusCode, await CodeAsync(archive)));
        var unknown = await s.Owner.PostAsJsonAsync($"/api/projects/{s.ProjectId}/sources", new { kind = "svn", repository = "acme/orders" });
        Assert.Equal((HttpStatusCode.BadRequest, "validation_error"), (unknown.StatusCode, await CodeAsync(unknown)));
    }

    [Fact]
    public async Task A_name_match_with_an_element_of_another_Source_is_a_question_and_the_answer_binds_or_adds()
    {
        var s = await ImportScenario.CreateAsync(import);
        await s.ScanAndAcceptAllAsync();
        var a = Assert.Single(await SourcesAsync(s));
        var elementsBefore = await s.ElementsAsync();
        var byName = elementsBefore.ToDictionary(e => e.Text("name")!, e => e.Text("id")!);

        // Accepting an element fact bound the element to its key in Source A.
        var boundToA = await s.Owner.GetFromJsonAsync<JsonArray>($"/api/projects/{s.ProjectId}/sources/{a.Id()}/bindings");
        Assert.Contains(boundToA!, b => b!["elementName"]!.GetValue<string>() == "Orders.Api");

        // Source B holds the same services; its store runs MySQL where A says Postgres.
        var mirror = new SortedDictionary<string, byte[]>(FixtureGitHub.Load("orders"), StringComparer.Ordinal);
        mirror["docker-compose.yml"] = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(mirror["docker-compose.yml"]).Replace("postgres:16-alpine", "mysql:8"));
        s.GitHub.Commit(ImportScenario.Commit2, mirror);
        s.GitHub.Head = ImportScenario.Commit1;
        s.GitHub.Branches["mirror"] = ImportScenario.Commit2;
        var b = await CreateSourceAsync(s, "acme/orders-mirror", "Orders mirror");
        var scanB = await StartSourceScanAsync(s, b.Id(), new { @ref = "mirror" });
        await s.DrainAsync();
        Assert.Equal("completed", (await s.GetAsync(scanB)).Text("status"));

        // Every element of B whose name matches an element of A is a question. Nothing is
        // bound, added or changed for it, and the model is what it was.
        var proposals = await s.ProposalsAsync(scanB);
        var questions = proposals.Where(p => p.Text("action") == "bind").ToList();
        Assert.Equal(["Billing", "Orders.Api", "inventory-worker", "postgres"], questions.Values("name"));
        Assert.All(questions, q => Assert.Equal(byName[q.Text("name")!], q.Text("entityId")));
        Assert.All(questions, q => Assert.Equal(a.Text("id"), q["otherSources"]![0]!["sourceId"]!.GetValue<string>()));
        Assert.DoesNotContain(proposals, p => p.Text("entityKind") == "element" && p.Text("action") != "bind" && byName.ContainsKey(p.Text("name")!));
        Assert.Equal(elementsBefore.Values("id"), (await s.ElementsAsync()).Values("id"));
        var review = (await SourcesAsync(s)).Single("id", b.Text("id")!)["review"]!;
        Assert.Equal(4, review["bindQuestions"]!.GetValue<int>());

        // Yes for Orders.Api and postgres, no for Billing. inventory-worker waits.
        var answered = await s.Owner.PostAsJsonAsync(s.Url($"/{scanB}/proposals/decide"), new
        {
            bind = new object[]
            {
                new { id = questions.Single("name", "Orders.Api").Id(), same = true },
                new { id = questions.Single("name", "postgres").Id(), same = true },
                new { id = questions.Single("name", "Billing").Id(), same = false },
            },
        });
        var result = (await answered.Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.True(answered.StatusCode == HttpStatusCode.OK, result.ToJsonString());
        Assert.Empty(result["failed"]!.AsArray());
        Assert.True(result["replanned"]!.GetValue<bool>());

        var boundToB = (await s.Owner.GetFromJsonAsync<JsonArray>($"/api/projects/{s.ProjectId}/sources/{b.Id()}/bindings"))!;
        Assert.Equal(["Orders.Api", "postgres"], boundToB.Select(x => x!["elementName"]!.GetValue<string>()).Order(StringComparer.Ordinal));

        var after = (await s.ProposalsAsync(scanB)).Where(p => p.Text("status") == "pending").ToList();
        // A yes targets the bound element; its values agree, so it is a change that links.
        var api = after.Single(p => p.Text("entityKind") == "element" && p.Text("name") == "Orders.Api");
        Assert.Equal(("change", byName["Orders.Api"]), (api.Text("action"), api.Text("entityId")));
        // The Sources disagree on the store: a conflict that shows both sides and picks neither.
        var store = after.Single(p => p.Text("entityKind") == "element" && p.Text("name") == "postgres");
        Assert.Equal(("conflict", byName["postgres"]), (store.Text("action"), store.Text("entityId")));
        var technology = store["changes"]!.AsArray().Single(c => c!["field"]!.GetValue<string>() == "technology")!;
        Assert.Equal(("postgres", "mysql"), (technology["from"]!.GetValue<string>(), technology["to"]!.GetValue<string>()));
        var other = Assert.Single(store["otherSources"]!.AsArray())!;
        Assert.Equal((a.Text("id"), "postgres", "observed-in-source"),
            (other["sourceId"]!.GetValue<string>(), other["values"]!["technology"]!.GetValue<string>(), other["factStatus"]!.GetValue<string>()));
        Assert.StartsWith($"{s.GitHubHost}/acme/orders@{ImportScenario.Commit1}:", other["citation"]!.GetValue<string>());
        // A no makes the fact a new element; the question is not asked again.
        var billing = after.Single(p => p.Text("entityKind") == "element" && p.Text("name") == "Billing");
        Assert.Equal("add", billing.Text("action"));
        Assert.NotEqual(byName["Billing"], billing.Text("entityId"));
        Assert.Equal("bind", after.Single(p => p.Text("name") == "inventory-worker").Text("action"));

        // Accept all from B takes neither the conflict nor the open question.
        var bulk = await s.Owner.PostAsJsonAsync($"/api/projects/{s.ProjectId}/sources/{b.Id()}/accept", new { acknowledgeImpact = true });
        var bulkResult = (await bulk.Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.True(bulk.StatusCode == HttpStatusCode.OK, bulkResult.ToJsonString());
        Assert.Empty(bulkResult["failed"]!.AsArray());
        var accepted = bulkResult["accepted"]!.AsArray().Select(id => id!.GetValue<Guid>()).ToHashSet();
        Assert.Contains(api.Id(), accepted);
        Assert.Contains(billing.Id(), accepted);
        Assert.DoesNotContain(store.Id(), accepted);
        var pending = (await s.ProposalsAsync(scanB)).Where(p => p.Text("status") == "pending").ToList();
        Assert.Equal(["conflict postgres", "bind inventory-worker"], pending.Where(p => p.Text("entityKind") == "element")
            .Select(p => $"{p.Text("action")} {p.Text("name")}").OrderBy(x => x, StringComparer.Ordinal).Reverse());
        var elements = await s.ElementsAsync();
        Assert.Equal("postgres", elements.Single("id", byName["postgres"]).Text("technology"));
        Assert.Equal(2, elements.Count(e => e.Text("name") == "Billing"));
    }

    [Fact]
    public async Task Staleness_compares_Snapshots_of_one_Source_and_a_scan_of_another_Source_concludes_nothing_about_its_facts()
    {
        var s = await ImportScenario.CreateAsync(import);
        var first = await s.ScanAndAcceptAllAsync();
        var a = Assert.Single(await SourcesAsync(s));
        var model = await s.Owner.GetStringAsync(s.Model("elements"));

        // Source B holds none of A's files.
        s.GitHub.Commit(ImportScenario.Commit2, new Dictionary<string, byte[]> { ["README.md"] = "# Other\n"u8.ToArray() });
        s.GitHub.Head = ImportScenario.Commit1;
        s.GitHub.Branches["other"] = ImportScenario.Commit2;
        var b = await CreateSourceAsync(s, "acme/other", null);
        var scanB = await StartSourceScanAsync(s, b.Id(), new { @ref = "other" });
        await s.DrainAsync();
        var completed = await s.GetAsync(scanB);
        Assert.Equal(("completed", true), (completed.Text("status"), completed["isComplete"]!.GetValue<bool>()));

        // B's Snapshot says nothing about A's facts: no removal, no stale mark.
        Assert.DoesNotContain(await s.ProposalsAsync(scanB), p => p.Text("action") == "remove");
        Assert.Equal(model, await s.Owner.GetStringAsync(s.Model("elements")));
        Assert.DoesNotContain("\"stale\":{", model);

        // Snapshots of two Sources are not compared.
        var across = await s.Owner.GetAsync($"/api/projects/{s.ProjectId}/sources/{a.Id()}/snapshots/compare?base={first.Id()}&head={scanB}");
        Assert.Equal(HttpStatusCode.NotFound, across.StatusCode);

        // A's next Snapshot without billing: the comparison of A's two Snapshots is what marks it.
        var withoutBilling = FixtureGitHub.Load("orders");
        foreach (var path in withoutBilling.Keys.Where(p => p.StartsWith("billing/", StringComparison.Ordinal)).ToList())
            withoutBilling.Remove(path);
        s.GitHub.Commit(ImportScenario.Commit3, withoutBilling);
        var next = await s.ScanAsync();
        var diff = (await s.Owner.GetFromJsonAsync<JsonObject>($"/api/projects/{s.ProjectId}/sources/{a.Id()}/snapshots/compare?base={first.Id()}&head={next.Id()}"))!;
        Assert.False(diff["sameSnapshot"]!.GetValue<bool>());
        Assert.Contains(diff["files"]!["removed"]!.AsArray(), f => f!.GetValue<string>() == "billing/Billing.csproj");
        Assert.Contains(diff["facts"]!["removed"]!.AsArray(), f => f!["name"]!.GetValue<string>() == "Billing");
        Assert.NotNull((await s.ElementsAsync()).Single("name", "Billing")["stale"] as JsonObject);
    }

    private static async Task<List<JsonObject>> SourcesAsync(ImportScenario s) =>
        [.. (await s.Owner.GetFromJsonAsync<JsonArray>($"/api/projects/{s.ProjectId}/sources"))!.Select(x => x!.AsObject())];

    private static async Task<JsonObject> CreateSourceAsync(ImportScenario s, string repository, string? name)
    {
        var response = await s.Owner.PostAsJsonAsync($"/api/projects/{s.ProjectId}/sources", new { kind = "github", repository, name });
        var body = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.True(response.StatusCode == HttpStatusCode.Created, body.ToJsonString());
        Assert.Equal(name ?? repository, body.Text("name"));
        return body;
    }

    private static async Task<Guid> StartSourceScanAsync(ImportScenario s, Guid sourceId, object body)
    {
        s.Import.Time.Advance(TimeSpan.FromSeconds(1));
        var response = await s.Owner.PostAsJsonAsync($"/api/projects/{s.ProjectId}/sources/{sourceId}/scans", body);
        Assert.True(response.StatusCode == HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<JsonObject>())!.Id();
    }

    private async Task<List<string>> ReadPathsAsync(ImportScenario s, Guid scanId)
    {
        using var scope = import.Host.Factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().ScanFiles
            .Where(f => f.ScanId == scanId && f.State == ScanFileState.Read).Select(f => f.Path).ToListAsync();
    }

    private static async Task<string?> CodeAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonObject>())?["type"]?.GetValue<string>();
}
