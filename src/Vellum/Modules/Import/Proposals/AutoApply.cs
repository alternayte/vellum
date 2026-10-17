using Deedbox;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Vellum.Modules.Identity.AgentTokens;
using Vellum.Modules.Modelling.Export;
using Vellum.Modules.Workspaces;
using Vellum.Shared;

namespace Vellum.Modules.Import.Proposals;

/// <summary>
/// What a tracking scan applies by itself. Extraction is observation, not inference, so its
/// output reaches the model without a review gate (§2.2 holds: each fact keeps its label, its
/// Snapshot, file and line provenance, and the person who started the scan as its acceptor).
/// <list type="bullet">
/// <item>Every addition is applied, in dependency order.</item>
/// <item>A change is applied only when this Source wrote the fact last and no person edited it
/// since: the planner made it a <c>change</c> (not a <c>conflict</c>), the fact is linked to
/// this Source, and no other Source holds it.</item>
/// <item>Everything else stays pending for a person: conflicts, removals, "same as?"
/// questions, relinks, and any proposal the model refuses or whose acceptance breaks a
/// scenario. These are the "Needs you" list.</item>
/// </list>
/// The proposals go through <see cref="DecideProposals"/>, the one write path of the importer.
/// Nothing is deleted: a removal is never applied here.
/// </summary>
internal static class AutoApply
{
    /// <summary>The most decisions that may refuse something before the rest is left for a person.</summary>
    public const int MaxRefusedRounds = 8;

    /// <summary>
    /// Applies the additions and the untouched changes of a completed tracking scan, as
    /// <paramref name="acceptedBy"/> or, without one, as the person who started the scan.
    /// Returns how many proposals were applied. A scan that is no tracking scan, or whose
    /// starter is no Editor of the project now, applies nothing.
    /// </summary>
    public static async Task<int> RunAsync(IServiceProvider services, Guid scanId, string? acceptedBy, TimeProvider time, CancellationToken ct)
    {
        await using var scope = services.GetRequiredService<IServiceScopeFactory>().CreateAsyncScope();
        var scoped = scope.ServiceProvider;
        var context = scoped.GetRequiredService<DeedboxContext>();
        context.TenantId = services.GetRequiredService<DeedboxContext>().TenantId;
        var db = scoped.GetRequiredService<AppDbContext>();

        var scan = await db.Scans.AsNoTracking().FirstOrDefaultAsync(s => s.Id == scanId, ct);
        if (scan is null || scan.Status != ScanStatus.Completed || scan.CommitSha is null)
            return 0;
        var tracked = await db.TrackedRefs.AsNoTracking()
            .FirstOrDefaultAsync(t => t.ProjectId == scan.ProjectId && t.Host == scan.Host && t.Repository == scan.Repository, ct);
        if (!ScanKindInfo.Of(scan, tracked).Tracking)
            return 0;

        var (userId, actor) = acceptedBy is not null ? (acceptedBy, $"user:{acceptedBy}") : await StarterAsync(db, scan.RequestedBy, ct);
        if (userId is null || !Guid.TryParse(userId, out _))
            return 0;
        var workspaceId = await db.Projects.AsNoTracking().Where(p => p.Id == scan.ProjectId).Select(p => (Guid?)p.WorkspaceId).FirstOrDefaultAsync(ct);
        if (workspaceId is null
            || await scoped.GetRequiredService<WorkspaceAuthorizationService>().GetRoleAsync(workspaceId.Value, userId, ct) is not >= WorkspaceRole.Editor)
            return 0;

        var refused = new HashSet<string>(StringComparer.Ordinal);
        var applied = 0;
        var refusedRounds = 0;
        while (true)
        {
            db.ChangeTracker.Clear();
            var batch = Batch(await CandidatesAsync(db, scan, refused, ct));
            if (batch.Count == 0)
                break;

            // The events are the person's: they started the scan that observed the facts.
            context.Metadata = new EventMetadata { Actor = actor, CorrelationId = scan.Id.ToString() };
            var result = await DecideProposals.DecideAsync(scoped, db, scan,
                new DecideProposals.DecisionInput([.. batch.Select(c => c.Id)], [], [], [], AcknowledgeImpact: false), userId, time, ct);
            if (result is not Ok<DecideProposalsResponse> { Value: { } done })
                break;
            if (done.Failed.Length == 0)
            {
                applied += batch.Count;
                continue;
            }

            // A refused proposal stays for a person, and so does what depends on it. A
            // re-plan may give the proposals new IDs, so a refusal is kept by what it says.
            var byId = batch.ToDictionary(c => c.Id);
            foreach (var failure in done.Failed)
            {
                if (byId.TryGetValue(failure.Id, out var candidate))
                    refused.Add(candidate.Fingerprint);
            }
            if (++refusedRounds >= MaxRefusedRounds)
                break;
        }
        return applied;
    }

