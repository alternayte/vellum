using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Deedbox;
using Microsoft.Extensions.DependencyInjection;
using Vellum.Modules.Build;
using Vellum.Modules.Promises;

namespace Vellum.Tests.Modules.Build;

/// <summary>
/// An export takes a pinned state from a caller only when Vellum issued that state: the seal
/// of the state verifies for the project. A state that a caller made, with a promise taken
/// out or a commit invented, is refused, so it cannot give a bundle that says build ready.
/// A bundle from before the seal is still checked.
/// </summary>
public class BuildSealTests(IntegrationFixture fixture) : IClassFixture<IntegrationFixture>
{
    private static string Build(Guid projectId, string path) => $"/api/projects/{projectId}/build/{path}";

    private static async Task<JsonObject> Ok(Task<HttpResponseMessage> request, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = await request;
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode}: {text}");
        return JsonNode.Parse(text)!.AsObject();
    }

    private static Dictionary<string, byte[]> Unzip(byte[] zip)
    {
        using var archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read);
        return archive.Entries.ToDictionary(e => e.FullName, e =>
        {
            using var stream = e.Open();
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            return buffer.ToArray();
        });
    }

    private static Task<HttpResponseMessage> VerifyZipAsync(HttpClient client, Guid projectId, byte[] zip)
    {
        var content = new ByteArrayContent(zip);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        return client.PostAsync(Build(projectId, "verify"), content);
    }

    /// <summary>The export of a pinned state that Vellum did not issue is refused, and no header says build ready.</summary>
    private static async Task RefusedAsync(HttpClient client, Guid projectId, JsonNode pinned)
    {
        foreach (var format in new[] { "sdd", "bundle" })
        {
            var response = await client.PostAsJsonAsync(Build(projectId, "export"), new { format, pinned });
            var ready = response.Headers.TryGetValues("X-Vellum-Build-Ready", out var values) ? values.Single() : "(no header)";
            var text = format == "sdd" || response.StatusCode != HttpStatusCode.OK ? await response.Content.ReadAsStringAsync() : "(a zip)";
            Assert.True(response.StatusCode == HttpStatusCode.Conflict, $"{(int)response.StatusCode} with X-Vellum-Build-Ready: {ready}\n{(text.Length > 400 ? text[..400] : text)}");
            var refusal = JsonNode.Parse(text)!.AsObject();
            Assert.Equal("pinned_not_issued", (string)refusal["type"]!);
            Assert.Contains("GET build/manifest", (string)refusal["detail"]!);
            Assert.Equal("(no header)", ready);
        }
    }

    [Fact]
    public async Task An_export_takes_only_a_pinned_state_that_vellum_issued()
    {
        var factory = fixture.Factory;
        var owner = await TestHost.SignInAsync(factory);
        var (workspaceId, projectId) = await TestHost.CreateProjectAsync(owner);
        var otherProject = Guid.NewGuid();
        await TestHost.Created(owner.PostAsJsonAsync($"/api/workspaces/{workspaceId}/projects", new { id = otherProject, name = "Test Project" }));

        // One draft promise is all that stands between this project and build ready.
        await TestHost.Created(owner.PostAsJsonAsync($"/api/projects/{projectId}/elements", new { id = Guid.NewGuid(), kind = "system", name = "Orders" }));
        await TestHost.Created(owner.PostAsJsonAsync($"/api/projects/{otherProject}/elements", new { id = Guid.NewGuid(), kind = "system", name = "Orders" }));
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync($"/api/projects/{projectId}/promises/prefix", new { prefix = "ORD", startAt = 14 })).StatusCode);
        await Ok(owner.PostAsJsonAsync($"/api/projects/{projectId}/promises", new { title = "Orders are not lost", claim = "No order is lost." }), HttpStatusCode.Created);
        var agent = (await TestHost.CreateAgentAsync(factory, owner, workspaceId, ["export"])).Client;
        var manifest = await Ok(agent.GetAsync(Build(projectId, "manifest")));
        Assert.False((bool)manifest["buildReady"]!);
        Assert.All(manifest["blockers"]!.AsArray(), b => Assert.StartsWith("promise-", (string)b!["code"]!));
        var pinned = manifest["pinned"]!.AsObject();
        Assert.Single(pinned["promises"]!.AsArray());

        // A subset: the state with the promise taken out has no blocker. A token that only exports cannot have it.
        var subset = pinned.DeepClone().AsObject();
        subset["promises"] = new JsonArray();
        await RefusedAsync(agent, projectId, subset);
        var unsealed = subset.DeepClone().AsObject();
        Assert.True(unsealed.Remove("seal"));
        await RefusedAsync(agent, projectId, unsealed);

        // Invented sources: a commit that no scan read would make stale evidence read as current.
        var invented = pinned.DeepClone().AsObject();
        invented["sources"] = new JsonArray(new JsonObject
        {
            ["repository"] = "github.com/acme/orders", ["trackedRef"] = "main", ["trackedCommit"] = "0123abc", ["latestCommit"] = "0123abc",
        });
        await RefusedAsync(owner, projectId, invented);

        // A seal of another project: the two states differ only in the project they name.
        var foreign = pinned.DeepClone().AsObject();
        foreign["seal"] = (await Ok(owner.GetAsync(Build(otherProject, "manifest"))))["pinned"]!["seal"]!.DeepClone();
        await RefusedAsync(owner, projectId, foreign);

        // The state as Vellum issued it is exported, and it gives the bundle of the export that pinned it.
        var first = await agent.PostAsJsonAsync(Build(projectId, "export"), new { format = "bundle" });
        var zip = await first.Content.ReadAsByteArrayAsync();
        var again = await agent.PostAsJsonAsync(Build(projectId, "export"), new { format = "bundle", pinned });
        Assert.Equal((HttpStatusCode.OK, "false", "true"), (again.StatusCode, again.Headers.GetValues("X-Vellum-Build-Ready").Single(), again.Headers.GetValues("X-Vellum-Current").Single()));
        Assert.Equal((string)manifest["bundleHash"]!, again.Headers.GetValues("X-Vellum-Bundle-Hash").Single());
        Assert.Equal(first.Headers.GetValues("X-Vellum-Bundle-Hash").Single(), again.Headers.GetValues("X-Vellum-Bundle-Hash").Single());
        Assert.Equal(zip, await again.Content.ReadAsByteArrayAsync());
        Assert.Equal("vellum-bundle/4", (string)manifest["format"]!);
        var inZip = JsonNode.Parse(Unzip(zip)["manifest.json"])!;
        Assert.Equal((string)pinned["seal"]!, (string)inZip["pinned"]!["seal"]!);
        var checkedZip = await Ok(VerifyZipAsync(agent, projectId, zip));
        Assert.Equal((true, true, true, true), ((bool)checkedZip["identical"]!, (bool)checkedZip["matchesPinned"]!, (bool)checkedZip["current"]!, (bool)checkedZip["issued"]!));

        // A manifest alone with the promise taken out and its numbers made to agree: the check
        // says that Vellum did not issue the state and that it is not the state of now.
        var forged = await Ok(agent.PostAsync(Build(projectId, "verify"), JsonContent.Create(new JsonObject { ["manifest"] = new JsonObject { ["pinned"] = unsealed.DeepClone() } })));
        Assert.Equal((false, false), ((bool)forged["issued"]!, (bool)forged["current"]!));
        Assert.Contains(forged["moved"]!.AsArray(), m => (string)m!["kind"]! == "promise");

        // Bundles from before the seal, of the formats 3 and 2, are still what their pinned versions generate.
        foreach (var (format, hasDecisions) in new[] { ("vellum-bundle/3", true), ("vellum-bundle/2", false) })
        {
            var old = pinned.DeepClone().AsObject();
            old.Remove("seal");
            if (!hasDecisions)
                old.Remove("decisions");
            byte[] oldZip;
            using (var scope = factory.Services.CreateScope())
            {
                var pin = JsonSerializer.Deserialize<PinnedState>(old.ToJsonString(), PromiseJson.Options)!;
                var model = await PinnedModel.AtVersionAsync(scope.ServiceProvider.GetRequiredService<IEventStore>(), pin.Branch, pin.StreamVersion, CancellationToken.None);
                var input = await BuildInput.LoadAsync(
                    scope.ServiceProvider, projectId, new PinnedModel(new ModelPin(pin.Branch, pin.StreamVersion, null, null), model!),
                    BuildLimits.Default, CancellationToken.None, pin);
                oldZip = Bundle.Build(input, BuildLimits.Default).Zip();
            }
            var oldManifest = JsonNode.Parse(Unzip(oldZip)["manifest.json"])!;
            Assert.Equal(format, (string)oldManifest["format"]!);
            Assert.False(oldManifest["pinned"]!.AsObject().ContainsKey("seal"));
            var verified = await Ok(VerifyZipAsync(agent, projectId, oldZip));
            Assert.Equal((true, true, "match", false), ((bool)verified["identical"]!, (bool)verified["matchesPinned"]!, (string)verified["manifest"]!, (bool)verified["issued"]!));
            if (hasDecisions)
            {
                // The seal changes no file: the bundle of format 3 and the bundle of format 4 of one state have one bundle hash.
                Assert.Equal((string)manifest["bundleHash"]!, (string)oldManifest["bundleHash"]!);
                await RefusedAsync(agent, projectId, oldManifest["pinned"]!);
            }
        }
    }
}
