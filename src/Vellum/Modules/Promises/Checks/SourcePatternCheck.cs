using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Vellum.Modules.Import;
using Vellum.Modules.Import.Proposals;
using Vellum.Modules.Import.Scans;

namespace Vellum.Modules.Promises.Checks;

/// <summary>
/// The <c>source-pattern-check</c>: looks at one import snapshot (one scan of one commit) for
/// a fact whose <c>pattern</c> attribute has the value the obligation names. A fact is found
/// by that attribute alone: fact keys and fact kinds are the importer's own and are not read
/// here, except that a service is matched by the key an accepted import linked to the element.
/// </summary>
public static class SourcePatternCheck
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private const string PatternAttribute = "pattern";
    private const string ServiceAttribute = "service";
    private const int MaxEvidence = 50;

    /// <summary>The scan to look at, or why the request cannot name it.</summary>
    public static async Task<(ScanEntity? Scan, string? Refusal)> FindScanAsync(
        ImportDbContext importDb, Guid projectId, SourcePatternRecipe recipe, Guid scanId, CancellationToken ct)
    {
        var scan = await importDb.Scans.AsNoTracking().FirstOrDefaultAsync(s => s.Id == scanId && s.ProjectId == projectId, ct);
        if (scan is null)
            return (null, "No scan with this ID exists in the project");
        return scan.Repository == recipe.Repository
            ? (scan, null)
            : (null, $"The scan is of {scan.Repository}; the obligation names {recipe.Repository}");
    }

    public static CheckOutcome NoSnapshotGiven(SourcePatternRecipe recipe) => new(
        RunResult.NotRun, "source", $"Pattern '{recipe.Pattern}' in {recipe.Repository}.",
        "No import snapshot was given. Name a completed scan of the repository in scanId.", [], []);

    public static async Task<CheckOutcome> RunAsync(
        ImportDbContext importDb, PromisesDbContext promisesDb, Guid projectId, Guid promiseId, string obligationId,
        SourcePatternRecipe recipe, ScanEntity scan, CancellationToken ct)
    {
        var snapshot = await importDb.Snapshots.AsNoTracking().FirstOrDefaultAsync(s => s.ScanId == scan.Id, ct);
        if (snapshot is null)
        {
            return new CheckOutcome(
                RunResult.NotRun, "source", $"Pattern '{recipe.Pattern}' in {recipe.Repository}, scan {scan.Id}.",
                $"The scan has no snapshot (status '{scan.Status}'). Only a completed scan has one.", [], [], ScanId: scan.Id);
        }

        var repository = $"{snapshot.Host}/{snapshot.Repository}";
        var environment = $"source@{snapshot.CommitSha}";
        var limitations = new List<string>
        {
            "A source fact shows that the code declares the pattern at the cited lines of this commit. It does not show that the pattern works at run time.",
        };

        HashSet<string>? serviceKeys = null;
        var serviceText = "";
        if (recipe.Service is { } service)
        {
            serviceText = $" for service element {service}";
            serviceKeys = (await importDb.FactLinks.AsNoTracking()
                    .Where(l => l.ProjectId == projectId && l.Host == snapshot.Host && l.Repository == snapshot.Repository && l.EntityId == service)
                    .Select(l => l.FactKey)
                    .ToListAsync(ct))
                .ToHashSet(StringComparer.Ordinal);
        }

        var scope = $"Pattern '{recipe.Pattern}'{serviceText} in {repository} at commit {snapshot.CommitSha} (scan {snapshot.ScanId}).";
        CheckOutcome Outcome(string result, string detail, IReadOnlyList<EvidenceItem> evidence) =>
            new(result, environment, scope, detail, evidence, limitations, CommitSha: snapshot.CommitSha, ScanId: snapshot.ScanId);

        if (serviceKeys is { Count: 0 })
        {
            return Outcome(RunResult.Unsupported,
                "No accepted import links the service element to a service of this repository, so a pattern fact cannot be attributed to it.", []);
        }

        var facts = await importDb.SnapshotFacts.AsNoTracking()
            .Where(f => f.ScanId == snapshot.ScanId)
            .OrderBy(f => f.Seq)
            .Select(f => new { f.Attributes, f.Evidence })
            .ToListAsync(ct);

        var evidence = new List<EvidenceItem>();
        foreach (var fact in facts)
        {
            var attributes = Attributes(fact.Attributes);
            if (attributes.GetValueOrDefault(PatternAttribute) != recipe.Pattern)
                continue;
            if (serviceKeys is not null && (!attributes.TryGetValue(ServiceAttribute, out var owner) || !serviceKeys.Contains(owner)))
                continue;

            // A run keeps at most MaxEvidence pointers; they are enough to find the declaration.
            foreach (var reference in (JsonSerializer.Deserialize<List<EvidenceRef>>(fact.Evidence, Json) ?? []).Take(MaxEvidence - evidence.Count))
                evidence.Add(new EvidenceItem(EvidenceKind.Source, Pointer: Pointer(repository, snapshot.CommitSha, reference), Url: Url(repository, snapshot.CommitSha, reference)));
        }

        if (evidence.Count > 0)
        {
            return Outcome(RunResult.Passed,
                string.Create(CultureInfo.InvariantCulture, $"The pattern is declared at {evidence.Count} place(s) of this commit."), evidence);
        }

        var before = await PreviouslyFoundAsync(promisesDb, promiseId, obligationId, ct);
        if (!EstablishesAbsence(snapshot, scan))
        {
            return Outcome(RunResult.Unsupported,
                "The snapshot has no such fact, and it cannot establish absence: the scan was limited to some paths or did not read everything." + before, []);
        }

        limitations.Add("Absence is established for the files the scan read at this commit, under the extraction rules of this snapshot.");
        return Outcome(RunResult.Failed, "A complete scan of the whole repository at this commit found no such fact." + before, []);
    }

    /// <summary>
    /// SnapshotCoverage: whether a snapshot can establish that a fact is absent from the
    /// repository. The import module has no coverage record of its own yet, so the rule is
    /// decided here and nowhere else: the scan is complete and was not scoped. A scan with
    /// include or exclude paths, or one that reached a bound, did not look everywhere, so a
    /// missing fact proves nothing.
    /// </summary>
    internal static bool EstablishesAbsence(SnapshotEntity snapshot, ScanEntity scan) =>
        snapshot.IsComplete && scan is { IsComplete: true, Partial: false, IncludePaths.Length: 0, ExcludePaths.Length: 0 };

    /// <summary>Where the newest passed run of this obligation found the pattern. An obligation ID is unique inside its promise only.</summary>
    private static async Task<string> PreviouslyFoundAsync(PromisesDbContext promisesDb, Guid promiseId, string obligationId, CancellationToken ct)
    {
        var last = await promisesDb.Runs.AsNoTracking()
            .Where(r => r.PromiseId == promiseId && r.ObligationId == obligationId && r.Result == RunResult.Passed)
            .OrderByDescending(r => r.Version)
            .Select(r => r.Document)
            .FirstOrDefaultAsync(ct);
        return last is not null && PromiseJson.Read<VerificationRun>(last).Evidence.FirstOrDefault(e => e.Pointer is not null) is { } found
            ? $" It was found before at {found.Pointer}."
            : "";
    }

    private static Dictionary<string, string> Attributes(string json)
    {
        var attributes = new Dictionary<string, string>(StringComparer.Ordinal);
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            return attributes;
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.String)
                attributes[property.Name] = property.Value.GetString()!;
        }
        return attributes;
    }

    /// <summary><c>github.com/acme/orders@&lt;sha&gt;:src/Api/Program.cs:41-44</c></summary>
    private static string Pointer(string repository, string commitSha, EvidenceRef evidence) => string.Create(
        CultureInfo.InvariantCulture, $"{repository}@{evidence.CommitSha ?? commitSha}:{evidence.Path}:{evidence.StartLine}-{evidence.EndLine}");

    /// <summary>The page that shows the lines at the commit.</summary>
    private static string Url(string repository, string commitSha, EvidenceRef evidence)
    {
        var path = string.Join('/', evidence.Path.Split('/').Select(Uri.EscapeDataString));
        var lines = evidence.EndLine > evidence.StartLine
            ? string.Create(CultureInfo.InvariantCulture, $"L{evidence.StartLine}-L{evidence.EndLine}")
            : string.Create(CultureInfo.InvariantCulture, $"L{evidence.StartLine}");
        return $"https://{repository}/blob/{evidence.CommitSha ?? commitSha}/{path}#{lines}";
    }
}
