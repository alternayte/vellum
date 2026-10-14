using Deedbox;
using Microsoft.EntityFrameworkCore;
using Vellum.Modules.Import.Extraction;
using Vellum.Modules.Import.Scans;
using Vellum.Modules.Modelling;
using Vellum.Modules.Schemas;
using Vellum.Modules.Workspaces;
using Vellum.Shared;

namespace Vellum.Modules.Import.Proposals;

internal enum SnapshotOutcome
{
    Written,
    /// <summary>Another worker has the scan, or the scan is no longer what the caller read. Nothing was written.</summary>
    LeaseLost,
    ProjectGone,
    /// <summary>The model or the accepted facts changed on every attempt to plan. Nothing was written.</summary>
    ModelBusy,
}

/// <summary>
/// How the plan of a scan is written: at the end of the scan's run, when a person makes its
/// ref the tracked ref, or when a person answered a "same as X?" question and the scan's
/// pending proposals are planned again.
/// </summary>
/// <param name="LeaseToken">The fencing token of the run that completes the scan. Null otherwise.</param>
/// <param name="TrackedBy">The person who called <c>track</c>. Null otherwise.</param>
/// <param name="Replan">True when only the pending proposals are planned again; decided ones stay as they are.</param>
internal sealed record WriteMode(long? LeaseToken, string? TrackedBy, bool Replan = false)
{
    public static WriteMode Complete(long leaseToken) => new(leaseToken, null);

    public static WriteMode Track(string userId) => new(null, userId);

    public static WriteMode PlanAgain() => new(null, null, Replan: true);
}

/// <summary>The identity of the Snapshot that a completed run writes: its ID and the number of files in its manifest.</summary>
internal sealed record ManifestSummary(string SnapshotId, int Files);

/// <param name="Coverage">Makes the coverage of the scan. A plan may narrow the one it is given, so each plan gets its own.</param>
/// <param name="ExtractionGap">See <see cref="ScanEntity.ExtractionGap"/>.</param>
internal sealed record SnapshotInput(
    IReadOnlyList<ExtractedFact> Facts, bool IsComplete, string? ExtractionGap, string Notes,
    Func<ScanCoverage> Coverage, int FilesExtracted, ManifestSummary? Manifest = null);

/// <summary>
/// The end of a scan: the snapshot, the proposals, the moved links, the stale marks and the
/// status of the scan, in one transaction.
/// <para>
/// The plan is not computed under the lock of the model stream. The model and the accepted
/// facts are read first, without a lock, and the plan is computed from that reading. Then
/// the transaction writes the rows that only this scan owns, takes the stream lock, and
/// checks that the model version and the accepted facts are still the ones the plan was
/// computed from. When they are not, nothing is written and the plan is computed again, at
/// most <see cref="MaxPlans"/> times. So a write to the project waits for a few statements
/// and never for the planning.
/// </para>
/// <para>
/// What a scan concludes depends on its kind (<see cref="ScanKindInfo"/>). A tracking scan
/// moves links to new fact keys, marks facts stale or clears the mark, and supersedes the
/// pending proposals of earlier scans of the same ref. A comparison scan writes its snapshot
/// and its proposals as a preview and nothing else.
/// </para>
/// <para>
/// The only model events written here are stale marks: set on an accepted,
/// <c>observed-in-source</c> fact whose evidence a scan looked for and did not find, and
/// cleared on a marked fact whose evidence this scan found. No value, status or acceptance
/// stamp of any fact changes, and no proposal is accepted.
/// </para>
/// </summary>
internal static class SnapshotWriter
{
    public const int MaxPlans = 3;

    private sealed record Prepared(
        Guid StreamId, long ModelVersion, ImportState State, ScanKindInfo Kind, Plan Plan, string RuleFingerprint);

    public static async Task<SnapshotOutcome> WriteAsync(
        IServiceProvider scanServices, ScanEntity scan, WriteMode mode, SnapshotInput input, DateTimeOffset now, CancellationToken ct)
    {
        for (var attempt = 1; attempt <= MaxPlans; attempt++)
        {
            if (await PrepareAsync(scanServices, scan, mode, input, ct) is not { } prepared)
                return SnapshotOutcome.ProjectGone;

            var outcome = await SharedTransaction.RunAsync(
                scanServices, services => CommitAsync(services, scan, mode, input, prepared, now, ct), ct);
            if (outcome is { } done)
                return done;
        }
        return SnapshotOutcome.ModelBusy;
    }

