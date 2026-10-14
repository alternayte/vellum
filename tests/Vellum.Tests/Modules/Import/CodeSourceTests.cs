using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vellum.Modules.Import.Sources;
using Vellum.Shared;
using Vellum.Tests.Shared.Outbound;

namespace Vellum.Tests.Modules.Import;

/// <summary>
/// Code Sources other than a git host: a folder under the sources root of a local instance,
/// and an uploaded archive. Both are refused when a path, a link or an entry would reach
/// outside what was given, and both give the Snapshot a git host gives for the same tree.
/// </summary>
public sealed class CodeSourceTests : IClassFixture<ImportHost>, IDisposable
{
    private readonly string _root;
    private readonly string _outside;
    private readonly ImportHost _local;

    public CodeSourceTests(ImportHost import)
    {
        var scratch = Path.Combine(Path.GetTempPath(), $"vellum-sources-{Guid.NewGuid():N}");
        _root = Path.Combine(scratch, "root");
        _outside = Path.Combine(scratch, "outside");
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_outside);
        File.WriteAllText(Path.Combine(_outside, "secret.yml"), "password: outside-the-root-0123456789\n");
        _local = ImportHost.With(import.Fixture, new Dictionary<string, string?> { [SourcesRoot.ConfigurationKey] = _root });
    }

    public void Dispose()
    {
        _local.Dispose();
        Directory.Delete(Path.GetDirectoryName(_root)!, recursive: true);
    }

    [Fact]
    public async Task One_tree_as_a_GitHub_commit_a_zip_a_tar_gz_and_a_folder_is_one_Snapshot()
    {
        var s = await ImportScenario.CreateAsync(_local);
        var files = FixtureGitHub.Load("orders");

        var github = await s.ScanAsync();
        Assert.Equal("completed", github.Text("status"));
        var expected = github.Text("snapshotId");
        var expectedFiles = await ManifestAsync(s, github.Id());

        // An archive as GitHub serves it: every file under one top folder, which is taken off.
        var zip = await CreateAsync(s, new { kind = "archive", name = "Orders zip" });
        var zipScan = await UploadAsync(s, zip, Zip(files.ToDictionary(f => $"acme-orders-1111111/{f.Key}", f => f.Value)));
        var tar = await CreateAsync(s, new { kind = "archive", name = "Orders tarball" });
        var tarScan = await UploadAsync(s, tar, TarGz(files.ToDictionary(f => $"./orders/{f.Key}", f => f.Value)));

        // The same tree in a folder with a .git folder: its HEAD is read as text, and the folder is not listed.
        var folder = Path.Combine(_root, "acme", "orders");
        foreach (var (path, content) in files)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(folder, path))!);
            File.WriteAllBytes(Path.Combine(folder, path), content);
        }
        Directory.CreateDirectory(Path.Combine(folder, ".git", "refs", "heads"));
        File.WriteAllText(Path.Combine(folder, ".git", "HEAD"), "ref: refs/heads/main\n");
        File.WriteAllText(Path.Combine(folder, ".git", "packed-refs"), $"# pack-refs with: peeled\n{ImportScenario.Commit1} refs/heads/main\n");
        File.WriteAllText(Path.Combine(folder, ".git", "config"), "[core]\n\trepositoryformatversion = 0\n");
        var directory = await CreateAsync(s, new { kind = "directory", path = "acme/orders" });
        var directoryScan = await StartAsync(s, directory);
        await s.DrainAsync();

        foreach (var scan in new[] { zipScan, tarScan, directoryScan })
        {
            var read = await s.GetAsync(scan);
            Assert.Equal(("completed", expected), (read.Text("status"), read.Text("snapshotId")));
            Assert.Equal(expectedFiles, await ManifestAsync(s, scan));
        }

        // A Source without commits is cited by its Snapshot; the folder's HEAD is metadata.
        var sources = await s.Owner.GetFromJsonAsync<JsonArray>($"/api/projects/{s.ProjectId}/sources");
        var dir = sources!.Select(x => x!.AsObject()).Single(x => x.Text("kind") == "directory");
        Assert.Equal("directory/acme/orders", dir.Text("citation"));
        var snapshot = dir["latestSnapshot"]!.AsObject();
        Assert.Equal((expected, ImportScenario.Commit1), (snapshot.Text("revision"), snapshot.Text("commitSha")));
        var proposals = await s.ProposalsAsync(directoryScan);
        Assert.All(proposals.SelectMany(p => p["evidence"]!.AsArray()), e => Assert.StartsWith(
            $"directory/acme/orders@{expected}:", e!["pointer"]!.GetValue<string>(), StringComparison.Ordinal));

        // Deleting a Source keeps every blob another Snapshot still uses, and drops the rest.
        var blobs = await BlobCountAsync(s);
        Assert.Equal(HttpStatusCode.NoContent, (await s.Owner.DeleteAsync($"/api/projects/{s.ProjectId}/sources/{zip}")).StatusCode);
        Assert.Equal(blobs, await BlobCountAsync(s));
        Assert.Equal(HttpStatusCode.NotFound, (await s.Owner.GetAsync($"/api/projects/{s.ProjectId}/sources/{zip}")).StatusCode);
        foreach (var other in sources!.Select(x => x!.AsObject().Id()).Where(id => id != zip))
            Assert.Equal(HttpStatusCode.NoContent, (await s.Owner.DeleteAsync($"/api/projects/{s.ProjectId}/sources/{other}")).StatusCode);
        Assert.Equal(0, await BlobCountAsync(s));
    }

    [Fact]
    public async Task A_folder_path_or_link_that_leaves_the_root_is_refused_and_nothing_outside_is_read()
    {
        var s = await ImportScenario.CreateAsync(_local, fixture: null);
        Directory.CreateDirectory(Path.Combine(_root, "team", "api"));
        File.WriteAllText(Path.Combine(_root, "team", "api", "openapi.yaml"), "openapi: 3.0.0\ninfo: { title: Api, version: '1' }\npaths: {}\n");
        Directory.CreateSymbolicLink(Path.Combine(_root, "escape"), _outside);
        File.CreateSymbolicLink(Path.Combine(_root, "secret.yml"), Path.Combine(_outside, "secret.yml"));
        // Inside the folder: a link to a file outside the root. It is listed as git stores a link, never followed.
        File.CreateSymbolicLink(Path.Combine(_root, "team", "api", "docker-compose.yml"), Path.Combine(_outside, "secret.yml"));

        foreach (var (path, status, code) in new[]
                 {
                     ("../outside", HttpStatusCode.BadRequest, "validation_error"),
                     ("team/../../outside", HttpStatusCode.BadRequest, "validation_error"),
                     (_outside, HttpStatusCode.BadRequest, "validation_error"),
                     ("escape", HttpStatusCode.BadRequest, "directory_refused"),
                     ("escape/inner", HttpStatusCode.BadRequest, "directory_refused"),
                     ("secret.yml", HttpStatusCode.BadRequest, "directory_refused"),
                     ("missing", HttpStatusCode.BadRequest, "directory_refused"),
                 })
        {
            var response = await s.Owner.PostAsJsonAsync($"/api/projects/{s.ProjectId}/sources", new { kind = "directory", path });
            Assert.True((status, code) == (response.StatusCode, await CodeAsync(response)), $"{path}: {await response.Content.ReadAsStringAsync()}");
        }

        var api = await CreateAsync(s, new { kind = "directory", path = "team/api" });
        var scan = await StartAsync(s, api);
        await s.DrainAsync();
        Assert.Equal("completed", (await s.GetAsync(scan)).Text("status"));
        await using (var db = Db(s))
        {
            var stored = await db.Blobs.AsNoTracking().Where(b => b.WorkspaceId == s.WorkspaceId && b.Content != null).Select(b => b.Content!).ToListAsync();
            Assert.NotEmpty(stored);
            Assert.DoesNotContain(stored, c => Encoding.UTF8.GetString(c).Contains("outside-the-root", StringComparison.Ordinal));
            var link = await db.ScanFiles.AsNoTracking().SingleAsync(f => f.ScanId == scan && f.Path == "docker-compose.yml");
            Assert.Equal(GitBlob.Sha(Encoding.UTF8.GetBytes(Path.Combine(_outside, "secret.yml"))), link.BlobSha);
        }

        // A folder that is swapped for a link after the Source was made is refused when it is scanned.
        Directory.Delete(Path.Combine(_root, "team", "api"), recursive: true);
        Directory.CreateSymbolicLink(Path.Combine(_root, "team", "api"), _outside);
        var swapped = await s.Owner.PostAsJsonAsync($"/api/projects/{s.ProjectId}/sources/{api}/scans", new { });
        Assert.Equal((HttpStatusCode.Conflict, "directory_refused"), (swapped.StatusCode, await CodeAsync(swapped)));
    }

    [Fact]
    public async Task A_hosted_instance_refuses_a_directory_Source()
    {
        var database = await TestHost.CreateDatabaseAsync();
        var settings = TestHost.Hosted(TestHost.NewMasterKey());
        settings[SourcesRoot.ConfigurationKey] = _root;
        await using var factory = TestHost.Create(database, settings);
        var (owner, created, _) = await TestHost.SetUpAsync(factory, TestHost.SetupToken, https: true);
        var projectId = Guid.NewGuid();
        await TestHost.Created(owner.PostAsJsonAsync($"/api/workspaces/{created.WorkspaceId}/projects", new { id = projectId, name = "Hosted" }));

        var refused = await owner.PostAsJsonAsync($"/api/projects/{projectId}/sources", new { kind = "directory", path = "acme" });
        Assert.Equal((HttpStatusCode.BadRequest, "source_kind_not_in_profile"), (refused.StatusCode, await CodeAsync(refused)));
        var archive = await owner.PostAsJsonAsync($"/api/projects/{projectId}/sources", new { kind = "archive", name = "orders" });
        Assert.Equal(HttpStatusCode.Created, archive.StatusCode);
    }

    public static TheoryData<string> HostileArchives => ["zip-slip", "absolute", "zip-symlink", "tar-symlink", "tar-hardlink", "nested", "ratio", "oversize"];

    [Theory]
    [MemberData(nameof(HostileArchives))]
    public async Task A_hostile_archive_is_refused_whole_and_stores_no_blob(string kind)
    {
        var s = await ImportScenario.CreateAsync(_local, fixture: null);
        await OutboundHost.PutSettingsAsync(s.Owner, s.WorkspaceId, new JsonObject { ["import"] = new JsonObject { ["maxArchiveBytes"] = 1024 * 1024 } });
        var good = new Dictionary<string, byte[]> { ["app/openapi.yaml"] = "openapi: 3.0.0\npaths: {}\n"u8.ToArray() };
        var random = new byte[700 * 1024];
        new Random(7).NextBytes(random);
        var noise = Encoding.ASCII.GetBytes(Convert.ToBase64String(random));
        var (body, code) = kind switch
        {
            "zip-slip" => (Zip(good.Append(new("../x.yaml", "a: 1\n"u8.ToArray())).ToDictionary()), "archive_path_traversal"),
            "absolute" => (Zip(good.Append(new("/etc/x.yaml", "a: 1\n"u8.ToArray())).ToDictionary()), "archive_absolute_path"),
            "zip-symlink" => (Zip(good, symlink: ("app/link.yaml", "/etc/passwd")), "archive_link_entry"),
            "tar-symlink" => (TarGz(good, link: (TarEntryType.SymbolicLink, "app/link.yaml", "/etc/passwd")), "archive_link_entry"),
            "tar-hardlink" => (TarGz(good, link: (TarEntryType.HardLink, "app/hard.yaml", "app/openapi.yaml")), "archive_link_entry"),
            "nested" => (Zip(good.Append(new("app/notes.txt", Zip(good))).ToDictionary()), "archive_nested"),
            "ratio" => (Zip(good.Append(new("app/zeros.txt", new byte[900 * 1024])).ToDictionary()), "archive_compression_ratio"),
            _ => (Zip(good.Append(new("app/a.txt", noise)).Append(new("app/b.txt", noise)).ToDictionary()), "archive_too_large"),
        };

        var source = await CreateAsync(s, new { kind = "archive", name = "Hostile" });
        var response = await PostArchiveAsync(s, source, body);
        Assert.True((HttpStatusCode.BadRequest, code) == (response.StatusCode, await CodeAsync(response)), await response.Content.ReadAsStringAsync());
        Assert.Equal(0, await BlobCountAsync(s));
        await using var db = Db(s);
        Assert.False(await db.Scans.AnyAsync(x => x.SourceId == source));
    }

    [Fact]
    public async Task A_bulk_add_queues_one_scan_per_repository_bounded_per_host_and_one_failure_stops_no_other()
    {
        var s = await ImportScenario.CreateAsync(_local);
        // acme/broken does not exist on this GitHub; the others are the orders fixture.
        _local.Host.Transport.Forget(s.GitHubHost);
        _local.Host.Transport.For(s.GitHubHost).Then(sent => sent.Uri.AbsolutePath.StartsWith("/repos/acme/broken", StringComparison.Ordinal)
            ? Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("""{"message":"Not Found"}""") })
            : s.GitHub.Handle(sent));

        var added = await s.Owner.PostAsJsonAsync($"/api/projects/{s.ProjectId}/sources/bulk",
            new { kind = "github", repositories = new[] { "acme/orders", "acme/broken", "not a name", "acme/billing" } });
        Assert.Equal(HttpStatusCode.OK, added.StatusCode);
        var items = (await added.Content.ReadFromJsonAsync<JsonObject>())!["items"]!.AsArray().Select(i => i!.AsObject()).ToList();
        Assert.Equal(4, items.Count);
        Assert.Equal("validation_error", items[2]["error"]!.AsObject().Text("type"));
        var queued = items.Where(i => i["scanId"] is not null).ToDictionary(i => i.Text("repository")!, i => i.Id("scanId"));
        Assert.Equal(["acme/billing", "acme/broken", "acme/orders"], queued.Keys.Order(StringComparer.Ordinal));

        // While as many scans of the host run as the bound allows, the next one waits.
        var running = queued.Values.Take(2).ToArray();
        await s.SqlAsync($"UPDATE import.scans SET status = 'running', lease_expires_at = now() + interval '1 minute' WHERE id = ANY({running})");
        Assert.False(await s.Worker.RunOnceAsync("worker-a", CancellationToken.None));
        await s.SqlAsync($"UPDATE import.scans SET status = 'queued', lease_expires_at = NULL WHERE id = ANY({running})");

        await s.DrainAsync();
        Assert.Equal("completed", (await s.GetAsync(queued["acme/orders"])).Text("status"));
        Assert.Equal("completed", (await s.GetAsync(queued["acme/billing"])).Text("status"));
        var broken = await s.GetAsync(queued["acme/broken"]);
        Assert.Equal(("failed", "not_found"), (broken.Text("status"), broken["error"]!["code"]!.GetValue<string>()));
        Assert.False(broken["isComplete"]!.GetValue<bool>());

        // Each Source shows its own last scan.
        var sources = (await s.Owner.GetFromJsonAsync<JsonArray>($"/api/projects/{s.ProjectId}/sources"))!.Select(x => x!.AsObject()).ToList();
        Assert.Equal("failed", sources.Single(x => x.Text("locator") == "acme/broken")["latestScan"]!.AsObject().Text("status"));
        Assert.Equal("completed", sources.Single(x => x.Text("locator") == "acme/billing")["latestScan"]!.AsObject().Text("status"));
    }

    private static async Task<Guid> CreateAsync(ImportScenario s, object body)
    {
        var response = await s.Owner.PostAsJsonAsync($"/api/projects/{s.ProjectId}/sources", body);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<JsonObject>())!.Id();
    }

    private static async Task<Guid> StartAsync(ImportScenario s, Guid source)
    {
        s.Import.Time.Advance(TimeSpan.FromSeconds(1));
        var response = await s.Owner.PostAsJsonAsync($"/api/projects/{s.ProjectId}/sources/{source}/scans", new { });
        Assert.True(response.StatusCode == HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<JsonObject>())!.Id();
    }

    private static async Task<Guid> UploadAsync(ImportScenario s, Guid source, byte[] archive)
    {
        s.Import.Time.Advance(TimeSpan.FromSeconds(1));
        var response = await PostArchiveAsync(s, source, archive);
        Assert.True(response.StatusCode == HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync());
        var id = (await response.Content.ReadFromJsonAsync<JsonObject>())!.Id();
        await s.DrainAsync();
        return id;
    }

    private static Task<HttpResponseMessage> PostArchiveAsync(ImportScenario s, Guid source, byte[] archive)
    {
        var content = new ByteArrayContent(archive);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        return s.Owner.PostAsync($"/api/projects/{s.ProjectId}/sources/{source}/archive", content);
    }

    private static async Task<string?> CodeAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonObject>())?.Text("type");

    private static AppDbContext Db(ImportScenario s) =>
        s.Import.Host.Factory.Services.CreateScope().ServiceProvider.GetRequiredService<AppDbContext>();

    private static async Task<int> BlobCountAsync(ImportScenario s)
    {
        await using var db = Db(s);
        return await db.Blobs.CountAsync(b => b.WorkspaceId == s.WorkspaceId);
    }

    /// <summary>The files a scan read, as path and blob SHA.</summary>
    private static async Task<List<(string, string, long)>> ManifestAsync(ImportScenario s, Guid scan)
    {
        await using var db = Db(s);
        return (await db.ScanFiles.AsNoTracking().Where(f => f.ScanId == scan && f.State == Vellum.Modules.Import.ScanFileState.Read)
                .Select(f => new { f.Path, f.BlobSha, f.Size }).ToListAsync())
            .Select(f => (f.Path, f.BlobSha, f.Size)).OrderBy(f => f.Path, StringComparer.Ordinal).ToList();
    }

    private static byte[] Zip(IReadOnlyDictionary<string, byte[]> files, (string Path, string Target)? symlink = null)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, content) in files)
            {
                using var stream = zip.CreateEntry(path, CompressionLevel.Optimal).Open();
                stream.Write(content);
            }
            if (symlink is { } link)
            {
                var entry = zip.CreateEntry(link.Path);
                entry.ExternalAttributes = unchecked((int)(0xA1FFu << 16));
                using var stream = entry.Open();
                stream.Write(Encoding.UTF8.GetBytes(link.Target));
            }
        }
        return buffer.ToArray();
    }

    private static byte[] TarGz(IReadOnlyDictionary<string, byte[]> files, (TarEntryType Type, string Path, string Target)? link = null)
    {
        using var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionLevel.Optimal, leaveOpen: true))
        using (var tar = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: true))
        {
            foreach (var (path, content) in files)
                tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, path) { DataStream = new MemoryStream(content) });
            if (link is { } l)
                tar.WriteEntry(new PaxTarEntry(l.Type, l.Path) { LinkName = l.Target });
        }
        return buffer.ToArray();
    }
}
