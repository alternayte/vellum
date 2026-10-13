using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Vellum.Modules.Import.Extraction;
using Vellum.Modules.Import.Proposals;
using Vellum.Modules.Import.Scans;
using Vellum.Tests.Shared.Outbound;

namespace Vellum.Tests.Modules.Import;

/// <summary>A clock that a test moves. Timers still run in real time; only "now" is set.</summary>
internal sealed class ManualTime : TimeProvider
{
    private DateTimeOffset _now = DateTimeOffset.UtcNow;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}

/// <summary>
/// A GitHub that serves fixture directories as commits, in the shapes of the REST API: the
/// repository, a ref as a commit SHA, the recursive tree with real git blob SHAs, and each
/// blob as base64 in lines of 60 characters. It records every request, so a test can say
/// what was never asked for.
/// </summary>
internal sealed class FixtureGitHub
{
    private readonly Dictionary<string, SortedDictionary<string, byte[]>> _commits = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> _requests = new();
    private readonly ConcurrentQueue<string?> _authorization = new();

    /// <summary>The commit that every branch name resolves to.</summary>
    public string Head { get; set; } = "";
    /// <summary>A private repository: without a token GitHub says it does not exist.</summary>
    public bool Private { get; set; }
    /// <summary>GitHub refuses the token.</summary>
    public bool Unauthorized { get; set; }
    public bool Truncated { get; set; }
    /// <summary>
    /// Called with the path before a blob is served. A response it returns is served instead.
    /// The token is the request's: a gate that waits on it ends when the caller gives up.
    /// </summary>
    public Func<string, CancellationToken, Task<HttpResponseMessage?>>? OnBlob { get; set; }

    /// <summary>What was asked for, in order: <c>repository</c>, <c>ref:main</c>, <c>tree:&lt;sha&gt;</c>, <c>blob:&lt;path&gt;</c>.</summary>
    public IReadOnlyList<string> Requests => [.. _requests];
    public IReadOnlyList<string> BlobRequests => [.. _requests.Where(r => r.StartsWith("blob:", StringComparison.Ordinal)).Select(r => r[5..])];
    public IReadOnlyList<string?> AuthorizationHeaders => [.. _authorization];

