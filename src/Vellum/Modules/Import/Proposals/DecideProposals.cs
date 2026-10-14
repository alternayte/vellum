using System.Security.Claims;
using System.Text.Json.Nodes;
using Deedbox;
using Microsoft.EntityFrameworkCore;
using Vellum.Modules.Import.Scans;
using Vellum.Modules.Modelling.Export;
using Vellum.Modules.Modelling.Model;
using Vellum.Modules.Schemas;
using Vellum.Modules.Workspaces;
using Vellum.Modules.Workspaces.Authorization;
using Vellum.Shared;
using static Vellum.Shared.ResultExtensions;

namespace Vellum.Modules.Import.Proposals;

/// <param name="Id">A proposal of action <c>relink</c>.</param>
/// <param name="FactKey">The fact key of one of its candidates, or null to say that none of them is the accepted fact.</param>
public sealed record RelinkChoice(Guid Id, string? FactKey);

/// <param name="Accept">Proposals to apply to the model.</param>
/// <param name="Reject">Proposals to refuse. A refused proposal is not made again while it says the same thing.</param>
/// <param name="Relink">Decisions on <c>relink</c> proposals. They change links only, never the model.</param>
/// <param name="AcknowledgeImpact">
/// Must be true to accept a removal that breaks a stored scenario, or leaves an entity
/// pointing at what is removed. Without it such a decision fails and names what would break.
/// </param>
public sealed record DecideProposalsRequest(Guid[]? Accept, Guid[]? Reject, RelinkChoice[]? Relink = null, bool AcknowledgeImpact = false);

public sealed record DecisionFailure(Guid Id, string Reason);

/// <summary>
/// When <paramref name="Failed"/> holds anything, nothing was applied and the other lists
/// are empty: a decision is applied whole or not at all.
/// </summary>
public sealed record DecideProposalsResponse(Guid[] Accepted, Guid[] Rejected, DecisionFailure[] Failed, Guid[] Relinked);

/// <summary>
/// The one place where something a scan found reaches the model: a person with the Editor
/// role names the proposals to accept and to reject. The accepted ones become model events
/// through the model's own deciders; the events, the link rows, the rejections and the
/// proposal statuses commit in one transaction.
/// <para>
/// Only the proposals of a tracking scan can be decided. The first accepted decision for a
/// repository makes the ref of its scan the tracked ref; from then on a scan of another ref
/// is a preview.
/// </para>
/// <para>
/// A decision is all or nothing. When any named proposal cannot be applied (it is not pending,
/// something it depends on is not accepted, the model refuses its values, the model changed
/// since the scan, or it breaks something and the request does not acknowledge that), nothing
/// is written and every such proposal is named with its reason.
/// </para>
/// </summary>
public static class DecideProposals
{
    /// <summary>The most proposals one decision names. It bounds the request and the time the model stream is locked.</summary>
    public const int MaxProposals = 1000;

    public static async Task<IResult> Handle(
        Guid projectId, Guid importId, DecideProposalsRequest request,
        ClaimsPrincipal user, HttpContext http,
        WorkspaceAuthorizationService auth, AppDbContext db,
        TimeProvider time, CancellationToken ct)
    {
        var userId = user.UserId();
        await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Editor, ct);

        var accept = (request.Accept ?? []).Distinct().ToArray();
        var reject = (request.Reject ?? []).Distinct().ToArray();
        var relink = (request.Relink ?? []).Where(r => r is not null).ToArray();
        if (accept.Length == 0 && reject.Length == 0 && relink.Length == 0)
            return Invalid("accept", "Name at least one proposal to accept, to reject or to relink.");
        if (accept.Length + reject.Length + relink.Length > MaxProposals)
            return Invalid("accept", $"One decision names at most {MaxProposals} proposals.");
        if (accept.Intersect(reject).Any())
            return Invalid("reject", "A proposal cannot be accepted and rejected in one decision.");
        if (relink.Select(r => r.Id).Distinct().Count() != relink.Length
            || relink.Any(r => accept.Contains(r.Id) || reject.Contains(r.Id))
            || relink.Any(r => r.FactKey is { } key && (key.Length == 0 || TextRule.Line("factKey", key, FactMapping.MaxFactKey) is not null)))
            return Invalid("relink", "A relink names a relink proposal once, with the fact key of one of its candidates or with null.");

