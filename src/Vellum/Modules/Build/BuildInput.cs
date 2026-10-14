using Deedbox;
using Microsoft.EntityFrameworkCore;
using Vellum.Modules.Import;
using Vellum.Modules.Import.Scans;
using Vellum.Modules.Modelling;
using Vellum.Modules.Promises;
using Vellum.Modules.Review;
using Vellum.Modules.Review.Decisions;
using Vellum.Modules.Schemas;
using Vellum.Modules.Trace.Engine;
using Vellum.Modules.Trace;
using Vellum.Modules.Workspaces;
using Vellum.Shared;

namespace Vellum.Modules.Build;

/// <summary>A stream an export read, at the version it read it.</summary>
public sealed record PinnedStream(Guid Id, long Version);

/// <summary>A verification run an export shows: the newest run of an obligation of a revision when the export was made.</summary>
public sealed record PinnedRun(Guid PromiseId, int PromiseRevision, string ObligationId, Guid RunId);

/// <summary>
/// What the import tables said about one repository when the export was made. A result about
/// source is current only for the commit the model follows, so that commit is an input.
/// </summary>
/// <param name="Repository"><c>host/owner/name</c>.</param>
/// <param name="TrackedRef">The ref the accepted facts follow, or null when nothing was accepted yet.</param>
/// <param name="TrackedCommit">The commit of the last accepted decision on that ref.</param>
/// <param name="TrackedScan">The scan of that decision.</param>
/// <param name="LatestCommit">The commit of the newest snapshot.</param>
/// <param name="LatestScan">The scan of the newest snapshot.</param>
public sealed record PinnedSource(
    string Repository, string? TrackedRef, string? TrackedCommit, Guid? TrackedScan, string? LatestCommit, Guid? LatestScan);

/// <summary>
/// Every input of an export, at the exact version that was read. The same pinned state gives
/// the same bundle, byte for byte, whatever happened in the project afterwards: the model is
/// folded to <paramref name="StreamVersion"/> (or is the named revision), every scenario,
/// schema and promise stream is folded to its version, the runs of a promise are the runs
/// that its stream holds at that version, and the project name and the followed commits are
/// kept here as values.
/// </summary>
/// <param name="Branch">The model stream the export was made from.</param>
/// <param name="RevisionId">The named revision, or null when the branch was pinned at its version at export.</param>
/// <param name="Decisions">
/// The decision streams, from the format <c>vellum-bundle/3</c> on. Null in a pinned state of
/// the format <c>vellum-bundle/2</c>, which was made before the review existed: such a state
/// is generated as it was then, with no review and no decision, and the member is left out of
/// its JSON so that its manifest keeps its bytes. Only <c>POST build/verify</c> generates such a
/// state; an export refuses it, because a state with no review has no review blocker.
/// </param>
/// <param name="Seal">
/// The mark that Vellum issued this state, from the format <c>vellum-bundle/4</c> on: see
/// <see cref="PinnedSeal"/>. Null in a state of an older format, and the member is then left
/// out of its JSON so that its manifest keeps its bytes. An export takes a pinned state from a
/// caller only when its seal verifies. The seal is no input of the files: it changes no file
/// hash and no bundle hash.
/// </param>
/// <param name="RevisionName">
/// The name of the named revision when the state was pinned. A revision can be renamed, and a
/// revision that nothing else names can be deleted: a state generated again names the
/// revision as it was pinned and reads its model from the branch at the pinned version. Null
/// for a branch pin, and in a state that was made before the member existed; the member is
/// then left out of its JSON so that its manifest keeps its bytes.
/// </param>
public sealed record PinnedState(
    Guid ProjectId, string ProjectName, Guid Branch, long StreamVersion, Guid? RevisionId,
    IReadOnlyList<PinnedStream> Scenarios,
    IReadOnlyList<PinnedStream> Schemas,
    IReadOnlyList<PinnedStream> Promises,
    IReadOnlyList<PinnedRun> Runs,
    IReadOnlyList<PinnedSource> Sources,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<PinnedStream>? Decisions = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    string? Seal = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    string? RevisionName = null);

/// <param name="StreamVersion">The version of the schema stream. <paramref name="Version"/> is the version of the content, which a rename does not change.</param>
public sealed record BuildSchema(Guid Id, string Name, string? Description, string Content, int Version, long StreamVersion = 0);