    public static SortedDictionary<string, byte[]> Load(string fixture)
    {
        var root = Repo.Root(fixture);
        var files = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFiles(root, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 }))
            files[Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/')] = File.ReadAllBytes(path);
        return files;
    }

    /// <summary>Adds a commit and makes it the head.</summary>
    public FixtureGitHub Commit(string sha, IDictionary<string, byte[]> files)
    {
        _commits[sha] = new SortedDictionary<string, byte[]>(files, StringComparer.Ordinal);
        Head = sha;
        return this;
    }

    public IReadOnlyDictionary<string, byte[]> Files(string sha) => _commits[sha];

    public static string BlobSha(byte[] content) =>
        Convert.ToHexStringLower(SHA1.HashData([.. Encoding.ASCII.GetBytes($"blob {content.Length}\0"), .. content]));

    public async Task<HttpResponseMessage> Handle(SentRequest sent)
    {
        _authorization.Enqueue(sent.Header("Authorization"));
        var segments = sent.Uri.AbsolutePath.Trim('/').Split('/');

        if (Unauthorized)
            return Json(HttpStatusCode.Unauthorized, """{"message":"Bad credentials","status":"401"}""");
        if (segments.Length < 3 || segments[0] != "repos")
            return NotFound();
        if (Private && sent.Header("Authorization") is null)
        {
            _requests.Enqueue("refused");
            return NotFound();
        }

        var (owner, name) = (segments[1], segments[2]);
        if (segments.Length == 3)
        {
            _requests.Enqueue("repository");
            return Json(HttpStatusCode.OK, new JsonObject
            {
                ["id"] = 1, ["full_name"] = $"{owner}/{name}", ["private"] = Private,
                ["default_branch"] = "main", ["archived"] = false, ["size"] = 10,
            }.ToJsonString());
        }

        if (segments is [_, _, _, "commits"])
        {
            // "List commits" with ?sha=<ref>&per_page=1: the newest commit of the ref, or 404 for no such ref.
            var reference = System.Web.HttpUtility.ParseQueryString(sent.Uri.Query)["sha"] ?? "";
            _requests.Enqueue($"ref:{reference}");
            var sha = _commits.ContainsKey(reference) ? reference : reference == "main" ? Head : null;
            return sha is null
                ? NotFound()
                : Json(HttpStatusCode.OK, new JsonArray(new JsonObject { ["sha"] = sha }).ToJsonString());
        }

        if (segments is [_, _, _, "git", "trees", var treeSha])
        {
            _requests.Enqueue($"tree:{treeSha}");
            if (!_commits.TryGetValue(treeSha, out var files))
                return NotFound();

            var tree = new JsonArray();
            var directories = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var path in files.Keys)
            {
                for (var slash = path.IndexOf('/'); slash >= 0; slash = path.IndexOf('/', slash + 1))
                    directories.Add(path[..slash]);
            }
            foreach (var directory in directories)
                tree.Add(new JsonObject { ["path"] = directory, ["mode"] = "040000", ["type"] = "tree", ["sha"] = BlobSha(Encoding.UTF8.GetBytes(directory)) });
            foreach (var (path, content) in files)
                tree.Add(new JsonObject { ["path"] = path, ["mode"] = "100644", ["type"] = "blob", ["sha"] = BlobSha(content), ["size"] = content.Length });
            return Json(HttpStatusCode.OK, new JsonObject { ["sha"] = treeSha, ["truncated"] = Truncated, ["tree"] = tree }.ToJsonString());
        }

        if (segments is [_, _, _, "git", "blobs", var blobSha])
        {
            var found = _commits.Values.SelectMany(files => files).FirstOrDefault(file => BlobSha(file.Value) == blobSha);
            _requests.Enqueue($"blob:{found.Key ?? blobSha}");
            if (found.Key is null)
                return NotFound();
            if (OnBlob is not null && await OnBlob(found.Key, sent.Cancellation) is { } instead)
                return instead;

            var base64 = Convert.ToBase64String(found.Value);
            var wrapped = new StringBuilder();
            for (var i = 0; i < base64.Length; i += 60)
                wrapped.Append(base64, i, Math.Min(60, base64.Length - i)).Append('\n');
            return Json(HttpStatusCode.OK, new JsonObject
            {
                ["sha"] = blobSha, ["size"] = found.Value.Length, ["encoding"] = "base64", ["content"] = wrapped.ToString(),
            }.ToJsonString());
        }

        return NotFound();
    }

    /// <summary>GitHub's answer when the primary rate limit is used up.</summary>
    public static HttpResponseMessage RateLimited(DateTimeOffset resetAt)
    {
        var response = Json(HttpStatusCode.Forbidden, """{"message":"API rate limit exceeded.","status":"403"}""");
        response.Headers.TryAddWithoutValidation("x-ratelimit-remaining", "0");
        response.Headers.TryAddWithoutValidation("x-ratelimit-reset", resetAt.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture));
        return response;
    }

    private static HttpResponseMessage NotFound() => Json(HttpStatusCode.NotFound, """{"message":"Not Found","status":"404"}""");

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
}

/// <summary>
/// One app for the import tests. No worker loop runs in it: a test runs the worker itself, so
/// each step of a scan happens when the test says. The clock is the test's.
/// </summary>
public sealed class ImportHost : IDisposable
{
    internal ManualTime Time { get; } = new();
    public OutboundHost Host { get; }
    public IntegrationFixture Fixture { get; }

    public ImportHost(IntegrationFixture fixture) : this(fixture, runWorker: false)
    {
    }

    /// <summary>An app whose own worker loop runs, as it does outside tests.</summary>
    internal static ImportHost WithWorker(IntegrationFixture fixture) => new(fixture, runWorker: true);

    private ImportHost(IntegrationFixture fixture, bool runWorker)
    {
        Fixture = fixture;
        Host = new OutboundHost(fixture, configure: services =>
        {
            services.AddSingleton<TimeProvider>(Time);
            services.AddSingleton(ScanWorkerOptions.Default with
            {
                Enabled = runWorker, PollInterval = TimeSpan.FromMilliseconds(100),
                WatchInterval = TimeSpan.FromMilliseconds(50), LeaseDuration = TimeSpan.FromSeconds(60),
            });
        });
    }

