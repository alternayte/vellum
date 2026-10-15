using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Vellum.Modules.Import.Proposals;
using Vellum.Modules.Import.Scans;
using Vellum.Modules.Import.Sources;
using Vellum.Shared;

namespace Vellum.Modules.Import.Runtime;

/// <summary>The connection of a runtime Source, as <see cref="SourceEntity.Connection"/> holds it. No credential is in it.</summary>
/// <param name="AccountId">The New Relic account. Null for a <c>traces</c> Source.</param>
/// <param name="Region"><c>us</c> or <c>eu</c> for a <c>newrelic</c> Source.</param>
public sealed record RuntimeConnection(long? AccountId, string? Region)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static RuntimeConnection FromJson(string json) =>
        JsonSerializer.Deserialize<RuntimeConnection>(json, Json) ?? new RuntimeConnection(null, null);
}

/// <summary>
/// The fetch stage of the runtime kinds. Its product is one blob: the derived
/// <see cref="RuntimeGraph"/> document, which is the Snapshot's only file. A <c>traces</c>
/// Source got it when the file was uploaded; a <c>newrelic</c> Source gets it here, from
/// NerdGraph. The commit SHA of the scan is the Snapshot ID, so a citation reads
/// <c>&lt;source&gt;@&lt;snapshot&gt;:path:line-line</c>.
/// </summary>
internal static class RuntimeFetch
{
    public const string TracesRule = "runtime.traces";
    public const string NewRelicRule = "runtime.newrelic";

    public static bool IsRuntime(SourceKind kind) => kind is SourceKind.Traces or SourceKind.Newrelic;

    public static string RuleOf(SourceKind kind) => kind == SourceKind.Traces ? TracesRule : NewRelicRule;

    /// <summary>The path a trace upload is cited under: the file's own name, after a fixed folder.</summary>
    public static string TracePath(string fileName) => "traces/" + fileName;

    public static string AccountPath(long accountId) => $"newrelic/account-{accountId}.jsonl";

    /// <summary>A trace upload is read when it arrives. Here its blob must still be there.</summary>
    public static async Task TracesAsync(FetchContext context, CancellationToken ct)
    {
        await context.RequeueLostAsync(ct);
        foreach (var file in await context.PendingAsync(ct))
        {
            if (!await context.UseStoredAsync(file, ct))
                throw ScanStop.Failed(new ScanError("trace_upload_missing",
                    "The derived edges of this upload are no longer stored. Upload the trace file again."));
        }
        await context.SaveAsync(ct);
    }

    public static async Task NewRelicAsync(FetchContext context, CancellationToken ct)
    {
        var (scan, source) = (context.Scan, context.Source);
        if (scan.CommitSha is not null && scan.TreeListed)
        {
            // A resumed scan that has its document reads it again; the account is not asked twice.
            await context.RequeueLostAsync(ct);
            if ((await context.PendingAsync(ct)).Count == 0)
                return;
            // The stored document is gone. The scan asks New Relic again, as a new Snapshot.
            scan.CommitSha = null;
            scan.TreeListed = false;
            await context.Db.ScanFiles.Where(f => f.ScanId == scan.Id).ExecuteDeleteAsync(ct);
        }

        var connection = RuntimeConnection.FromJson(source.Connection);
        if (connection is not { AccountId: { } accountId, Region: { } region } || !NewRelicRegions.IsRegion(region))
            throw ScanStop.Failed(new ScanError("newrelic_not_configured", "This Source names no New Relic account and region. Create the Source again."));

        var reader = context.Services.GetRequiredService<NewRelicReader>();
        var (key, keyFailure) = await reader.KeyAsync(scan.WorkspaceId, source.Id, region, ct);
        if (keyFailure is not null)
            throw Stop(keyFailure, context);
        var (read, failure) = await reader.ReadAccountAsync(region, key!, accountId, ct);
        if (failure is not null)
            throw Stop(failure, context);

        var graph = Graph(read!, accountId, region);
        var document = graph.Write();
        var path = AccountPath(accountId);
        var blobSha = GitBlob.Sha(document);
        scan.CommitSha = graph.SnapshotId();
        scan.ResolvedRef = null;
        scan.TreeListed = true;
        scan.FilesListed = 1;
        scan.Partial |= !read!.Complete;
        scan.Notes = ScanNotes.Write(ScanNotes.Read(scan.Notes).Concat(read.Notes.Select(n => ScanNotes.Of(n, ""))));
        var row = new ScanFileEntity { ScanId = scan.Id, Path = path, PathHash = KeyHash.Of(path), BlobSha = blobSha, Size = document.LongLength };
        context.Db.ScanFiles.Add(row);
        await context.AcceptDerivedAsync(row, document, ct);
        await context.SaveAsync(ct);
    }