    private sealed record Candidate(Guid Id, int Seq, StoredEntityKind EntityKind, Guid[] DependsOn, string Fingerprint);

    /// <summary>
    /// The pending proposals the scan may apply by itself, in the order of the scan, which is
    /// an order in which each comes after what it depends on. One whose dependency is neither
    /// accepted nor applied with it is left out.
    /// </summary>
    private static async Task<List<Candidate>> CandidatesAsync(AppDbContext db, ScanEntity scan, HashSet<string> refused, CancellationToken ct)
    {
        var pending = await db.ImportProposals.AsNoTracking()
            .Where(p => p.ScanId == scan.Id && p.Status == ProposalStatus.Pending
                && (p.Action == ProposalAction.Add || (p.Action == ProposalAction.Change && p.OtherSources == "[]")))
            .OrderBy(p => p.Seq)
            .Select(p => new { p.Id, p.Seq, p.Action, p.EntityKind, p.EntityId, p.Subject, p.FactKey, p.DependsOn, p.Fingerprint })
            .ToListAsync(ct);

        // A change is this Source's to make only on a fact this Source wrote: one linked to it.
        var changed = pending.Where(p => p.Action == ProposalAction.Change).Select(p => p.EntityId).Distinct().ToList();
        var linked = (await db.FactLinks.AsNoTracking()
                .Where(l => l.ProjectId == scan.ProjectId && l.Host == scan.Host && l.Repository == scan.Repository && changed.Contains(l.EntityId))
                .Select(l => new { l.Subject, l.FactKey, l.EntityId })
                .ToListAsync(ct))
            .Select(l => (l.Subject, l.FactKey, l.EntityId))
            .ToHashSet();

        var set = pending
            .Where(p => !refused.Contains(p.Fingerprint)
                && (p.Action == ProposalAction.Add || linked.Contains((p.Subject, p.FactKey, p.EntityId))))
            .Select(p => new Candidate(p.Id, p.Seq, p.EntityKind, p.DependsOn, p.Fingerprint))
            .ToDictionary(c => c.Id);

        var outside = set.Values.SelectMany(c => c.DependsOn).Where(d => !set.ContainsKey(d)).Distinct().ToList();
        var accepted = (await db.ImportProposals.AsNoTracking()
            .Where(p => outside.Contains(p.Id) && p.Status == ProposalStatus.Accepted)
            .Select(p => p.Id).ToListAsync(ct)).ToHashSet();
        bool dropped;
        do
        {
            dropped = false;
            foreach (var candidate in set.Values.ToList())
            {
                if (candidate.DependsOn.Any(d => !set.ContainsKey(d) && !accepted.Contains(d)))
                {
                    set.Remove(candidate.Id);
                    dropped = true;
                }
            }
        }
        while (dropped);
        return [.. set.Values.OrderBy(c => c.Seq)];
    }

    /// <summary>
    /// The first proposals of the list that one decision may hold. A later decision finds the
    /// earlier ones accepted, so what it depends on is met.
    /// </summary>
    private static List<Candidate> Batch(List<Candidate> candidates)
    {
        var batch = new List<Candidate>();
        var schemas = 0;
        foreach (var candidate in candidates)
        {
            if (batch.Count == DecideProposals.MaxProposals)
                break;
            if (candidate.EntityKind == StoredEntityKind.Schema)
            {
                if (schemas == ImportModel.MaxSchemas)
                    break;
                schemas++;
            }
            batch.Add(candidate);
        }
        return batch;
    }

    /// <summary>
    /// The person who started a scan, and the actor of the events written for them. A scan of
    /// an agent token is the token's: it acts for the person who made it.
    /// </summary>
    private static async Task<(string? UserId, string Actor)> StarterAsync(AppDbContext db, string requestedBy, CancellationToken ct)
    {
        if (AgentPrincipal.IsAgentActor(requestedBy))
        {
            var tokenId = Guid.TryParse(requestedBy[AgentPrincipal.AgentActorPrefix.Length..], out var id) ? id : Guid.Empty;
            var owner = await db.AgentTokens.AsNoTracking().Where(t => t.Id == tokenId).Select(t => t.CreatedBy).FirstOrDefaultAsync(ct);
            return (owner, requestedBy);
        }
        var userId = requestedBy.StartsWith("user:", StringComparison.Ordinal) ? requestedBy["user:".Length..] : requestedBy;
        return (userId, $"user:{userId}");
    }
}