    public void Dispose() => Host.Dispose();
}

/// <summary>One user, one project and one stub GitHub of their own.</summary>
internal sealed record ImportScenario(
    ImportHost Import, HttpClient Owner, Guid WorkspaceId, Guid ProjectId, string GitHubHost, FixtureGitHub GitHub)
{
    public const string Commit1 = "1111111111111111111111111111111111111111";
    public const string Commit2 = "2222222222222222222222222222222222222222";
    public const string Commit3 = "3333333333333333333333333333333333333333";

    public static async Task<ImportScenario> CreateAsync(ImportHost import, string? fixture = "orders")
    {
        var host = import.Host;
        var owner = await TestHost.SignInAsync(host.Factory);
        var (workspaceId, projectId) = await TestHost.CreateProjectAsync(owner);
        var gitHubHost = $"{Guid.NewGuid():N}.gh.test";
        await OutboundHost.PutSettingsAsync(owner, workspaceId, new JsonObject { ["github"] = new JsonObject { ["apiBaseUrl"] = $"https://{gitHubHost}" } });

        var gitHub = new FixtureGitHub();
        if (fixture is not null)
            gitHub.Commit(Commit1, FixtureGitHub.Load(fixture));
        host.Transport.For(gitHubHost).Then(gitHub.Handle);
        return new ImportScenario(import, owner, workspaceId, projectId, gitHubHost, gitHub);
    }

    public string Url(string path = "") => $"/api/projects/{ProjectId}/imports{path}";

    public string Model(string path) => $"/api/projects/{ProjectId}/{path}";

    public ScanWorker Worker => Import.Host.Factory.Services.GetRequiredService<ScanWorker>();

    /// <summary>Asks for a scan and returns its ID.</summary>
    public async Task<Guid> StartAsync(string repository = "acme/orders", object? body = null)
    {
        // The clock stands still unless a test moves it. Scans are ordered by when they were asked for.
        Import.Time.Advance(TimeSpan.FromSeconds(1));
        var response = await Owner.PostAsJsonAsync(Url(), body ?? new { repository });
        Assert.True(response.StatusCode == HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<JsonObject>())!["id"]!.GetValue<Guid>();
    }

    /// <summary>Runs the worker until it finds no scan to take.</summary>
    public async Task DrainAsync(string workerId = "worker-a")
    {
        while (await Worker.RunOnceAsync(workerId, CancellationToken.None))
        {
        }
    }

    /// <summary>Asks for a scan, runs it and returns it as the API shows it.</summary>
    public async Task<JsonObject> ScanAsync(string repository = "acme/orders")
    {
        var id = await StartAsync(repository);
        await DrainAsync();
        return await GetAsync(id);
    }

    public async Task<JsonObject> GetAsync(Guid importId) => (await Owner.GetFromJsonAsync<JsonObject>(Url($"/{importId}")))!;

    public Task<List<JsonObject>> ProposalsAsync(Guid importId, string query = "") => AllAsync(Url($"/{importId}/proposals"), query);

    public Task<List<JsonObject>> EvidenceAsync(Guid importId, string query = "") => AllAsync(Url($"/{importId}/evidence"), query);

    /// <summary>Every item of a paged list, read through its cursors.</summary>
    public async Task<List<JsonObject>> AllAsync(string url, string query = "")
    {
        var items = new List<JsonObject>();
        string? cursor = null;
        do
        {
            var separator = url.Contains('?') ? '&' : '?';
            var page = (await Owner.GetFromJsonAsync<JsonObject>(
                $"{url}{separator}limit=7{(query.Length > 0 ? "&" + query : "")}{(cursor is null ? "" : "&cursor=" + Uri.EscapeDataString(cursor))}"))!;
            items.AddRange(page["items"]!.AsArray().Select(item => item!.AsObject()));
            cursor = page["cursor"]?.GetValue<string>();
        }
        while (cursor is not null);
        return items;
    }

    public async Task<JsonObject> DecideAsync(Guid importId, IEnumerable<Guid>? accept = null, IEnumerable<Guid>? reject = null, HttpClient? client = null)
    {
        var response = await (client ?? Owner).PostAsJsonAsync(Url($"/{importId}/proposals/decide"), new { accept = accept ?? [], reject = reject ?? [] });
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<JsonObject>())!;
    }

    /// <summary>Scans, then accepts every proposal in one decision. Returns the scan.</summary>
    public async Task<JsonObject> ScanAndAcceptAllAsync()
    {
        var scan = await ScanAsync();
        Assert.Equal("completed", scan.Text("status"));
        var proposals = await ProposalsAsync(scan.Id());
        var decision = await DecideAsync(scan.Id(), accept: proposals.Select(p => p.Id()));
        Assert.Empty(decision["failed"]!.AsArray());
        return scan;
    }

    public Task<List<JsonObject>> ElementsAsync() => AllAsync(Model("elements"));
}