/// <summary>A pinned input that can no longer be read at its version: its stream was deleted, or never had that version.</summary>
public sealed class PinnedInputUnavailableException(IReadOnlyList<string> inputs)
    : Exception($"These pinned inputs cannot be read at their versions: {string.Join(", ", inputs)}")
{
    public IReadOnlyList<string> Inputs { get; } = inputs;
}

/// <summary>
/// Everything an export is made from. Nothing in it is read "as it is now" after the load:
/// <see cref="Pinned"/> names the version of every part, and <see cref="LoadAsync"/> with that
/// pinned state reads exactly those versions again. The same input gives the same files, byte
/// for byte, whatever the order of its lists.
/// </summary>
public sealed record BuildInput(
    Guid ProjectId,
    string ProjectName,
    ModelPin Pin,
    ModelState Model,
    IReadOnlyList<BuildSchema> Schemas,
    IReadOnlyList<Scenario> Scenarios,
    IReadOnlyList<PromiseDto> Promises,
    PinnedState Pinned,
    IReadOnlyDictionary<Guid, ScenarioSnapshot> ScenarioSnapshots)
{
    /// <summary>The decisions (ADRs) of the project at their pinned versions. The review reads which findings they settle.</summary>
    public IReadOnlyList<PinnedDecision> Decisions { get; init; } = [];

    /// <summary>
    /// An input whose parts the caller holds in memory and did not read from streams. Every
    /// part counts as version 1 of itself, and no run and no source is pinned.
    /// </summary>
    public BuildInput(
        Guid projectId, string projectName, ModelPin pin, ModelState model,
        IReadOnlyList<BuildSchema> schemas, IReadOnlyList<Scenario> scenarios, IReadOnlyList<PromiseDto> promises)
        : this(projectId, projectName, pin, model, schemas, scenarios, promises,
            new PinnedState(
                projectId, projectName, pin.StreamId, pin.Version, pin.RevisionId,
                [.. scenarios.Select(s => new PinnedStream(s.Id.Value, 1)).OrderBy(p => p.Id)],
                [.. schemas.Select(s => new PinnedStream(s.Id, 1)).OrderBy(p => p.Id)],
                [.. promises.Select(p => new PinnedStream(p.Id, 1)).OrderBy(p => p.Id)],
                [], [], []),
            scenarios.ToDictionary(s => s.Id.Value, s => ScenarioSnapshot.Of(s, 1)))
    {
    }

    /// <summary>
    /// Reads the input of a project for a pinned model. With <paramref name="pins"/> null the
    /// other inputs are read at their current versions, and <see cref="Pinned"/> records them.
    /// With <paramref name="pins"/> every input is read at exactly the version it names, from
    /// the stored events; nothing that was added, changed or deleted afterwards is read.
    /// </summary>
    /// <exception cref="BundleTooLargeException">The scenarios alone are larger than a bundle may be; reading stops there.</exception>
    /// <exception cref="PinnedInputUnavailableException">A pinned input cannot be read at its version.</exception>
    public static async Task<BuildInput> LoadAsync(
        IServiceProvider services, Guid projectId, PinnedModel pinned, BuildLimits limits, CancellationToken ct, PinnedState? pins = null)
    {
        var store = services.GetRequiredService<IEventStore>();
        var unavailable = new List<string>();

        var projectName = pins?.ProjectName ?? await services.GetRequiredService<AppDbContext>().Projects.AsNoTracking()
            .Where(p => p.Id == projectId).Select(p => p.Name).FirstAsync(ct);

        // Schemas: a stream each. The version of the stream is pinned, because a name and a
        // description are in the export and change without a new content version.
        var schemaPins = pins?.Schemas ?? [.. (await services.GetRequiredService<AppDbContext>().Schemas.AsNoTracking()
            .Where(s => s.ProjectId == projectId).Select(s => s.Id).ToListAsync(ct)).Select(id => new PinnedStream(id, 0))];
        var schemas = new List<BuildSchema>(schemaPins.Count);
        foreach (var pin in schemaPins.OrderBy(p => p.Id))
        {
            var folded = await StreamAt.FoldAsync<SchemaState>(store, pin.Id, pins is null ? null : pin.Version, ct);
            if (folded?.State.Schema is { } schema && schema.ProjectId == projectId)
                schemas.Add(new BuildSchema(schema.Id, schema.Name, schema.Description, schema.Content, schema.Version, folded.Version));
            else if (pins is not null)
                unavailable.Add($"schema {pin.Id} at version {pin.Version}");
        }

        var scenarioPins = pins?.Scenarios ?? [.. (await services.GetRequiredService<AppDbContext>().Scenarios.AsNoTracking()
            .Where(s => s.ProjectId == projectId).Select(s => s.Id).ToListAsync(ct)).Select(id => new PinnedStream(id, 0))];
        var scenarios = new Dictionary<Guid, ScenarioSnapshot>();
        long scenarioBytes = 0;
        foreach (var pin in scenarioPins.OrderBy(p => p.Id))
        {
            var folded = await StreamAt.FoldAsync<ScenarioState>(store, pin.Id, pins is null ? null : pin.Version, ct);
            if (folded?.State.DocumentIn(projectId) is not { } document)
            {
                if (pins is not null)
                    unavailable.Add($"scenario {pin.Id} at version {pin.Version}");
                continue;
            }
            scenarios[pin.Id] = ScenarioSnapshot.Of(document, folded.Version);

            // Every scenario becomes a file of at least this size, so a project whose scenarios
            // cannot fit is refused before the rest is read into memory.
            scenarioBytes += System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(document, BuildText.Json).Length;
            if (scenarioBytes > limits.MaxBundleBytes)
            {
                throw new BundleTooLargeException(
                    $"The scenarios of the project are larger than a bundle may be ({limits.MaxBundleBytes} bytes). Export a build brief for a part of the model.");
            }
        }

        var sources = pins?.Sources ?? await SourcesAsync(services.GetRequiredService<AppDbContext>(), projectId, ct);

        // Promises: the state and the runs of each, as its stream holds them at the pinned version.
        var promiseStates = new List<(Guid Id, PromiseState State, Dictionary<string, VerificationRun> Runs, long Version)>();
        if (pins is null)
        {
            var rows = await services.GetRequiredService<AppDbContext>().Promises.AsNoTracking()
                .Where(p => p.ProjectId == projectId)
                .OrderBy(p => p.Number).ThenBy(p => p.Id)
                .Select(p => new { p.Id, p.Document, p.LatestRuns, p.Version })
                .ToListAsync(ct);
            foreach (var row in rows)
            {
                // The row is the fold of the stream at its version; it commits with each event.
                // A row from before it recorded the version is folded from the stream here.
                if (row.Version > 0)
                {
                    promiseStates.Add((row.Id, PromiseJson.Read<PromiseState>(row.Document),
                        PromiseJson.Read<Dictionary<string, VerificationRun>>(row.LatestRuns), row.Version));
                }
                else if (await FoldPromiseAsync(store, row.Id, null, ct) is { } folded && folded.State.ExistsIn(projectId))
                    promiseStates.Add((row.Id, folded.State, folded.Runs, folded.Version));
            }
        }
        else
        {
            foreach (var pin in pins.Promises.OrderBy(p => p.Id))
            {
                if (await FoldPromiseAsync(store, pin.Id, pin.Version, ct) is { } folded && folded.State.ExistsIn(projectId))
                    promiseStates.Add((pin.Id, folded.State, folded.Runs, folded.Version));
                else
                    unavailable.Add($"promise {pin.Id} at version {pin.Version}");
            }
        }

        // Decisions: a stream each. A pinned state of the format before decisions names none and reads none.
        IReadOnlyList<PinnedDecision> decisions = pins is null
            ? await DecisionReads.CurrentAsync(services.GetRequiredService<AppDbContext>(), projectId, ct)
            : await DecisionReads.AtAsync(store, projectId, (pins.Decisions ?? []).Select(p => (p.Id, p.Version)), unavailable, ct);

        if (unavailable.Count > 0)
            throw new PinnedInputUnavailableException(unavailable);

        // Promises are read against the pinned inputs and nothing else: their links, and the
        // inputs that their runs are compared with, belong to the state the export names.
        var world = new PromiseWorld(
            pinned.Pin.StreamId, pinned.Pin.Version, pinned.State,
            scenarios.ToDictionary(s => s.Key, s => s.Value.Document.Name),
            scenarios,
            schemas.ToDictionary(s => s.Id, s => s.Version),
            sources.Where(s => s.TrackedRef is not null).ToDictionary(s => s.Repository, s => new TrackedCommit(s.TrackedRef!, s.TrackedCommit), StringComparer.Ordinal),
            sources.Where(s => s.LatestCommit is not null).ToDictionary(s => s.Repository, s => s.LatestCommit!, StringComparer.Ordinal),
            sources.Select(s => s.Repository).ToHashSet(StringComparer.Ordinal));
        var promises = promiseStates
            .OrderBy(p => p.State.Number).ThenBy(p => p.Id)
            .Select(p => PromiseViews.ToDto(p.Id, p.State, p.Runs, world))
            .ToList();

        var state = new PinnedState(
            projectId, projectName, pinned.Pin.StreamId, pinned.Pin.Version, pinned.Pin.RevisionId,
            [.. scenarios.OrderBy(s => s.Key).Select(s => new PinnedStream(s.Key, s.Value.Version))],
            [.. schemas.OrderBy(s => s.Id).Select(s => new PinnedStream(s.Id, s.StreamVersion))],
            [.. promiseStates.OrderBy(p => p.Id).Select(p => new PinnedStream(p.Id, p.Version))],
            [.. promiseStates.SelectMany(p => p.Runs.Values.Select(run => new PinnedRun(p.Id, run.PromiseRevision, run.ObligationId, run.Id)))
                .OrderBy(r => r.PromiseId).ThenBy(r => r.PromiseRevision).ThenBy(r => r.ObligationId, StringComparer.Ordinal)],
            [.. sources.OrderBy(s => s.Repository, StringComparer.Ordinal)],
            pins is not null && pins.Decisions is null ? null : [.. decisions.OrderBy(d => d.Id).Select(d => new PinnedStream(d.Id, d.Version))],
            RevisionName: pins is not null ? pins.RevisionName : pinned.Pin.RevisionName);

        return new BuildInput(
            projectId, projectName, pinned.Pin, pinned.State, schemas, [.. scenarios.Values.Select(s => s.Document)], promises, state, scenarios)
        {
            Decisions = decisions,
        };
    }

    /// <summary>A promise stream at a version: its state, and the newest run of each obligation of each revision among the runs it holds there.</summary>
    private static async Task<(PromiseState State, Dictionary<string, VerificationRun> Runs, long Version)?> FoldPromiseAsync(
        IEventStore store, Guid promiseId, long? version, CancellationToken ct)
    {
        if (await StreamAt.FoldAsync<PromiseState>(store, promiseId, version, ct, e => e is PromiseEvent.VerificationRunRecorded) is not { } folded
            || folded.State.Deleted || folded.State.Revisions.Count == 0)
            return null;

        var runs = new Dictionary<string, VerificationRun>(StringComparer.Ordinal);
        foreach (var recorded in folded.Kept.Cast<PromiseEvent.VerificationRunRecorded>().Where(r => PromiseState.CountsAsLatest(r.Run)))
            runs[PromiseState.RunKey(recorded.Run.PromiseRevision, recorded.Run.ObligationId)] = recorded.Run;
        return (folded.State, runs, folded.Version);
    }

    /// <summary>The tracked ref and the newest snapshot of each repository of the project, as they are now.</summary>
    private static async Task<IReadOnlyList<PinnedSource>> SourcesAsync(AppDbContext db, Guid projectId, CancellationToken ct)
    {
        var tracked = await db.TrackedRefs.AsNoTracking()
            .Where(t => t.ProjectId == projectId)
            .Select(t => new { t.Host, t.Repository, t.RefName, t.CommitSha, t.ScanId })
            .ToListAsync(ct);
        // A pull request or a bare commit is never the newest state of a repository.
        var snapshots = await ScanSnapshots.OfRefs(db, projectId).AsNoTracking()
            .OrderBy(s => s.CreatedAt).ThenBy(s => s.ScanId)
            .Select(s => new { s.Host, s.Repository, s.CommitSha, s.ScanId })
            .ToListAsync(ct);

        var sources = new SortedDictionary<string, PinnedSource>(StringComparer.Ordinal);
        foreach (var t in tracked)
        {
            var key = PromiseWorld.RepositoryKey(t.Host, t.Repository);
            sources[key] = new PinnedSource(key, t.RefName, t.CommitSha, t.ScanId, null, null);
        }
        foreach (var s in snapshots)
        {
            var key = PromiseWorld.RepositoryKey(s.Host, s.Repository);
            sources[key] = (sources.TryGetValue(key, out var known) ? known : new PinnedSource(key, null, null, null, null, null))
                with
            { LatestCommit = s.CommitSha, LatestScan = s.ScanId };
        }
        return [.. sources.Values];
    }
}
