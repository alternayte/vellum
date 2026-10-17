using System.Security.Claims;
using Deedbox;
using Microsoft.EntityFrameworkCore;
using Vellum.Modules.Import.Scans;
using Vellum.Modules.Modelling;
using Vellum.Modules.Workspaces;
using Vellum.Shared;
using static Vellum.Shared.ResultExtensions;

namespace Vellum.Modules.Import.Proposals;

/// <param name="EntityKind"><c>element</c>, <c>operation</c>, <c>channel</c>, <c>subscription</c>, <c>binding</c>, <c>message</c> or <c>relationship</c>.</param>
public sealed record KeepFactRequest(string? EntityKind, Guid EntityId);

/// <summary>
/// A person keeps a fact that the latest Snapshot of its Source no longer holds ("not seen in
/// latest scan"). The mark goes, the pending removal of the fact is rejected, and the rejection
/// is remembered by what it says: the next scans of the Source neither mark the fact nor
/// propose its removal again while its values stay the same. A later Snapshot that changes the
/// fact, or an edit of it, makes the removal a new statement, and the mark can come back.
/// <para>A rescan deletes nothing; removing such a fact is a person's act too, through the
/// removal in "Needs you" or the delete of the entity.</para>
/// </summary>
public static class KeepFact
{
    public static async Task<IResult> Handle(
        Guid projectId, KeepFactRequest request, ClaimsPrincipal user, HttpContext http,
        WorkspaceAuthorizationService auth, AppDbContext db, TimeProvider time, CancellationToken ct)
    {
        var userId = user.UserId();
        await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Editor, ct);
        if (!EnumText.TryParse(request.EntityKind, out StoredEntityKind kind) || kind == StoredEntityKind.Schema)
            return Invalid("entityKind", "Name the kind of a model fact: element, operation, channel, subscription, binding, message or relationship.");

        var streamId = await db.Projects.AsNoTracking().Where(p => p.Id == projectId).Select(p => p.StreamId).FirstAsync(ct);
        var now = time.GetUtcNow();
        return await SharedTransaction.RunAsync(http.RequestServices, services =>
            KeepAsync(services, projectId, streamId, kind, request.EntityId, userId, now, ct), ct);
    }

    private static async Task<IResult> KeepAsync(
        IServiceProvider services, Guid projectId, Guid streamId, StoredEntityKind kind, Guid entityId, string userId, DateTimeOffset now, CancellationToken ct)
    {
        var db = services.GetRequiredService<AppDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var store = services.GetRequiredService<IEventStore>().ForProject(projectId).UseDbContext(db);
        // The lock of the model stream: a scan or a decision of the project waits for the keep.
        var locked = await store.Execute<ModelState>(StreamId.From(streamId), _ => [], ct);

        var kindText = EnumText.Of(kind);
        if (ModelIndex.Read(locked.State, kindText, entityId) is not { } entity)
            return NotFound(kindText);
        if (entity.Stale is not { } mark)
            return Results.Conflict(new ErrorResponse("not_marked", "This fact is not marked 'not seen in latest scan'. There is nothing to keep."));

        // The facts of the Source that set the mark: of any Source when the mark names none.
        var links = db.FactLinks.Where(l => l.ProjectId == projectId && l.EntityKind == kind && l.EntityId == entityId);
        if (mark.SourceId is { } sourceId
            && await db.Sources.AsNoTracking().FirstOrDefaultAsync(s => s.Id == sourceId && s.ProjectId == projectId, ct) is { } source)
            links = links.Where(l => l.Host == source.Host && l.Repository == source.Locator);
        var linked = await links.AsNoTracking().ToListAsync(ct);

        var removals = await db.ImportProposals
            .Where(p => p.ProjectId == projectId && p.EntityId == entityId && p.EntityKind == kind
                && p.Action == ProposalAction.Remove && p.Status == ProposalStatus.Pending)
            .ToListAsync(ct);
        foreach (var removal in removals)
        {
            removal.Status = ProposalStatus.Rejected;
            removal.DecidedBy = userId;
            removal.DecidedAt = now;
        }

        foreach (var link in linked)
        {
            var fingerprint = ProposalPlanner.RemoveFingerprint(kindText, FactMapping.KeyOf(link.Subject, link.FactKey), entity.Values);
            var known = await db.Rejections.AnyAsync(r =>
                r.ProjectId == projectId && r.Host == link.Host && r.Repository == link.Repository
                && r.Subject == link.Subject && r.FactKeyHash == link.FactKeyHash && r.Fingerprint == fingerprint, ct);
            if (known)
                continue;
            db.Rejections.Add(new RejectionEntity
            {
                Id = Guid.NewGuid(), ProjectId = projectId, Host = link.Host, Repository = link.Repository, RepositoryId = link.RepositoryId,
                Subject = link.Subject, FactKey = link.FactKey, FactKeyHash = link.FactKeyHash, Fingerprint = fingerprint,
                ScanId = removals.FirstOrDefault()?.ScanId ?? link.ScanId, RejectedBy = userId, RejectedAt = now,
            });
        }

        var events = StaleDecider.Mark(locked.State, entityId, null);
        if (events.Count > 0)
            await store.Append(StreamId.From(streamId), ExpectedVersion.Exact(locked.Version), events, ct);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return Results.NoContent();
    }
}
