using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Vellum.Modules.Import;
using Vellum.Modules.Import.Extraction;
using Vellum.Modules.Import.Proposals;
using Vellum.Modules.Import.Scans;
using Vellum.Shared;

namespace Vellum.Modules.Promises.Checks;

/// <summary>
/// The <c>source-pattern-check</c>: looks at one import snapshot (one scan of one commit) for
/// a fact whose <c>pattern</c> attribute has the value the obligation names. A fact is found
/// by that attribute alone: fact keys and fact kinds are the importer's own and are not read
/// here, except that a service is matched by the key an accepted import linked to the element.
/// <para>
/// Finding the pattern is a statement about the commit of the scan. Not finding it is a
/// statement only when the scan could have found it: its coverage (the import module's
/// <see cref="ScanCoverage"/>) includes every place the pattern was found before and every
/// place it would be found: the project directory of the service, or the whole repository
/// when the obligation names no service. Else the result is <c>unsupported</c> with the reason.
/// </para>
/// <para>
/// A comparison scan (a pull request, another ref or a bare commit) is checked on its own
/// snapshot the same way, with two more conditions: no limit cut it short, and it read every
/// file where the snapshot that the model follows declares the pattern. Its result is for
/// that commit only: the run records the scan and the commit, and it is never the newest
/// result of the obligation for the promise (<see cref="PromiseState.CountsAsLatest"/>).
/// </para>
/// </summary>
public static class SourcePatternCheck
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private const string PatternAttribute = "pattern";
    private const string ServiceAttribute = "service";
    private const string ProjectAttribute = "project";
    private const string ConditionalAttribute = "conditional";
    private const int MaxEvidence = 50;
    public const string Covered = "covered";

    /// <summary>The JSON that a run document against a comparison scan contains.</summary>
    internal static readonly string ComparisonRun = JsonSerializer.Serialize(new { inputs = new { scanKind = ScanKind.Comparison } });

    /// <summary>The scan to look at, or why the request cannot name it.</summary>
    public static async Task<(ScanEntity? Scan, string? Refusal)> FindScanAsync(
        AppDbContext db, Guid projectId, SourcePatternRecipe recipe, Guid scanId, CancellationToken ct)
    {
        var scan = await db.Scans.AsNoTracking().FirstOrDefaultAsync(s => s.Id == scanId && s.ProjectId == projectId, ct);
        if (scan is null)
            return (null, "No scan with this ID exists in the project");
        return scan.Repository == recipe.Repository
            ? (scan, null)
            : (null, $"The scan is of {scan.Repository}; the obligation names {recipe.Repository}");
    }

    public static CheckOutcome NoSnapshotGiven(SourcePatternRecipe recipe, string obligationHash) => new(
        RunResult.NotRun, "source", $"Pattern '{recipe.Pattern}' in {recipe.Repository}.",
        "No import snapshot was given. Name a completed scan of the repository in scanId.", [], [],
        Inputs: new RunInputs(obligationHash));

    /// <summary>
    /// What the checks of one call read of one scan: its snapshot, the tracked ref, the facts of
    /// the snapshot, the state of each file and the pattern facts of the followed snapshot. A
    /// call that runs many checks against one scan loads each of them once.
    /// </summary>
    internal sealed class ScanInputs
    {
        internal required ScanEntity Scan { get; init; }
        internal SnapshotEntity? Snapshot { get; init; }
        internal TrackedRefEntity? Tracked { get; init; }
        internal IReadOnlyList<(string FactKey, Dictionary<string, string> Attributes, string Evidence)> Facts { get; init; } = [];
        internal Dictionary<string, string?>? Files { get; set; }
        internal IReadOnlyList<(Dictionary<string, string> Attributes, string Evidence)>? FollowedPatterns { get; set; }
        internal Dictionary<Guid, List<FactLinkEntity>> ServiceLinks { get; } = [];

        /// <summary>True for a completed scan that is a comparison now: a run against it is for that scan only.</summary>
        internal bool IsComparison => Snapshot is not null && !ScanKindInfo.Of(Scan, Tracked).Tracking;

        public static async Task<ScanInputs> LoadAsync(AppDbContext db, Guid projectId, ScanEntity scan, CancellationToken ct)
        {
            var snapshot = await db.ImportSnapshots.AsNoTracking().FirstOrDefaultAsync(s => s.ScanId == scan.Id, ct);
            if (snapshot is null)
                return new ScanInputs { Scan = scan };
            var tracked = await db.TrackedRefs.AsNoTracking()
                .FirstOrDefaultAsync(t => t.ProjectId == projectId && t.Host == snapshot.Host && t.Repository == snapshot.Repository, ct);
            var rows = await db.SnapshotFacts.AsNoTracking()
                .Where(f => f.ScanId == snapshot.ScanId)
                .OrderBy(f => f.Seq)
                .Select(f => new { f.FactKey, f.Attributes, f.Evidence })
                .ToListAsync(ct);
            return new ScanInputs
            {
                Scan = scan, Snapshot = snapshot, Tracked = tracked,
                Facts = [.. rows.Select(r => (r.FactKey, Attributes(r.Attributes), r.Evidence))],
            };
        }
    }

    public static async Task<CheckOutcome> RunAsync(
        AppDbContext db, Guid projectId, Guid promiseId, string obligationId, string obligationHash,
        SourcePatternRecipe recipe, ScanEntity scan, CancellationToken ct) =>
        await RunAsync(db, projectId, promiseId, obligationId, obligationHash, recipe,
            await ScanInputs.LoadAsync(db, projectId, scan, ct), ct);

    internal static async Task<CheckOutcome> RunAsync(
        AppDbContext db, Guid projectId, Guid promiseId, string obligationId, string obligationHash,
        SourcePatternRecipe recipe, ScanInputs inputs, CancellationToken ct)
    {
        var scan = inputs.Scan;
        var snapshot = inputs.Snapshot;
        if (snapshot is null)
        {
            return new CheckOutcome(
                RunResult.NotRun, "source", $"Pattern '{recipe.Pattern}' in {recipe.Repository}, scan {scan.Id}.",
                $"The scan has no snapshot (status '{scan.Status}'). Only a completed scan has one.", [], [], ScanId: scan.Id,
                Inputs: new RunInputs(obligationHash, ScanId: scan.Id));
        }

        var repository = $"{snapshot.Host}/{snapshot.Repository}";
        var environment = $"source@{snapshot.CommitSha}";
        var limitations = new List<string>
        {
            "A source fact shows that the code declares the pattern at the cited lines of this commit. It does not show that the pattern works at run time.",
        };

        var tracked = inputs.Tracked;
        var kind = ScanKindInfo.Of(scan, tracked);
        var refText = scan.ResolvedRef is { Length: > 0 } resolved ? $"ref '{resolved}'" : "no ref (a bare commit)";

        List<FactLinkEntity>? serviceLinks = null;
        var serviceText = "";
        if (recipe.Service is { } service)
        {
            serviceText = $" for service element {service}";
            if (!inputs.ServiceLinks.TryGetValue(service, out serviceLinks))
            {
                inputs.ServiceLinks[service] = serviceLinks = await db.FactLinks.AsNoTracking()
                    .Where(l => l.ProjectId == projectId && l.Host == snapshot.Host && l.Repository == snapshot.Repository && l.EntityId == service)
                    .OrderBy(l => l.Id)
                    .ToListAsync(ct);
            }
        }
        var serviceKeys = serviceLinks?.Select(l => l.FactKey).ToHashSet(StringComparer.Ordinal);

        var scope = $"Pattern '{recipe.Pattern}'{serviceText} in {repository} at {refText}, commit {snapshot.CommitSha} (scan {snapshot.ScanId}, a {kind.Kind} scan).";
        var coverageText = (string?)null;
        CheckOutcome Outcome(string result, string detail, IReadOnlyList<EvidenceItem> evidence) =>
            new(result, environment, scope, CheckOutcome.Bounded(detail), evidence, limitations, CommitSha: snapshot.CommitSha, ScanId: snapshot.ScanId,
                Inputs: new RunInputs(
                    obligationHash, Repository: repository, ScanId: snapshot.ScanId, CommitSha: snapshot.CommitSha,
                    Ref: scan.ResolvedRef, ScanKind: kind.Kind, Coverage: coverageText));

        if (serviceKeys is { Count: 0 })
        {
            return Outcome(RunResult.Unsupported,
                "No accepted import links the service element to a service of this repository, so a pattern fact cannot be attributed to it.", []);
        }

        var evidence = new List<EvidenceItem>();
        var conditions = new List<string>();
        var unconditional = 0;
        var projects = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var fact in inputs.Facts)
        {
            var attributes = fact.Attributes;
            var ofService = serviceKeys is null || (attributes.TryGetValue(ServiceAttribute, out var owner) && serviceKeys.Contains(owner));
            if (ofService && serviceKeys is not null && attributes.TryGetValue(ProjectAttribute, out var project))
                projects.Add(project);
            if (!ofService || attributes.GetValueOrDefault(PatternAttribute) != recipe.Pattern)
                continue;

            if (attributes.TryGetValue(ConditionalAttribute, out var condition))
                conditions.Add(condition);
            else
                unconditional++;

            // A run keeps at most MaxEvidence pointers; they are enough to find the declaration.
            foreach (var reference in (JsonSerializer.Deserialize<List<EvidenceRef>>(fact.Evidence, Json) ?? []).Take(MaxEvidence - evidence.Count))
                evidence.Add(new EvidenceItem(EvidenceKind.Source, Pointer: Pointer(repository, snapshot.CommitSha, reference), Url: Url(repository, snapshot.CommitSha, reference)));
        }

        var found = unconditional + conditions.Count;
        if (conditions.Count > 0)
        {
            limitations.Add(string.Create(CultureInfo.InvariantCulture,
                $"{conditions.Count} of {found} declaration(s) are declared under #if {string.Join(" / #if ", conditions.Distinct(StringComparer.Ordinal))}. The scan does not evaluate the condition, so they are in a build only when it holds."));
        }

        if (unconditional > 0 || (found > 0 && !recipe.NeedsUnconditional))
        {
            return Outcome(RunResult.Passed,
                string.Create(CultureInfo.InvariantCulture, $"The pattern is declared at {evidence.Count} place(s) of this commit.")
                + (unconditional == 0 ? " Every declaration is under a compile-time condition, which this obligation accepts." : ""),
                evidence);
        }

        // From here the result needs absence: of the pattern, or of an unconditional declaration of it.
        var (previousText, previousPaths) = await PreviouslyFoundAsync(db, promiseId, obligationId, ct);
        var missing = found == 0
            ? "The snapshot has no such fact"
            : $"The snapshot declares the pattern only under #if {string.Join(" / #if ", conditions.Distinct(StringComparer.Ordinal))}, and the obligation requires an unconditional declaration";

        if (!kind.Tracking)
        {
            limitations.Add($"This is a comparison scan ({(kind.Reason switch { "bare-commit" => "a commit and no ref", "pull-request" => "the head commit of a pull request", _ => $"another ref than the tracked ref '{tracked?.RefName}'" })}). The result is for this commit only. It does not change the result of the promise against the ref that the model follows.");
            // Where the snapshot that the model follows declares the pattern: a scan that did
            // not read those files cannot say the pattern is gone from them.
            if (tracked?.ScanId is { } followed && followed != scan.Id)
                previousPaths = [.. previousPaths.Concat(await FollowedPathsAsync(db, inputs, followed, recipe.Pattern, serviceKeys, ct)).Distinct(StringComparer.Ordinal)];
            if (!scan.IsComplete)
            {
                coverageText = CoverageGap.Limit;
                return Outcome(RunResult.Unsupported,
                    $"{missing}, and this scan cannot establish absence: a limit stopped it before every file was read. The scan did not look everywhere the pattern would be.{previousText}",
                    found == 0 ? [] : evidence);
            }
        }

        var gap = await CoverageGapAsync(db, inputs, serviceLinks, projects, previousPaths, ct);
        if (gap is not null)
        {
            coverageText = gap.Value.Class;
            return Outcome(RunResult.Unsupported,
                $"{missing}, and this scan cannot establish absence: {gap.Value.Sentence}{previousText}", found == 0 ? [] : evidence);
        }

        // The scan looked everywhere it could. A service whose fact key changed at this commit (its directory moved, or a second
        // project file in its directory gave it the key dir#path) has its facts under a key
        // that the accepted link does not name. Its pattern facts are then not counted as its
        // own, so the snapshot would seem to lack them: absence proves nothing here.
        if (serviceKeys is not null && !inputs.Facts.Any(f => serviceKeys.Contains(f.FactKey)))
        {
            coverageText = "service-not-found";
            return Outcome(RunResult.Unsupported,
                $"{missing}, and this scan cannot establish absence: the service is not found under its linked key at this commit ({string.Join(", ", serviceKeys.Order(StringComparer.Ordinal))}). Its directory or its project files may have changed. Accept the scan of the ref that the model follows to link the service again.{previousText}",
                found == 0 ? [] : evidence);
        }

        coverageText = Covered;
        limitations.Add("Absence is established for the files the scan read at this commit, under the extraction rules of this snapshot.");
        return Outcome(RunResult.Failed,
            (found == 0
                ? $"A {kind.Kind} scan that covers {(serviceKeys is null ? "the whole repository" : "the project directory of the service")} at this commit found no such fact."
                : $"{missing}. A {kind.Kind} scan that covers {(serviceKeys is null ? "the whole repository" : "the project directory of the service")} found no unconditional declaration.")
            + previousText,
            found == 0 ? [] : evidence);
    }

    /// <summary>The files where the snapshot of <paramref name="scanId"/> declares <paramref name="pattern"/>, in the service when keys are given.</summary>
    private static async Task<IReadOnlyList<string>> FollowedPathsAsync(
        AppDbContext db, ScanInputs inputs, Guid scanId, string pattern, HashSet<string>? serviceKeys, CancellationToken ct)
    {
        inputs.FollowedPatterns ??= [.. (await ChangeImpact.PatternRows(db, scanId).ToListAsync(ct)).Select(r => (Attributes(r.Attributes), r.Evidence))];
        var paths = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var (attributes, evidence) in inputs.FollowedPatterns)
        {
            if (attributes.GetValueOrDefault(PatternAttribute) != pattern
                || (serviceKeys is not null && !(attributes.TryGetValue(ServiceAttribute, out var owner) && serviceKeys.Contains(owner))))
                continue;
            foreach (var reference in JsonSerializer.Deserialize<List<EvidenceRef>>(evidence, Json) ?? [])
                paths.Add(reference.Path);
        }
        return [.. paths];
    }

    /// <summary>
    /// Null when the scan looked everywhere the pattern would be: the whole repository, or
    /// the project directories of the service, and every path where it was found before.
    /// Else the class of the first reason it did not, with a sentence that names the place.
    /// </summary>
    private static async Task<(string Class, string Sentence)?> CoverageGapAsync(
        AppDbContext db, ScanInputs inputs, List<FactLinkEntity>? serviceLinks,
        IReadOnlyCollection<string> projects, IReadOnlyList<string> previousPaths, CancellationToken ct)
    {
        var scan = inputs.Scan;
        if (scan.ExtractionGap is { } global)
            return (global, $"the extraction left facts out ({global}).");
        if (!scan.TreeListed)
            return (CoverageGap.Failed, "the file list of the commit was not read in full.");

        if (inputs.Files is null)
        {
            var rows = await db.ScanFiles.AsNoTracking().Where(f => f.ScanId == scan.Id)
                .Select(f => new { f.Path, f.State, f.Reason }).ToListAsync(ct);
            inputs.Files = rows.ToDictionary(
                f => f.Path, f => f.State == ScanFileEntity.Read ? null : f.Reason ?? CoverageGap.ParseFailure, StringComparer.Ordinal);
        }
        var files = inputs.Files;
        var coverage = new ScanCoverage(
            files, ScanScope.AbsentGap(ScanLimits.FromJson(scan.Limits), scan.IncludePaths, scan.ExcludePaths), scan.ExtractionGap);

        // Where the pattern was found before: the scan must have looked at exactly those files.
        foreach (var path in previousPaths)
        {
            if (coverage.GapFor([new EvidenceRef(path, 0, 0, "", 0)]) is { } before)
                return (before, $"it did not look at {path}, where the pattern was found before ({before}).");
        }

        // Where the pattern would be found.
        IReadOnlyList<string> directories;
        if (serviceLinks is null)
            directories = [""];
        else
        {
            // The project file of the service in this snapshot; when the service has no fact
            // in it, the files that the accepted link cites.
            var places = projects.Count > 0
                ? projects.ToList()
                : serviceLinks.SelectMany(l => ProposalJson.ReadEvidence(l.Evidence)).Select(e => e.Path).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
            if (places.Count == 0)
                return (CoverageGap.Failed, "the accepted link of the service cites no file, so there is no place to look.");
            foreach (var place in places)
            {
                if (coverage.GapFor([new EvidenceRef(place, 0, 0, "", 0)]) is { } around)
                    return (around, $"it did not look at {place} of the service ({around}).");
            }
            directories = [.. places.Select(p => files.ContainsKey(p) || p.Contains('.', StringComparison.Ordinal) ? DirectoryOf(p) : p).Distinct(StringComparer.Ordinal)];
        }

        foreach (var directory in directories)
        {
            var prefix = directory.Length == 0 ? "" : directory + "/";
            foreach (var (path, reason) in files.Where(f => f.Value is not null && f.Key.StartsWith(prefix, StringComparison.Ordinal)).OrderBy(f => f.Key, StringComparer.Ordinal))
            {
                // A file that no extractor reads under any rules hides nothing.
                if (PathFilter.SkipReason(path, out _) is not null || !PathFilter.IsRelevant(path[(path.LastIndexOf('/') + 1)..]))
                    continue;
                var gapClass = CoverageGap.Of(reason);
                return (gapClass, $"it did not read {path} ({gapClass}: {reason}), which is in {(directory.Length == 0 ? "the repository" : $"the directory {directory}")} where the pattern would be.");
            }
        }
        return null;
    }

    private static string DirectoryOf(string path)
    {
        var slash = path.LastIndexOf('/');
        return slash < 0 ? "" : path[..slash];
    }

    /// <summary>Where the newest passed run of this obligation found the pattern. An obligation ID is unique inside its promise only.</summary>
    private static async Task<(string Text, IReadOnlyList<string> Paths)> PreviouslyFoundAsync(
        AppDbContext db, Guid promiseId, string obligationId, CancellationToken ct)
    {
        var last = await db.PromiseRuns.AsNoTracking()
            // A run against a comparison scan read another line of history: where it found the pattern says nothing here.
            .Where(r => r.PromiseId == promiseId && r.ObligationId == obligationId && r.Result == RunResult.Passed
                && !EF.Functions.JsonContains(r.Document, ComparisonRun))
            .OrderByDescending(r => r.Version)
            .Select(r => r.Document)
            .FirstOrDefaultAsync(ct);
        if (last is null)
            return ("", []);

        var pointers = PromiseJson.Read<VerificationRun>(last).Evidence.Select(e => e.Pointer).OfType<string>().ToList();
        return pointers.Count == 0
            ? ("", [])
            : ($" It was found before at {pointers[0]}.", [.. pointers.Select(PathOf).OfType<string>().Distinct(StringComparer.Ordinal)]);
    }

    /// <summary>The path of a pointer <c>host/owner/name@sha:path:start-end</c>. A path may hold a colon; a SHA and a line range do not.</summary>
    private static string? PathOf(string pointer)
    {
        var at = pointer.IndexOf('@', StringComparison.Ordinal);
        var first = at < 0 ? -1 : pointer.IndexOf(':', at);
        var last = pointer.LastIndexOf(':');
        return first >= 0 && last > first ? pointer[(first + 1)..last] : null;
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