    /// <summary>
    /// Entities become services keyed by their GUID, the external key of their source binding.
    /// Relationships of type CALLS, PRODUCES and CONSUMES between two of them become edges. The
    /// documented NerdGraph answer does not say whether New Relic or a person made a
    /// relationship, so every relationship is <c>declared</c>; an entity, which New Relic
    /// makes from what its agents report, is <c>measured</c>.
    /// </summary>
    internal static RuntimeGraph Graph(NewRelicAccountRead read, long accountId, string region)
    {
        var services = new Dictionary<string, RuntimeService>(StringComparer.Ordinal);
        foreach (var entity in read.Entities)
        {
            if (RuntimeGraph.CleanName(entity.Name) is not { } name || !IsKey(entity.Guid))
                continue;
            services[entity.Guid] = new RuntimeService(entity.Guid, name, "measured", new Dictionary<string, string>
            {
                ["newrelic.domain"] = entity.Domain,
                ["newrelic.type"] = entity.Type,
                ["newrelic.entityType"] = entity.EntityType,
            });
        }

        var edges = new Dictionary<string, RuntimeEdge>(StringComparer.Ordinal);
        foreach (var relationship in read.Relationships)
        {
            var type = relationship.Type.ToUpperInvariant();
            if (type is not ("CALLS" or "PRODUCES" or "CONSUMES")
                || !services.ContainsKey(relationship.Source) || !services.ContainsKey(relationship.Target)
                || relationship.Source == relationship.Target)
                continue;
            var key = $"{type}:{relationship.Source}->{relationship.Target}";
            edges.TryAdd(key, new RuntimeEdge(key, relationship.Source, relationship.Target, type switch
            {
                "CALLS" => "calls",
                "PRODUCES" => "produces to",
                _ => "consumes from",
            }, "declared", new Dictionary<string, string> { ["newrelic.relationship"] = type }));
        }

        var header = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["kind"] = "newrelic",
            ["account"] = RuntimeGraph.Number(accountId),
            ["region"] = region,
            ["entities"] = RuntimeGraph.Number(services.Count),
            ["relationships"] = RuntimeGraph.Number(edges.Count),
        };
        return new RuntimeGraph(header, [.. services.Values], [.. edges.Values], read.Complete);
    }

    private static bool IsKey(string guid) => guid.Length is > 0 and <= 200 && TextRule.Line("guid", guid) is null;

    private static ScanStop Stop(NewRelicFailure failure, FetchContext context) => failure switch
    {
        NewRelicFailure.RateLimited limited => ScanStop.Waiting(context.RetryAt(limited.RetryAt)),
        NewRelicFailure.Unauthorized => ScanStop.Failed(new ScanError("newrelic_unauthorized",
            "New Relic refused the key, or the key cannot read this account. Enter a User API key of a user who can read the account." + ScanFailures.ResumeHint)),
        NewRelicFailure.Unavailable { Reason: "no_key" or "secret_unavailable" } => ScanStop.Failed(new ScanError("newrelic_no_key",
            "No usable API key is stored for this Source. Enter the key again." + ScanFailures.ResumeHint)),
        _ when NewRelicReader.IsNetwork(failure) => ScanStop.Failed(new ScanError("newrelic_unavailable",
            "New Relic cannot be reached from this instance. The Source is unavailable; everything else works without it. Resume the scan when the connection works.")),
        _ => ScanStop.Failed(new ScanError("newrelic_error",
            "New Relic did not give a usable answer. Resume the scan later.")),
    };
}