    /// <summary>The facts of a stored snapshot, for planning it again. A fact of a kind this build does not know is left as it is stored.</summary>
    public static async Task<IReadOnlyList<ExtractedFact>> StoredFactsAsync(AppDbContext db, Guid scanId, CancellationToken ct)
    {
        var rows = await db.SnapshotFacts.AsNoTracking().Where(f => f.ScanId == scanId).OrderBy(f => f.Seq).ToListAsync(ct);
        var facts = new List<ExtractedFact>(rows.Count);
        foreach (var row in rows)
        {
            facts.Add(new ExtractedFact(row.Kind, row.FactKey, row.Name, ProposalJson.ReadAttributes(row.Attributes),
                [.. ProposalJson.ReadEvidence(row.Evidence).Select(e => new SourceEvidence(e.Path, e.StartLine, e.EndLine, e.Rule, e.RuleVersion))]));
        }
        return facts;
    }

    /// <summary>Reads the model and the accepted facts without a lock and computes the plan. Null when the project is gone.</summary>
    private static async Task<Prepared?> PrepareAsync(
        IServiceProvider scanServices, ScanEntity scan, WriteMode mode, SnapshotInput input, CancellationToken ct)
    {
        await using var scope = scanServices.GetRequiredService<IServiceScopeFactory>().CreateAsyncScope();
        var services = scope.ServiceProvider;
        var ambient = scanServices.GetRequiredService<DeedboxContext>();
        services.GetRequiredService<DeedboxContext>().TenantId = ambient.TenantId;

        var project = await services.GetRequiredService<AppDbContext>().Projects.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == scan.ProjectId, ct);
        if (project is null)
            return null;

        var db = services.GetRequiredService<AppDbContext>();
        var store = services.GetRequiredService<IEventStore>().ForProject(scan.ProjectId);
        var (model, version) = await store.Load<ModelState>(StreamId.From(project.StreamId), ct);
        var schemas = await services.GetRequiredService<AppDbContext>().Schemas.AsNoTracking()
            .Where(s => s.ProjectId == scan.ProjectId)
            .Select(s => new SchemaRow(s.Id, s.Name, s.Content))
            .ToListAsync(ct);
        var state = await ImportState.LoadAsync(db, scan, ct);

        // A scan that a person makes the tracked one is planned as a tracking scan.
        var kind = mode.TrackedBy is not null ? new ScanKindInfo(ScanKind.Tracking, null) : ScanKindInfo.Of(scan, state.Tracked);
        var fingerprint = await db.ImportSnapshots.AsNoTracking().Where(s => s.ScanId == scan.Id).Select(s => s.RuleFingerprint).FirstOrDefaultAsync(ct)
            ?? RuleSet.CurrentFingerprint;

