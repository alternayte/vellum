extern alias cli;

using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vellum.Modules.Import;
using Vellum.Shared;
using CliProgram = cli::Vellum.Cli.Program;
using CliScan = cli::Vellum.Cli.LocalScan;
using CliLimits = cli::Vellum.Modules.Import.Scans.ScanLimits;
using PushRequest = cli::Vellum.Modules.Import.Sources.AgentPushRequest;
using PushRule = cli::Vellum.Modules.Import.Sources.AgentPushRule;
using PushFact = cli::Vellum.Modules.Import.Sources.AgentPushFact;
using PushFile = cli::Vellum.Modules.Import.Sources.AgentPushFile;
using PushCitation = cli::Vellum.Modules.Import.Sources.AgentPushCitation;

namespace Vellum.Tests.Modules.Import;

/// <summary>
/// <c>vellum scan</c> runs on a machine that holds the code and pushes what the server would
/// have extracted, without the code: the push body holds no file content, the Snapshot and the
/// facts equal a server scan of the same files, and the server takes the push as untrusted input.
/// </summary>
public class AgentPushTests(ImportHost import) : IClassFixture<ImportHost>
{
    private static readonly string Marker = $"marker-{Guid.NewGuid():N}";

    /// <summary>The orders fixture, with text no push may carry: a comment, a README, a secret file by name, a Secret manifest and a file dense with credentials.</summary>
    private static SortedDictionary<string, byte[]> Files()
    {
        var files = new SortedDictionary<string, byte[]>(FixtureGitHub.Load("orders"), StringComparer.Ordinal);
        static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);
        files["orders-api/Program.cs"] = [.. files["orders-api/Program.cs"], .. Utf8($"\n// {Marker}-comment\n")];
        files["README.md"] = Utf8($"# Orders\n\n{Marker}-readme\n");
        files["orders-api/.env"] = Utf8($"DB_PASSWORD={Marker}-env\n");
        files["deploy/k8s/db-credentials.yaml"] = Utf8($"apiVersion: v1\nkind: Secret\nmetadata:\n  name: db\nstringData:\n  password: {Marker}-k8s\n");
        files["orders-api/appsettings.Production.json"] = Utf8("{\n" + string.Join(",\n", Enumerable.Range(0, 6).Select(i => $"  \"password{i}\": \"{Marker}-dense-{i}\"")) + "\n}\n");
        return files;
    }

    private static string Write(IReadOnlyDictionary<string, byte[]> files)
    {
        var root = Path.Combine(Path.GetTempPath(), $"vellum-push-{Guid.NewGuid():N}");
        foreach (var (path, content) in files)
        {
            var full = Path.Combine(root, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllBytes(full, content);
        }
        return root;
    }

    /// <summary>Sends to the test server and keeps every request body as sent.</summary>
    private sealed class Recorder(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        public List<(HttpMethod Method, string Path, string Body)> Sent { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            lock (Sent)
                Sent.Add((request.Method, request.RequestUri!.AbsolutePath, body));
            return await base.SendAsync(request, cancellationToken);
        }
    }

    private sealed record CliRun(int ExitCode, string Output, string Error, Recorder Recorder);

    private async Task<CliRun> RunCliAsync(ImportScenario s, string token, params string[] args)
    {
        var recorder = new Recorder(import.Host.Factory.Server.CreateHandler());
        var (output, error) = (new StringWriter(), new StringWriter());
        string? Environment(string name) => name == CliProgram.TokenVariable ? token : null;
        var exit = await CliProgram.RunAsync(
            ["scan", .. args, "--url", "http://localhost", "--project", s.ProjectId.ToString()],
            output, error, Environment, () => recorder, CancellationToken.None);
        return new CliRun(exit, output.ToString(), error.ToString(), recorder);
    }

    private async Task<T> DbAsync<T>(Func<AppDbContext, Task<T>> query)
    {
        using var scope = import.Host.Factory.Services.CreateScope();
        return await query(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    private Task<List<string>> FactsAsync(Guid scanId) => DbAsync(async db =>
        (await db.SnapshotFacts.AsNoTracking().Where(f => f.ScanId == scanId).ToListAsync())
            .Select(f => $"{f.Kind}|{f.FactKey}|{f.Name}|{f.Attributes}|{f.Evidence}").Order(StringComparer.Ordinal).ToList());

    [Fact]
    public async Task A_push_holds_no_file_content_and_gives_the_Snapshot_and_facts_of_a_server_scan_of_the_same_files()
    {
        var s = await ImportScenario.CreateAsync(import, fixture: null);
        var files = Files();
        s.GitHub.Commit(ImportScenario.Commit1, files);
        var server = await s.ScanAsync();
        Assert.Equal("completed", server.Text("status"));

        var root = Write(files);
        // .git/HEAD is read as text for the commit; .git itself is never scanned.
        const string head = "abcdefabcdefabcdefabcdefabcdefabcdefabcd";
        Directory.CreateDirectory(Path.Combine(root, ".git", "refs", "heads"));
        File.WriteAllText(Path.Combine(root, ".git", "HEAD"), "ref: refs/heads/main\n");
        File.WriteAllText(Path.Combine(root, ".git", "refs", "heads", "main"), head + "\n");
        try
        {
            var blobsBefore = await DbAsync(db => db.Blobs.CountAsync(b => b.WorkspaceId == s.WorkspaceId));
            var agent = await TestHost.CreateAgentAsync(import.Host.Factory, s.Owner, s.WorkspaceId, ["push"]);
            var run = await RunCliAsync(s, agent.Token, root, "--name", "Orders pushed");
            Assert.True(run.ExitCode == 0, run.Error + run.Output);

            // What left the machine: no file whole, and no text that only a file holds.
            var push = Assert.Single(run.Recorder.Sent, r => r.Method == HttpMethod.Post);
            Assert.DoesNotContain(Marker, push.Body, StringComparison.Ordinal);
            foreach (var (path, content) in files.Where(f => f.Value.Length >= 64))
            {
                var text = Encoding.UTF8.GetString(content);
                var asJson = JsonSerializer.Serialize(text, JsonSerializerOptions.Web)[1..^1];
                Assert.False(push.Body.Contains(text, StringComparison.Ordinal) || push.Body.Contains(asJson, StringComparison.Ordinal), path);
            }
            var body = JsonNode.Parse(push.Body)!.AsObject();
            Assert.Equal(["commitSha", "extractionGap", "facts", "files", "filesListed", "isComplete", "name", "rules", "scope", "skipped"],
                body.Select(p => p.Key).Order(StringComparer.Ordinal));
            Assert.All(body["files"]!.AsArray(), f => Assert.Equal(["blobSha", "path", "size"], f!.AsObject().Select(p => p.Key).Order(StringComparer.Ordinal)));
            // Files whose content is a secret are named with the reason and nothing else; one refused by its name is not named at all.
            Assert.Equal(
                [("deploy/k8s/db-credentials.yaml", "secret-file"), ("orders-api/appsettings.Production.json", "credential-dense")],
                body["skipped"]!.AsArray().Select(k => (k!["path"]!.GetValue<string>(), k["reason"]!.GetValue<string>())).Order());

            // The same files give the same Snapshot and the same facts as the server's scan of them.
            var pushed = run.Output[(run.Output.IndexOf("Snapshot ", StringComparison.Ordinal) + "Snapshot ".Length)..].Split(',')[0];
            var scans = await s.AllAsync(s.Url());
            var pushScan = Assert.Single(scans, scan => scan.Id() != server.Id());
            var detail = await s.GetAsync(pushScan.Id());
            Assert.Equal(server.Text("snapshotId"), detail.Text("snapshotId"));
            Assert.Equal(server.Text("snapshotId"), pushed);
            Assert.Equal(await FactsAsync(server.Id()), await FactsAsync(pushScan.Id()));
            Assert.NotEmpty(await FactsAsync(pushScan.Id()));

            // Provenance: the token, the commit as reported, and no content held.
            Assert.Equal((false, head, $"agent-token:{agent.TokenId}"),
                (detail["contentHeld"]!.GetValue<bool>(), detail.Text("reportedCommitSha"), detail.Text("requestedBy")));
            var source = (await s.Owner.GetFromJsonAsync<JsonArray>($"/api/projects/{s.ProjectId}/sources"))!
                .Select(n => n!.AsObject()).Single(n => n.Text("kind") == "agent-push");
            Assert.Equal(("Orders pushed", "agent-push/orders-pushed"), (source.Text("name"), source.Text("citation")));
            var snapshot = source["latestSnapshot"]!.AsObject();
            Assert.Equal((false, head, (string?)null, server.Text("snapshotId")),
                (snapshot["contentHeld"]!.GetValue<bool>(), snapshot.Text("reportedCommitSha"), snapshot.Text("commitSha"), snapshot.Text("revision")));

            // Citations name the Snapshot; there is no page to link to and no line to show.
            var proposals = await s.ProposalsAsync(pushScan.Id());
            var evidence = proposals.SelectMany(p => p["evidence"]!.AsArray()).Select(e => e!.AsObject()).First();
            Assert.StartsWith($"agent-push/orders-pushed@{server.Text("snapshotId")}:", evidence.Text("pointer"));
            Assert.Null(evidence["url"]?.GetValue<string>());
            var window = await s.Owner.GetAsync(s.Url($"/{pushScan.Id()}/source?path={Uri.EscapeDataString(evidence.Text("path")!)}&startLine={evidence["startLine"]}&endLine={evidence["endLine"]}"));
            Assert.Equal(HttpStatusCode.Conflict, window.StatusCode);
            Assert.Contains("content_not_held", await window.Content.ReadAsStringAsync());

            // The server stored no content for the push.
            Assert.Equal(blobsBefore, await DbAsync(db => db.Blobs.CountAsync(b => b.WorkspaceId == s.WorkspaceId)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task A_push_needs_the_push_scope_this_servers_rules_and_stays_inside_the_limits()
    {
        var s = await ImportScenario.CreateAsync(import, fixture: null);
        var root = Write(FixtureGitHub.Load("orders"));
        try
        {
            // A token without the push scope is refused before anything is read or sent.
            var reader = await TestHost.CreateAgentAsync(import.Host.Factory, s.Owner, s.WorkspaceId, ["read", "propose", "export", "scan-pull-request"]);
            var refused = await RunCliAsync(s, reader.Token, root);
            Assert.Equal(3, refused.ExitCode);
            Assert.DoesNotContain(refused.Recorder.Sent, r => r.Method == HttpMethod.Post);
            Assert.Equal(HttpStatusCode.Forbidden, (await reader.Client.PostAsJsonAsync($"/api/projects/{s.ProjectId}/sources/push", new { })).StatusCode);

            var agent = await TestHost.CreateAgentAsync(import.Host.Factory, s.Owner, s.WorkspaceId, ["push"]);
            var url = $"/api/projects/{s.ProjectId}/sources/push";
            var settings = (await agent.Client.GetFromJsonAsync<JsonObject>(url))!;
            var valid = CliScan.Run(root, "orders", new CliLimits(
                settings["maxFiles"]!.GetValue<int>(), settings["maxFileBytes"]!.GetValue<long>(), settings["maxTotalBytes"]!.GetValue<long>(),
                [.. settings["excludeGlobs"]!.AsArray().Select(g => g!.GetValue<string>())]), [], []);
            async Task<(HttpStatusCode Status, string Body)> PushAsync(PushRequest request)
            {
                using var response = await agent.Client.PostAsync(url, JsonContent.Create(request, options: JsonSerializerOptions.Web));
                return (response.StatusCode, await response.Content.ReadAsStringAsync());
            }

            // Rules of another version: refused, whatever else the push holds.
            var other = valid with { Rules = [.. valid.Rules.Select((r, i) => i == 0 ? r with { Version = r.Version + 1 } : r)] };
            var mismatch = await PushAsync(other);
            Assert.Equal(HttpStatusCode.Conflict, mismatch.Status);
            Assert.Contains("extractor_version_mismatch", mismatch.Body);
            var citedUnknown = valid with { Facts = [.. valid.Facts.Select((f, i) => i == 0 ? f with { Evidence = [f.Evidence[0] with { RuleVersion = 99 }] } : f)] };
            Assert.Equal(HttpStatusCode.BadRequest, (await PushAsync(citedUnknown)).Status);

            // The limits and the scope rules of the workspace hold for a push.
            PushRequest[] outside =
            [
                valid with { Scope = valid.Scope with { MaxFiles = 1_000_000 } },
                valid with { Files = [.. valid.Files, new PushFile("orders-api/.env", 10, new string('a', 40))] },
                valid with { Files = [.. valid.Files, new PushFile("docs/notes.txt", 10, new string('a', 40))] },
                valid with { Files = [.. valid.Files, valid.Files[0]] },
                valid with { Files = [.. valid.Files.Select((f, i) => i == 0 ? f with { BlobSha = "not-a-sha" } : f)] },
                valid with { Files = [.. valid.Files.Select((f, i) => i == 0 ? f with { Size = 10L * 1024 * 1024 } : f)] },
                valid with { Facts = [.. valid.Facts.Select((f, i) => i == 0 ? f with { Evidence = [new PushCitation("elsewhere/x.cs", 1, 2, f.Evidence[0].Rule, f.Evidence[0].RuleVersion)] } : f)] },
                valid with { Facts = [.. valid.Facts, valid.Facts[0]] },
                valid with { Facts = [.. valid.Facts.Select((f, i) => i == 0 ? f with { Kind = "Secret" } : f)] },
                valid with { Facts = [.. Enumerable.Range(0, 20_001).Select(i => valid.Facts[0] with { Key = $"k{i}" })] },
                valid with { Skipped = [new("node_modules/x/index.json", "secret-file")] },
                valid with { CommitSha = "main" },
                valid with { Name = "  " },
            ];
            foreach (var request in outside)
            {
                var (status, body) = await PushAsync(request);
                Assert.True(status == HttpStatusCode.BadRequest, $"{status}: {body}");
            }
            Assert.Equal(0, await DbAsync(db => db.Scans.CountAsync(c => c.ProjectId == s.ProjectId)));

            // The valid push is taken.
            var taken = await PushAsync(valid);
            Assert.True(taken.Status == HttpStatusCode.Created, taken.Body);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task A_push_never_changes_an_accepted_fact_it_proposes_and_a_person_decides()
    {
        var s = await ImportScenario.CreateAsync(import, fixture: null);
        var files = new SortedDictionary<string, byte[]>(FixtureGitHub.Load("orders"), StringComparer.Ordinal);
        var root = Write(files);
        try
        {
            var agent = await TestHost.CreateAgentAsync(import.Host.Factory, s.Owner, s.WorkspaceId, ["push"]);
            Assert.Equal(0, (await RunCliAsync(s, agent.Token, root)).ExitCode);
            var first = Assert.Single(await s.AllAsync(s.Url()));
            Assert.True(first["canDecide"]!.GetValue<bool>());

            // A token cannot accept: that is a person's decision.
            var proposals = await s.ProposalsAsync(first.Id());
            var byToken = await agent.Client.PostAsJsonAsync(s.Url($"/{first.Id()}/proposals/decide"), new { accept = proposals.Select(p => p.Id()), reject = Array.Empty<Guid>() });
            Assert.Equal(HttpStatusCode.Forbidden, byToken.StatusCode);
            Assert.Empty(await s.ElementsAsync());

            var decision = await s.DecideAsync(first.Id(), accept: proposals.Select(p => p.Id()));
            Assert.Empty(decision["failed"]!.AsArray());
            var accepted = (await s.ElementsAsync()).Select(e => e.ToJsonString()).ToList();
            Assert.Contains(accepted, e => e.Contains("postgres", StringComparison.OrdinalIgnoreCase));

            // The next push says the store is MySQL. The model keeps what a person accepted.
            File.WriteAllText(Path.Combine(root, "docker-compose.yml"),
                File.ReadAllText(Path.Combine(root, "docker-compose.yml")).Replace("postgres:16-alpine", "mysql:8", StringComparison.Ordinal));
            var run = await RunCliAsync(s, agent.Token, root);
            Assert.True(run.ExitCode == 0, run.Error);
            var second = (await s.AllAsync(s.Url())).Single(scan => scan.Id() != first.Id());
            Assert.Equal(accepted, (await s.ElementsAsync()).Select(e => e.ToJsonString()).ToList());
            var pending = await s.ProposalsAsync(second.Id());
            Assert.Contains(pending, p => p.Text("status") == "pending" && p["changes"]!.AsArray().Any(c => c!["to"]?.ToJsonString().Contains("mysql", StringComparison.Ordinal) == true));
            Assert.DoesNotContain(pending, p => p.Text("status") == "accepted");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
