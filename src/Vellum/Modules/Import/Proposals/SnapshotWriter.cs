using Deedbox;
using Microsoft.EntityFrameworkCore;
using Vellum.Modules.Import.Extraction;
using Vellum.Modules.Import.Scans;
using Vellum.Modules.Modelling.Model;
using Vellum.Modules.Schemas;
using Vellum.Modules.Workspaces;
using Vellum.Shared;

namespace Vellum.Modules.Import.Proposals;

internal enum SnapshotOutcome
{
    Written,
    /// <summary>Another worker has the scan. Nothing was written.</summary>
    LeaseLost,
    ProjectGone,
}

/// <summary>
/// The end of a scan: the snapshot, the proposals, the stale marks and the status of the scan,
/// in one transaction. The model stream is locked while the proposals are computed, so they
/// describe one exact version of the model.
/// <para>
/// The only model events written here are stale marks: set on an accepted,
/// <c>observed-in-source</c> fact whose evidence a complete scan did not find, and cleared on
/// a marked fact whose evidence this scan found. No value, status or acceptance stamp of any
/// fact changes, and no proposal is accepted.
/// </para>
/// </summary>
internal static class SnapshotWriter
{
    public static Task<SnapshotOutcome> WriteAsync(
        IServiceProvider scanServices, ScanEntity scan, string workerId, ExtractionResult result,
        bool isComplete, string notes, DateTimeOffset now, CancellationToken ct) =>
        SharedTransaction.RunAsync(scanServices, async services =>
        {
            var project = await services.GetRequiredService<WorkspacesDbContext>().Projects.AsNoTracking()
                .FirstOrDefaultAsync(p => p.Id == scan.ProjectId, ct);
            if (project is null)
                return SnapshotOutcome.ProjectGone;

            var db = services.GetRequiredService<ImportDbContext>();
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var store = services.GetRequiredService<IEventStore>().ForProject(scan.ProjectId).UseDbContext(db);
            var commitSha = scan.CommitSha!;

            // The stream lock first. Decisions take the same lock, so no proposal is accepted
            // while this scan reads the links and the model.
            var locked = await store.Execute<ModelState>(StreamId.From(project.StreamId), _ => [], ct);
            var model = locked.State;

            var schemas = await services.GetRequiredService<SchemasDbContext>().Schemas.AsNoTracking()
                .Where(s => s.ProjectId == scan.ProjectId)
                .Select(s => new SchemaRow(s.Id, s.Name, s.Content))
                .ToListAsync(ct);

            var projectLinks = await db.FactLinks.AsNoTracking().Where(l => l.ProjectId == scan.ProjectId).ToListAsync(ct);
            var links = projectLinks
                .Where(l => l.Host == scan.Host && l.Repository == scan.Repository)
                .ToDictionary(l => l.FactKey, l => new LinkInfo(
                    l.FactKey, l.EntityKind, l.EntityId, l.CommitSha,
                    ProposalJson.ReadAttributes(l.Attributes), ProposalJson.ReadValues(l.Accepted),
                    ProposalJson.ReadEvidence(l.Evidence)), StringComparer.Ordinal);
            var rejections = (await db.Rejections.AsNoTracking()
                    .Where(r => r.ProjectId == scan.ProjectId && r.Host == scan.Host && r.Repository == scan.Repository)
                    .Select(r => new { r.FactKey, r.Fingerprint })
                    .ToListAsync(ct))
                .Select(r => (r.FactKey, r.Fingerprint)).ToHashSet();

            var plan = ProposalPlanner.Compute(new PlanInput(
                $"{scan.Host}/{scan.Repository}", commitSha, isComplete, result.Facts,
                ModelIndex.From(model, schemas), links,
                projectLinks.Select(l => l.EntityId).ToHashSet(), rejections), Guid.NewGuid);

            db.Snapshots.Add(new SnapshotEntity
            {
                ScanId = scan.Id, ProjectId = scan.ProjectId, Host = scan.Host, Repository = scan.Repository,
                CommitSha = commitSha, IsComplete = isComplete,
                RuleVersions = ProposalJson.Write(EvidenceExtractor.Rules.Select(r => new RuleVersion(r.Id, r.Version))),
                Limits = scan.Limits, FactCount = plan.Facts.Count, CreatedAt = now,
            });

            var seq = 0;
            db.SnapshotFacts.AddRange(plan.Facts.Select(f => new SnapshotFactEntity
            {
                ScanId = scan.Id, Seq = seq++, Kind = f.Fact.Kind.ToString(), FactKey = f.Fact.Key, Name = f.Fact.Name,
                Attributes = ProposalJson.Write(f.Fact.Attributes),
                Evidence = ProposalJson.Write(f.Fact.Evidence.Select(FactMapping.Ref)),
                Disposition = f.Disposition, Reason = f.Reason,
            }));

            seq = 0;
            db.Proposals.AddRange(plan.Proposals.Select(p => new ProposalEntity
            {
                Id = p.Id, ScanId = scan.Id, ProjectId = scan.ProjectId, Seq = seq++,
                Action = p.Action, EntityKind = p.EntityKind, EntityId = p.EntityId, FactKey = p.FactKey, Name = p.Name,
                Proposed = p.Proposed?.ToJsonString(), Current = p.Current?.ToJsonString(),
                Changes = ProposalJson.Write(p.Changes), Evidence = ProposalJson.Write(p.Evidence),
                Attributes = ProposalJson.Write(p.Attributes), DependsOn = [.. p.DependsOn],
                Status = ProposalStatus.Pending, Partial = !isComplete, Fingerprint = p.Fingerprint,
            }));

            // Proposals of an earlier scan of this repository describe an older commit.
            var earlier = db.Scans.Where(s =>
                s.ProjectId == scan.ProjectId && s.Host == scan.Host && s.Repository == scan.Repository && s.Id != scan.Id);
            await db.Proposals
                .Where(p => p.Status == ProposalStatus.Pending && earlier.Any(s => s.Id == p.ScanId))
                .ExecuteUpdateAsync(set => set.SetProperty(p => p.Status, ProposalStatus.Superseded), ct);

            // The status of the scan is written under the condition that this worker still
            // has it. Without the row, nothing above is committed.
            var completed = await db.Scans
                .Where(s => s.Id == scan.Id && s.LeaseOwner == workerId && s.Status == ScanStatus.Running)
                .ExecuteUpdateAsync(set => set
                    .SetProperty(s => s.Status, ScanStatus.Completed)
                    .SetProperty(s => s.IsComplete, isComplete)
                    .SetProperty(s => s.Partial, !isComplete)
                    .SetProperty(s => s.Notes, notes)
                    .SetProperty(s => s.ErrorCode, (string?)null)
                    .SetProperty(s => s.ErrorMessage, (string?)null)
                    .SetProperty(s => s.CompletedAt, now)
                    .SetProperty(s => s.UpdatedAt, now)
                    .SetProperty(s => s.LeaseOwner, (string?)null)
                    .SetProperty(s => s.LeaseExpiresAt, (DateTimeOffset?)null), ct);
            if (completed == 0)
                return SnapshotOutcome.LeaseLost;

            var mark = new StaleMark(commitSha, StaleDecider.EvidenceNotFound);
            var events = new List<object>();
            foreach (var id in plan.MarkStale)
                events.AddRange(StaleDecider.Mark(model, id, mark));
            foreach (var id in plan.ClearStale)
                events.AddRange(StaleDecider.Mark(model, id, null));

            // One append, last: it holds the store-wide counter until the commit.
            if (events.Count > 0)
                await store.Append(StreamId.From(project.StreamId), ExpectedVersion.Exact(locked.Version), events, ct);

            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return SnapshotOutcome.Written;
        }, ct);
}

/// <summary>A rule of an extractor and the version of it that made a snapshot.</summary>
public sealed record RuleVersion(string Rule, int Version);