        var plan = await Task.Run(() => ProposalPlanner.Compute(new PlanInput(
            $"{scan.Host}/{scan.Repository}", scan.CommitSha!, fingerprint, input.Facts,
            ModelIndex.From(model, schemas), state.Links, state.TakenEntityIds, state.Rejections,
            input.Coverage(), scan.SourceId, state.OtherSources), Guid.NewGuid), ct);
        return new Prepared(project.StreamId, version, state, kind, plan, fingerprint);
    }

    /// <summary>Null when the model or the accepted facts are not the ones the plan was computed from.</summary>
    private static async Task<SnapshotOutcome?> CommitAsync(
        IServiceProvider services, ScanEntity scan, WriteMode mode, SnapshotInput input, Prepared prepared, DateTimeOffset now, CancellationToken ct)
    {
        var db = services.GetRequiredService<AppDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var store = services.GetRequiredService<IEventStore>().ForProject(scan.ProjectId).UseDbContext(db);
        var commitSha = scan.CommitSha!;
        var plan = prepared.Plan;
        var tracking = prepared.Kind.Tracking;

        // A re-plan takes the stream lock first: two re-plans of one scan, or a re-plan and a
        // decision, then take turns before any proposal row is touched.
        StreamExecuteResult? early = null;
        if (mode.Replan)
        {
            early = await LockAsync(store, prepared.StreamId, ct);
            if (await SupersededAsync(db, scan, ct))
                return SnapshotOutcome.LeaseLost;
        }

        // 1. The rows that only this scan owns, before any lock that another writer waits for.
        var seq = 0;
        if (mode.LeaseToken is not null)
        {
            // Without one from the run, the manifest is the files the scan rows say were read.
            var manifest = input.Manifest ?? await ManifestOfAsync(db, scan.Id, ct);
            db.ImportSnapshots.Add(new SnapshotEntity
            {
                ScanId = scan.Id, ProjectId = scan.ProjectId, SourceId = scan.SourceId, Host = scan.Host, Repository = scan.Repository,
                CommitSha = commitSha, SnapshotId = manifest.SnapshotId, ManifestFiles = manifest.Files, IsComplete = input.IsComplete,
                RuleVersions = ProposalJson.Write(RuleSet.Current), RuleFingerprint = RuleSet.CurrentFingerprint,
                Limits = scan.Limits, FactCount = plan.Facts.Count, CreatedAt = now,
            });
            var factSeq = 0;
            db.SnapshotFacts.AddRange(plan.Facts.Select(f => new SnapshotFactEntity
            {
                ScanId = scan.Id, Seq = factSeq++, Kind = f.Fact.Kind, FactKey = f.Fact.Key, Name = f.Fact.Name,
                Attributes = ProposalJson.Write(f.Fact.Attributes),
                Evidence = ProposalJson.Write(f.Fact.Evidence.Select(FactMapping.Ref)),
                Disposition = f.Disposition, Reason = f.Reason,
            }));
        }
        else
        {
            // The snapshot stays; what the importer did with each fact is written again.
            var rows = await db.SnapshotFacts.Where(f => f.ScanId == scan.Id).OrderBy(f => f.Seq).ToListAsync(ct);
            if (rows.Count != plan.Facts.Count)
                return SnapshotOutcome.LeaseLost;
            for (var i = 0; i < rows.Count; i++)
                (rows[i].Disposition, rows[i].Reason) = (plan.Facts[i].Disposition, plan.Facts[i].Reason);
            if (mode.Replan)
            {
                // Decided proposals are history and stay. A pending one that the new plan makes
                // again comes back with a new ID after them; the plan never repeats a decided one.
                await db.ImportProposals.Where(p => p.ScanId == scan.Id && p.Status == ProposalStatus.Pending).ExecuteDeleteAsync(ct);
                seq = await db.ImportProposals.Where(p => p.ScanId == scan.Id).Select(p => (int?)p.Seq).MaxAsync(ct) + 1 ?? 0;
            }
            else
                await db.ImportProposals.Where(p => p.ScanId == scan.Id).ExecuteDeleteAsync(ct);
        }

        db.ImportProposals.AddRange(plan.Proposals.Select(p => new ProposalEntity
        {
            Id = p.Id, ScanId = scan.Id, ProjectId = scan.ProjectId, Seq = seq++,
            Action = p.Action, EntityKind = Enum.Parse<StoredEntityKind>(p.EntityKind, ignoreCase: true), EntityId = p.EntityId,
            Subject = FactMapping.SubjectOf(p.FactKey), FactKey = FactMapping.StoredKey(p.FactKey), Name = p.Name,
            Proposed = p.Proposed?.ToJsonString(), Current = p.Current?.ToJsonString(),
            Changes = ProposalJson.Write(p.Changes), Evidence = ProposalJson.Write(p.Evidence),
            Attributes = ProposalJson.Write(p.Attributes), ConsumerKeys = ProposalJson.Write(p.ConsumerKeys),
            DependsOn = [.. p.DependsOn], OtherSources = ProposalJson.Write(p.OtherSources ?? []),
            Status = ProposalStatus.Pending, Partial = !input.IsComplete, Fingerprint = p.Fingerprint,
        }));
        await db.SaveChangesAsync(ct);

        // 2. The stream lock. Decisions take the same lock, so from here to the commit no
        // proposal is accepted and no link changes. The plan holds only when nothing did
        // since it was computed.
        var locked = early ?? await LockAsync(store, prepared.StreamId, ct);
        if (locked.Version != prepared.ModelVersion
            || (await ImportState.LoadAsync(db, scan, ct)).Stamp != prepared.State.Stamp)
            return null;

        // A re-plan answers questions of this scan only: it supersedes nothing, moves no link
        // and sets or clears no mark. The run that completed the scan did those.
        if (tracking && !mode.Replan)
        {
            // Proposals of an earlier scan of this ref describe an older commit.
            var earlier = db.Scans.Where(s =>
                s.ProjectId == scan.ProjectId && s.Host == scan.Host && s.Repository == scan.Repository && s.Id != scan.Id
                && (s.ResolvedRef == scan.ResolvedRef || (s.ResolvedRef == null && s.RequestedRef == null && scan.RequestedRef == null)));
            await db.ImportProposals
                .Where(p => p.Status == ProposalStatus.Pending && earlier.Any(s => s.Id == p.ScanId))
                .ExecuteUpdateAsync(set => set.SetProperty(p => p.Status, ProposalStatus.Superseded), ct);

            await MoveLinksAsync(db, plan.Relinked, prepared.RuleFingerprint, ct);
        }

        if (mode.TrackedBy is { } trackedBy)
        {
            await SetTrackedRefAsync(db, scan, trackedBy, now, ct);
            var audited = await db.Scans
                .Where(s => s.Id == scan.Id && s.Status == ScanStatus.Completed)
                .ExecuteUpdateAsync(set => set
                    .SetProperty(s => s.TrackedBy, trackedBy)
                    .SetProperty(s => s.TrackedAt, now)
                    .SetProperty(s => s.Coverage, plan.Coverage.ToJson())
                    .SetProperty(s => s.Relinked, plan.Relinked.Count)
                    .SetProperty(s => s.NeedsReview, plan.NeedsReview)
                    .SetProperty(s => s.UpdatedAt, now), ct);
            if (audited == 0)
                return SnapshotOutcome.LeaseLost;
        }
        else if (mode.Replan)
        {
            var planned = await db.Scans
                .Where(s => s.Id == scan.Id && s.Status == ScanStatus.Completed)
                .ExecuteUpdateAsync(set => set
                    .SetProperty(s => s.Coverage, plan.Coverage.ToJson())
                    .SetProperty(s => s.NeedsReview, plan.NeedsReview)
                    .SetProperty(s => s.UpdatedAt, now), ct);
            if (planned == 0)
                return SnapshotOutcome.LeaseLost;
        }
        else
        {
            var notes = plan.Refused.Count == 0 && plan.NeedsReview == 0
                ? input.Notes
                : ScanNotes.Write(ScanNotes.Read(input.Notes)
                    .Concat(plan.Refused.Select(f => ScanNotes.Of(CoverageGap.FactRefused, f.Evidence.Count > 0 && TextRule.Line("path", f.Evidence[0].Path, ScanFileEntity.MaxPath) is null ? f.Evidence[0].Path : "")))
                    .Concat(plan.NeedsReview > 0 ? [ScanNotes.Of("relink-needs-review", "")] : []));

            // The status of the scan is written under the condition that this run still
            // has it. Without the row, nothing above is committed.
            var token = mode.LeaseToken!.Value;
            StoredCoverageGap? extractionGap = EnumText.TryParse(input.ExtractionGap, out StoredCoverageGap gap) ? gap : null;
            var completed = await db.Scans
                .Where(s => s.Id == scan.Id && s.LeaseToken == token && s.Status == ScanStatus.Running)
                .ExecuteUpdateAsync(set => set
                    .SetProperty(s => s.Status, ScanStatus.Completed)
                    .SetProperty(s => s.IsComplete, input.IsComplete)
                    .SetProperty(s => s.Partial, !input.IsComplete)
                    .SetProperty(s => s.ExtractionGap, extractionGap)
                    .SetProperty(s => s.FilesExtracted, input.FilesExtracted)
                    .SetProperty(s => s.Coverage, plan.Coverage.ToJson())
                    .SetProperty(s => s.Relinked, tracking ? plan.Relinked.Count : 0)
                    .SetProperty(s => s.NeedsReview, plan.NeedsReview)
                    .SetProperty(s => s.Notes, notes)
                    .SetProperty(s => s.ErrorCode, (string?)null)
                    .SetProperty(s => s.ErrorMessage, (string?)null)
                    .SetProperty(s => s.RetryAt, (DateTimeOffset?)null)
                    .SetProperty(s => s.InternalFailures, 0)
                    .SetProperty(s => s.Crashes, 0)
                    .SetProperty(s => s.CompletedAt, now)
                    .SetProperty(s => s.UpdatedAt, now)
                    .SetProperty(s => s.LeaseOwner, (string?)null)
                    .SetProperty(s => s.LeaseExpiresAt, (DateTimeOffset?)null), ct);
            if (completed == 0)
                return SnapshotOutcome.LeaseLost;
            Vellum.Shared.Telemetry.VellumMetrics.ImportScanEnded(EnumText.Of(ScanStatus.Completed));
        }

        var events = new List<object>();
        if (tracking && !mode.Replan)
        {
            var mark = new StaleMark(commitSha, StaleDecider.EvidenceNotFound, scan.SourceId);
            foreach (var id in plan.MarkStale)
                events.AddRange(StaleDecider.Mark(locked.State, id, mark));
            foreach (var id in plan.ClearStale)
                events.AddRange(StaleDecider.Mark(locked.State, id, null));
        }

        // One append, last: it holds the store-wide counter until the commit.
        if (events.Count > 0)
            await store.Append(StreamId.From(prepared.StreamId), ExpectedVersion.Exact(locked.Version), events, ct);

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return SnapshotOutcome.Written;
    }

    private static async Task<ManifestSummary> ManifestOfAsync(AppDbContext db, Guid scanId, CancellationToken ct)
    {
        var files = await db.ScanFiles.AsNoTracking().Where(f => f.ScanId == scanId && f.State == ScanFileState.Read)
            .Select(f => new { f.Path, f.Size, f.BlobSha }).ToListAsync(ct);
        return new ManifestSummary(Sources.SnapshotManifest.Id(files.Select(f => new Sources.ManifestEntry(f.Path, f.Size, f.BlobSha))), files.Count);
    }

    private static async Task<StreamExecuteResult> LockAsync(IEventStore store, Guid streamId, CancellationToken ct)
    {
        var locked = await store.Execute<ModelState>(StreamId.From(streamId), _ => [], ct);
        return new StreamExecuteResult(locked.State, locked.Version);
    }

    /// <summary>
    /// True when a re-plan of the scan would describe an old commit: a proposal of it was
    /// superseded, or a newer completed scan of the same ref of its Source exists.
    /// </summary>
    private static async Task<bool> SupersededAsync(AppDbContext db, ScanEntity scan, CancellationToken ct) =>
        await db.ImportProposals.AnyAsync(p => p.ScanId == scan.Id && p.Status == ProposalStatus.Superseded, ct)
        || await db.Scans.AnyAsync(s => s.SourceId == scan.SourceId && s.Id != scan.Id && s.Status == ScanStatus.Completed
            && s.PullRequestNumber == null && s.ResolvedRef != null && s.ResolvedRef == scan.ResolvedRef
            && s.CompletedAt > scan.CompletedAt, ct);

    private sealed record StreamExecuteResult(ModelState State, long Version);

    /// <summary>
    /// Moves each link to the key its fact has under the current rules. The link keeps its
    /// entity, its accepted values, its evidence and its commit: nothing of the model and
    /// nothing a person accepted changes. A message's consumer keys move with the links of
    /// its consumers.
    /// </summary>
    private static async Task MoveLinksAsync(AppDbContext db, IReadOnlyList<Relink> relinked, string ruleFingerprint, CancellationToken ct)
    {
        if (relinked.Count == 0)
            return;

        var newKeys = relinked.ToDictionary(r => r.Link.FactKey, r => r.FactKey, StringComparer.Ordinal);
        var ids = relinked.Select(r => r.Link.LinkId).ToList();
        var rows = await db.FactLinks.Where(l => ids.Contains(l.Id)).ToDictionaryAsync(l => l.Id, ct);
        foreach (var relink in relinked)
        {
            if (!rows.TryGetValue(relink.Link.LinkId, out var row))
                continue;
            row.FactKey = FactMapping.StoredKey(relink.FactKey);
            row.FactKeyHash = KeyHash.Of(row.FactKey);
            row.RuleFingerprint = ruleFingerprint;
            row.ConsumerKeys = ProposalJson.Write(relink.Link.ConsumerKeys.Select(key => newKeys.GetValueOrDefault(key, key)));
        }
    }

    /// <summary>Makes the ref of <paramref name="scan"/> the tracked ref of its repository in its project.</summary>
    public static async Task SetTrackedRefAsync(AppDbContext db, ScanEntity scan, string userId, DateTimeOffset now, CancellationToken ct)
    {
        var tracked = await db.TrackedRefs.FirstOrDefaultAsync(
            t => t.ProjectId == scan.ProjectId && t.Host == scan.Host && t.Repository == scan.Repository, ct);
        if (tracked is null)
        {
            tracked = new TrackedRefEntity { ProjectId = scan.ProjectId, Host = scan.Host, Repository = scan.Repository };
            db.TrackedRefs.Add(tracked);
        }
        tracked.RepositoryId = scan.RepositoryId;
        tracked.RefName = scan.ResolvedRef ?? "";
        tracked.CommitSha = scan.CommitSha;
        tracked.ScanId = scan.Id;
        tracked.SetBy = userId;
        tracked.SetAt = now;
    }
}