        var scan = await db.Scans.AsNoTracking().FirstOrDefaultAsync(s => s.Id == importId && s.ProjectId == projectId, ct);
        if (scan is null)
            return Results.NotFound(new ErrorResponse("not_found", "Import not found"));
        if (scan.Status != ScanStatus.Completed || scan.CommitSha is null)
            return Results.Conflict(new ErrorResponse("import_not_completed", "The scan has no proposals to decide: it did not complete."));

        // The schema streams must be locked before the first append, so they are read first.
        // A proposal keeps its entity for life, so this list cannot change under the decision.
        var schemaIds = await db.ImportProposals.AsNoTracking()
            .Where(p => p.ScanId == importId && accept.Contains(p.Id) && p.EntityKind == EntityKinds.Schema)
            .Select(p => p.EntityId)
            .Distinct().ToListAsync(ct);
        if (schemaIds.Count > ImportModel.MaxSchemas)
            return Invalid("accept", $"One decision accepts at most {ImportModel.MaxSchemas} schema proposals.");

        var project = await db.Projects.AsNoTracking().FirstAsync(p => p.Id == projectId, ct);
        if (schemaIds.Contains(project.StreamId))
            return Invalid("accept", "A schema proposal names the ID of the project's model stream.");

        return await SharedTransaction.RunAsync(http.RequestServices, services =>
            ApplyAsync(services, scan, project.StreamId, schemaIds, accept, reject, relink, request.AcknowledgeImpact, userId, time.GetUtcNow(), ct), ct);
    }

    private static async Task<IResult> ApplyAsync(
        IServiceProvider services, ScanEntity scan, Guid modelStreamId, List<Guid> schemaIds,
        Guid[] accept, Guid[] reject, RelinkChoice[] relink, bool acknowledgeImpact, string userId, DateTimeOffset now, CancellationToken ct)
    {
        var db = services.GetRequiredService<AppDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var store = services.GetRequiredService<IEventStore>().ForProject(scan.ProjectId).UseDbContext(db);

        // Every stream lock is taken here, in ascending order, before any row lock and before
        // the first append (see SharedTransaction). The lock of the model stream also makes
        // two decisions of one project take turns, so the statuses read below are current.
        var model = ModelState.Initial;
        long modelVersion = 0;
        var schemas = new Dictionary<Guid, (SchemaState State, long Version)>();
        foreach (var streamId in schemaIds.Append(modelStreamId).Order())
        {
            if (streamId == modelStreamId)
            {
                var locked = await store.Execute<ModelState>(StreamId.From(streamId), _ => [], ct);
                (model, modelVersion) = (locked.State, locked.Version);
            }
            else
            {
                var locked = await store.Execute<SchemaState>(StreamId.From(streamId), _ => [], ct);
                schemas[streamId] = (locked.State, locked.Version);
            }
        }

        // Under the lock the tracked ref cannot change, so the kind of the scan is settled here.
        var tracked = await db.TrackedRefs.FirstOrDefaultAsync(
            t => t.ProjectId == scan.ProjectId && t.Host == scan.Host && t.Repository == scan.Repository, ct);
        var kind = ScanKindInfo.Of(scan, tracked);
        if (!kind.Tracking)
            return Results.Conflict(new ErrorResponse("comparison_scan", ScanKindInfo.Describe(kind, tracked)!));

        var named = accept.Concat(reject).Concat(relink.Select(r => r.Id)).ToArray();
        var proposals = await db.ImportProposals.Where(p => p.ScanId == scan.Id && named.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
        var failures = new List<DecisionFailure>();
        foreach (var id in named)
        {
            if (!proposals.TryGetValue(id, out var proposal))
                failures.Add(new DecisionFailure(id, "This is not a proposal of this import."));
            else if (proposal.Status != ProposalStatus.Pending)
                failures.Add(new DecisionFailure(id, $"The proposal is already {proposal.Status}."));
            else if (proposal.Action == ProposalAction.Relink && !relink.Any(r => r.Id == id))
                failures.Add(new DecisionFailure(id, "A relink proposal is decided under 'relink', with the fact key of one of its candidates or with null."));
            else if (proposal.Action != ProposalAction.Relink && relink.Any(r => r.Id == id))
                failures.Add(new DecisionFailure(id, "This is not a relink proposal."));
        }
        if (failures.Count > 0)
            return Failed(failures);

        // A proposal is accepted with, or after, everything it depends on.
        var accepted = accept.Select(id => proposals[id]).OrderBy(p => p.Seq).ToList();
        var outside = accepted.SelectMany(p => p.DependsOn).Distinct().Where(id => !proposals.ContainsKey(id)).ToArray();
        var dependencies = await db.ImportProposals.AsNoTracking()
            .Where(p => outside.Contains(p.Id))
            .Select(p => new { p.Id, p.Name, p.Status, p.EntityKind })
            .ToDictionaryAsync(p => p.Id, ct);
        foreach (var proposal in accepted)
        {
            foreach (var needed in proposal.DependsOn)
            {
                if (accept.Contains(needed))
                    continue;
                if (reject.Contains(needed))
                {
                    failures.Add(new DecisionFailure(proposal.Id,
                        $"It depends on proposal {needed} ({proposals[needed].EntityKind} '{proposals[needed].Name}'), which this decision rejects."));
                    break;
                }
                if (!dependencies.TryGetValue(needed, out var dependency))
                {
                    failures.Add(new DecisionFailure(proposal.Id, $"It depends on proposal {needed}, which no longer exists. Scan the repository again."));
                    break;
                }
                if (dependency.Status != ProposalStatus.Accepted)
                {
                    failures.Add(new DecisionFailure(proposal.Id,
                        $"It depends on proposal {needed} ({dependency.EntityKind} '{dependency.Name}'), which is {dependency.Status}. " +
                        (dependency.Status == ProposalStatus.Pending ? "Accept both in one decision." : "Scan the repository again.")));
                    break;
                }
            }
        }
        if (failures.Count > 0)
            return Failed(failures);

        // What an accepted proposal would break, as the model and the scenarios are now.
        if (!acknowledgeImpact && accepted.Any(p => p.Action is ProposalAction.Remove or ProposalAction.Change or ProposalAction.Conflict))
        {
            var impact = await ProposalImpact.LoadAsync(services, store, scan.ProjectId, model, ct);
            // An entity that this decision removes or changes is dealt with by the decision.
            var handled = accepted.Select(p => p.EntityId).ToHashSet();
            foreach (var proposal in accepted)
            {
                var fields = ProposalJson.ReadChanges(proposal.Changes).Select(c => c.Field).ToList();
                var found = impact.Of(proposal.Action, proposal.EntityKind, proposal.EntityId, fields);
                var broken = found.Scenarios.Select(s => $"scenario '{s.Name}' ({s.Id}; {string.Join(", ", s.StepIds)})")
                    .Concat(found.Entities.Where(e => !handled.Contains(e.Id)).Select(e => $"{e.EntityKind} '{e.Name}' ({e.Id})"))
                    .ToList();
                if (broken.Count > 0)
                {
                    failures.Add(new DecisionFailure(proposal.Id,
                        $"Accepting it affects {string.Join("; ", broken.Take(20))}{(broken.Count > 20 ? $"; and {broken.Count - 20} more" : "")}. " +
                        "Send acknowledgeImpact: true to accept it anyway."));
                }
            }
            if (failures.Count > 0)
                return Failed(failures);
        }

        var repository = $"{scan.Host}/{scan.Repository}";
        var applier = new ProposalApplier(model, schemas, scan.ProjectId, repository, scan.CommitSha!, Guid.Parse(userId), now);
        foreach (var proposal in InDependencyOrder(accepted))
        {
            if (applier.Apply(proposal) is { } reason)
                failures.Add(new DecisionFailure(proposal.Id, reason));
        }
        if (failures.Count > 0)
            return Failed(failures);

        // A message may name a schema that this decision creates, whose stream is locked
        // above, or one the project already has. The rows of the second kind are held until
        // the commit, so none of them can be deleted under the decision (see SchemaReferences).
        var existing = applier.NamedSchemas.Where(n => !applier.CreatedSchemas.Contains(n.SchemaId)).ToList();
        var missing = await SchemaReferences.HoldAsync(db, scan.ProjectId, existing.Select(n => n.SchemaId), ct);
        foreach (var (schemaId, proposalId) in existing.Where(n => missing.Contains(n.SchemaId)))
            failures.Add(new DecisionFailure(proposalId, $"Schema {schemaId} is not a schema of this project. Scan the repository again."));
        if (failures.Count > 0)
            return Failed(failures);

        // The model append goes first, then one append per schema stream; no stream lock is
        // asked for after the first append.
        if (applier.ModelEvents.Count > 0)
            await store.Append(StreamId.From(modelStreamId), ExpectedVersion.Exact(modelVersion), applier.ModelEvents, ct);
        foreach (var (schemaId, version, events) in applier.SchemaAppends)
            await store.Append(StreamId.From(schemaId), ExpectedVersion.Exact(version), events, ct);

        // A schema is deleted under the guard of every other schema delete. It runs after the
        // model append, so a message that this decision removed no longer counts as a reference.
        foreach (var (schemaId, proposalId) in applier.SchemaDeletes)
        {
            var (state, version) = schemas[schemaId];
            if (!await SchemaReferences.HoldForDeleteAsync(db, schemaId, ct))
            {
                failures.Add(new DecisionFailure(proposalId, "The schema list is being rebuilt. Nothing was applied; try again."));
                continue;
            }
            if (await DeleteSchema.ReferrersAsync(services, store, scan.ProjectId, schemaId, ct) is { } refusal)
            {
                failures.Add(new DecisionFailure(proposalId, refusal));
                continue;
            }
            if (SchemaDecider.Delete(state, schemaId) is not CommandResult<IReadOnlyList<object>>.Success delete)
            {
                failures.Add(new DecisionFailure(proposalId, ProposalApplier.ChangedSinceScan));
                continue;
            }
            await store.Append(StreamId.From(schemaId), ExpectedVersion.Exact(version), delete.Value, ct);
        }
        if (failures.Count > 0)
        {
            // The transaction is not committed, so the appends above are undone.
            return Failed(failures);
        }

        // The rules that made the keys of this scan's facts. A link remembers them.
        var ruleFingerprint = await db.ImportSnapshots.AsNoTracking().Where(s => s.ScanId == scan.Id).Select(s => s.RuleFingerprint).FirstOrDefaultAsync(ct);

        foreach (var proposal in accepted)
        {
            proposal.Status = ProposalStatus.Accepted;
            proposal.DecidedBy = userId;
            proposal.DecidedAt = now;

            var link = await FindLinkAsync(db, scan, proposal.Subject, proposal.FactKey, ct);
            if (proposal.Action == ProposalAction.Remove)
            {
                if (link is not null)
                    db.FactLinks.Remove(link);
                continue;
            }

            if (link is null)
            {
                link = new FactLinkEntity
                {
                    Id = Guid.NewGuid(), ProjectId = scan.ProjectId, Host = scan.Host, Repository = scan.Repository,
                    Subject = proposal.Subject, FactKey = proposal.FactKey, FactKeyHash = KeyHash.Of(proposal.FactKey),
                };
                db.FactLinks.Add(link);
            }
            link.RepositoryId = scan.RepositoryId;
            link.EntityKind = proposal.EntityKind;
            link.EntityId = proposal.EntityId;
            link.ScanId = scan.Id;
            link.CommitSha = scan.CommitSha!;
            link.RuleFingerprint = ruleFingerprint;
            link.Attributes = proposal.Attributes;
            link.ConsumerKeys = proposal.ConsumerKeys;
            link.Accepted = ProposalJson.WriteValues(applier.AcceptedValues[proposal.Id]);
            link.Evidence = proposal.Evidence;
            link.AcceptedBy = userId;
            link.AcceptedAt = now;
        }

        // A relink names which fact of this snapshot an accepted fact is, or that none is.
        // It moves the link and nothing else: no value, no evidence and no event changes.
        foreach (var choice in relink)
        {
            var proposal = proposals[choice.Id];
            var link = await FindLinkAsync(db, scan, proposal.Subject, proposal.FactKey, ct);
            if (link is null)
            {
                failures.Add(new DecisionFailure(choice.Id, "The accepted fact of this proposal no longer exists. Scan the repository again."));
                continue;
            }
            if (choice.FactKey is { } chosen)
            {
                var offered = (JsonNode.Parse(proposal.Proposed ?? "{}")?["candidates"] as JsonArray ?? [])
                    .Select(c => c?["factKey"]?.GetValue<string>()).ToList();
                if (!offered.Contains(chosen))
                {
                    failures.Add(new DecisionFailure(choice.Id, "The fact key is not one of the candidates of this proposal."));
                    continue;
                }
                if (await FindLinkAsync(db, scan, Subject.Fact, chosen, ct) is { } holder && holder.Id != link.Id)
                {
                    failures.Add(new DecisionFailure(choice.Id, "Another accepted fact already has this fact key."));
                    continue;
                }
                link.FactKey = chosen;
                link.FactKeyHash = KeyHash.Of(chosen);
            }
            link.RuleFingerprint = ruleFingerprint;
            proposal.Status = ProposalStatus.Accepted;
            proposal.DecidedBy = userId;
            proposal.DecidedAt = now;
        }
        if (failures.Count > 0)
            return Failed(failures);

        foreach (var id in reject)
        {
            var proposal = proposals[id];
            proposal.Status = ProposalStatus.Rejected;
            proposal.DecidedBy = userId;
            proposal.DecidedAt = now;

            // Remembered by what the proposal says, so the next scan does not make it again.
            var hash = KeyHash.Of(proposal.FactKey);
            var known = await db.Rejections.AnyAsync(r =>
                r.ProjectId == scan.ProjectId && r.Host == scan.Host && r.Repository == scan.Repository
                && r.Subject == proposal.Subject && r.FactKeyHash == hash && r.Fingerprint == proposal.Fingerprint, ct);
            if (!known)
            {
                db.Rejections.Add(new RejectionEntity
                {
                    Id = Guid.NewGuid(), ProjectId = scan.ProjectId, Host = scan.Host, Repository = scan.Repository,
                    RepositoryId = scan.RepositoryId, Subject = proposal.Subject, FactKey = proposal.FactKey, FactKeyHash = hash,
                    Fingerprint = proposal.Fingerprint, ScanId = scan.Id, RejectedBy = userId, RejectedAt = now,
                });
            }
        }

        // The first accepted decision says which line of history the accepted facts follow.
        if (accepted.Count > 0)
        {
            if (tracked is null)
                await SnapshotWriter.SetTrackedRefAsync(db, scan, userId, now, ct);
            else
            {
                tracked.CommitSha = scan.CommitSha;
                tracked.ScanId = scan.Id;
                tracked.RepositoryId ??= scan.RepositoryId;
            }
        }

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return Results.Ok(new DecideProposalsResponse(accept, reject, [], [.. relink.Select(r => r.Id)]));
    }

    private static Task<FactLinkEntity?> FindLinkAsync(AppDbContext db, ScanEntity scan, string subject, string factKey, CancellationToken ct)
    {
        var hash = KeyHash.Of(factKey);
        return db.FactLinks.FirstOrDefaultAsync(l =>
            l.ProjectId == scan.ProjectId && l.Host == scan.Host && l.Repository == scan.Repository
            && l.Subject == subject && l.FactKeyHash == hash, ct);
    }

    /// <summary>
    /// Each proposal after the proposals it depends on, and otherwise in the order of the scan.
    /// The caller may name them in any order.
    /// </summary>
    private static List<ProposalEntity> InDependencyOrder(List<ProposalEntity> accepted)
    {
        var byId = accepted.ToDictionary(p => p.Id);
        var ordered = new List<ProposalEntity>(accepted.Count);
        var placed = new HashSet<Guid>();
        var visiting = new HashSet<Guid>();

        foreach (var proposal in accepted)
            Place(proposal);
        return ordered;

        void Place(ProposalEntity proposal)
        {
            if (placed.Contains(proposal.Id) || !visiting.Add(proposal.Id))
                return;
            foreach (var needed in proposal.DependsOn)
            {
                if (byId.TryGetValue(needed, out var first))
                    Place(first);
            }
            placed.Add(proposal.Id);
            ordered.Add(proposal);
        }
    }

    private static IResult Failed(List<DecisionFailure> failures) =>
        Results.Ok(new DecideProposalsResponse([], [], [.. failures.DistinctBy(f => f.Id)], []));

}
