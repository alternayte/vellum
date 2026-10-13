using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;

namespace Vellum.Modules.Mcp.Tools;

/// <summary>
/// The tools over the build endpoints: the manifest, the SDD, the brief and the check of a
/// manifest. Each calls the endpoint the HTTP API has for the same thing. A document that
/// is larger than one result holds is never cut: the result holds its manifest, and the
/// notice names the endpoint that gives the document.
/// </summary>
internal static class BuildTools
{
    private const string Revision = "A named model revision. Default: the branch at its version now.";
    private const string Branch = "A draft or variant branch. Default: the main model.";

    public static async Task<CallToolResult> GetBuildManifest(
        McpCall call,
        [Description("The ID of the project.")] Guid projectId,
        [Description(Revision)] Guid? revisionId = null,
        [Description(Branch)] Guid? branchId = null)
    {
        var reply = await call.SendAsync(query: Pin(revisionId, branchId));
        if (!reply.Succeeded)
            return call.Refused(reply);

        var manifest = reply.Json();
        var notice = Bound(manifest, $"GET /api/projects/{projectId}/build/manifest gives the whole manifest.");
        return call.Ok(manifest, notice: notice);
    }

    public static async Task<CallToolResult> ExportSdd(
        McpCall call,
        [Description("The ID of the project.")] Guid projectId,
        [Description(Revision)] Guid? revisionId = null,
        [Description(Branch)] Guid? branchId = null)
    {
        // One pinned state gives both the manifest and the SDD: the manifest is read first, and
        // the SDD is generated from exactly the versions it pins. So the hash, the version and
        // the manifest of the result are of the SDD of the result, also when the model moves
        // between the two calls. The export takes the pinned state because the manifest carries
        // the seal that Vellum gave it: a state that this tool or its caller made is refused.
        var pinned = await call.SendToAsync(AgentTools.Find("get_build_manifest").Endpoint!, query: Pin(revisionId, branchId));
        if (!pinned.Succeeded)
            return call.Refused(pinned);
        var manifest = pinned.Json();

        var reply = await call.SendAsync(body: new JsonObject { ["format"] = "sdd", ["pinned"] = manifest["pinned"]!.DeepClone() });
        if (!reply.Succeeded)
            return call.Refused(reply);

        var data = new JsonObject
        {
            ["bundleHash"] = reply.Headers["X-Vellum-Bundle-Hash"].ToString(),
            ["streamVersion"] = long.Parse(reply.Headers["X-Vellum-Stream-Version"].ToString(), System.Globalization.CultureInfo.InvariantCulture),
            ["buildReady"] = reply.Headers["X-Vellum-Build-Ready"].ToString() == "true",
            ["current"] = reply.Headers["X-Vellum-Current"].ToString() == "true",
            ["bytes"] = reply.Body.Length,
            ["markdown"] = reply.Text(),
        };
        if (McpOutput.Fits(data))
            return call.Ok(data);

        // The SDD is not cut: a part of a specification reads as the whole of it.
        data["markdown"] = null;
        data["manifest"] = manifest;
        Bound(manifest, "");
        return call.Ok(data, notice:
            $"The SDD has {reply.Body.Length} bytes, more than one result holds, so it is not here. The manifest in the result is the manifest of that SDD, with its long lists cut; totals has the real counts. POST /api/projects/{projectId}/build/export with the body {{\"format\":\"sdd\"}} gives the SDD of the model as it is then; the same token works there. Its X-Vellum-Bundle-Hash header equals bundleHash here unless the model moved. To get the SDD of exactly this state, send the pinned member of GET /api/projects/{projectId}/build/manifest as pinned in that body, whole and unchanged: its seal says that Vellum issued it, and the export refuses a state with no seal or with a changed member (409 pinned_not_issued). The pinned member in this result has cut lists, so it is not that state.");
    }