/// <summary>
/// The facts of a fixture commit, extracted at run time with the app's own extractors and
/// mapping. Tests take fact keys from here and never spell one: a key is an opaque string.
/// </summary>
internal sealed class FixtureFacts
{
    public ExtractionResult Result { get; }
    public string[] MappedKeys { get; }
    public int EvidenceOnly { get; }
    public int Folded { get; }

    public FixtureFacts(ImportScenario s, string commit)
    {
        var repository = $"{s.GitHubHost}/acme/orders";
        Result = EvidenceExtractor.Extract(new ExtractionInput(repository, commit,
            [.. s.GitHub.Files(commit).Select(f => new SourceFile(f.Key, System.Text.Encoding.UTF8.GetString(f.Value)))]));
        var mapping = FactMapping.Map(repository, Result.Facts);
        MappedKeys = [.. mapping.Candidates.Select(c => c.FactKey).Order(StringComparer.Ordinal)];
        EvidenceOnly = mapping.Facts.Count(f => f.Disposition == FactDisposition.EvidenceOnly);
        Folded = mapping.Facts.Count(f => f.Disposition == FactDisposition.Folded);
    }

    public int Count(FactKind kind) => Result.Facts.Count(f => f.Kind == kind);
}

internal static class ImportJson
{
    /// <summary>The one proposal for an entity of this kind with this name.</summary>
    public static JsonObject Proposal(this IEnumerable<JsonObject> proposals, string entityKind, string name)
    {
        var found = proposals.Where(p => p.Text("entityKind") == entityKind && p.Text("name") == name).ToList();
        Assert.True(found.Count == 1, $"Expected one {entityKind} proposal named '{name}', found {found.Count}.");
        return found[0];
    }

    /// <summary>The proposal of this kind whose proposed or current value of <paramref name="field"/> is <paramref name="value"/>.</summary>
    public static JsonObject ProposalWith(this IEnumerable<JsonObject> proposals, string entityKind, string field, string value)
    {
        var found = proposals.Where(p => p.Text("entityKind") == entityKind
            && ((p["proposed"] ?? p["current"])?[field]?.GetValue<string>() == value)).ToList();
        Assert.True(found.Count == 1, $"Expected one {entityKind} proposal with {field} = '{value}', found {found.Count}.");
        return found[0];
    }

    /// <summary>Kind and name of each proposal, sorted: what a test compares in place of fact keys.</summary>
    public static string[] Subjects(this IEnumerable<JsonObject> proposals) =>
        [.. proposals.Select(p => $"{p.Text("entityKind")} {p.Text("name")}").Order(StringComparer.Ordinal)];

    public static Guid Id(this JsonObject json, string field = "id") => json[field]!.GetValue<Guid>();

    public static string? Text(this JsonObject json, string field) => json[field]?.GetValue<string>();

    public static JsonObject Single(this IEnumerable<JsonObject> items, string field, string value)
    {
        var found = items.Where(i => i[field]?.GetValue<string>() == value).ToList();
        Assert.True(found.Count == 1, $"Expected one item with {field} = '{value}', found {found.Count}.");
        return found[0];
    }

    public static string[] Values(this IEnumerable<JsonObject> items, string field) =>
        [.. items.Select(i => i[field]!.GetValue<string>()).OrderBy(v => v, StringComparer.Ordinal)];
}