    public static async Task<CallToolResult> ExportBrief(
        McpCall call,
        [Description("The ID of the project.")] Guid projectId,
        [Description("The IDs of the promises the change is for (GUIDs, not requirement IDs).")] Guid[]? promiseIds = null,
        [Description("The IDs of the scenarios the change is for.")] Guid[]? scenarioIds = null,
        [Description("The IDs of the elements the change is for.")] Guid[]? elementIds = null,
        [Description(Revision)] Guid? revisionId = null,
        [Description(Branch)] Guid? branchId = null)
    {
        static JsonArray Ids(Guid[]? ids) => new([.. (ids ?? []).Select(id => (JsonNode)id)]);
        var reply = await call.SendAsync(body: new JsonObject
        {
            ["promiseIds"] = Ids(promiseIds), ["scenarioIds"] = Ids(scenarioIds), ["elementIds"] = Ids(elementIds),
            ["revisionId"] = revisionId, ["branchId"] = branchId,
        });
        if (!reply.Succeeded)
            return call.Refused(reply);

        var data = reply.Json();
        if (McpOutput.Fits(data))
            return call.Ok(data);

        var bytes = data["manifest"]?["bytes"]?.ToString();
        data["brief"] = null;
        Bound(data["manifest"]!.AsObject(), "");
        return call.Ok(data, notice:
            $"The brief has {bytes} bytes, more than one result holds, so it is not here. POST /api/projects/{projectId}/build/brief with the same IDs gives it; the same token works there. Its manifest is in the result, with its long lists cut; totals has the real counts. A smaller selection gives a smaller brief.");
    }

    public static async Task<CallToolResult> VerifyBundleManifest(
        McpCall call,
        [Description("The ID of the project.")] Guid projectId,
        [Description("The manifest.json of a bundle, whole, as a JSON object.")] JsonElement manifest)
    {
        if (manifest.ValueKind != JsonValueKind.Object)
            return ModelTools.Invalid("manifest", "The manifest is a JSON object: the manifest.json of a bundle");

        var reply = await call.SendAsync(body: new JsonObject { ["manifest"] = JsonNode.Parse(manifest.GetRawText()) });
        if (!reply.Succeeded)
            return call.Refused(reply);

        var verified = reply.Json();
        var data = new JsonObject
        {
            ["matchesPinned"] = verified["matchesPinned"]?.DeepClone(),
            ["current"] = verified["current"]?.DeepClone(),
            ["moved"] = verified["moved"]?.DeepClone(),
            ["issued"] = verified["issued"]?.DeepClone(),
            ["manifest"] = verified["manifest"]?.DeepClone(),
            ["manifestDifferences"] = verified["manifestDifferences"]?.DeepClone(),
            ["expectedBundleHash"] = verified["expectedBundleHash"]?.DeepClone(),
            ["actualBundleHash"] = verified["actualBundleHash"]?.DeepClone(),
            ["differingFiles"] = new JsonArray([.. verified["files"]!.AsArray().Where(f => f!["status"]?.ToString() != "match").Select(f => f!.DeepClone())]),
            ["selfCheckPassed"] = verified["selfCheck"]?["passed"]?.DeepClone(),
        };
        return call.Ok(data, notice: Bound(data, $"POST /api/projects/{projectId}/build/verify gives every item."));
    }

    private static Dictionary<string, string?> Pin(Guid? revisionId, Guid? branchId) =>
        new() { ["revisionId"] = revisionId?.ToString(), ["branchId"] = branchId?.ToString() };

    /// <summary>
    /// Cuts every list inside <paramref name="document"/> to <see cref="McpOutput.MaxListed"/>
    /// items, and to fewer while the document is over the byte bound, and writes the real
    /// counts to its <c>totals</c>. Returns the notice, or null when no list was cut.
    /// </summary>
    private static string? Bound(JsonObject document, string whole)
    {
        var totals = new JsonObject();
        var keep = McpOutput.MaxListed;
        void Walk(JsonNode? node, string path)
        {
            switch (node)
            {
                case JsonObject members:
                    foreach (var (name, member) in members.ToList())
                        Walk(member, path.Length == 0 ? name : $"{path}.{name}");
                    break;
                case JsonArray items when McpOutput.Cap(items, keep) is { } total:
                    // The count of the first cut is the real one.
                    totals.TryAdd(path, total);
                    break;
            }
        }

        Walk(document, "");
        while (!McpOutput.Fits(document) && keep > 1)
        {
            keep /= 2;
            Walk(document, "");
        }
        if (totals.Count == 0)
            return null;

        var cut = string.Join(", ", totals.Select(t => t.Key));
        document["totals"] = totals;
        return $"These lists were cut to {keep} items each: {cut}. totals has the real counts. {whole}".TrimEnd();
    }
}
