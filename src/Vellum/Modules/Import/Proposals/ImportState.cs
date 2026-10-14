using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Vellum.Modules.Import.Scans;

namespace Vellum.Modules.Import.Proposals;

/// <param name="Reason">For a comparison scan: <c>other-ref</c> or <c>bare-commit</c>.</param>
internal sealed record ScanKindInfo(string Kind, string? Reason)
{
    public bool Tracking => Kind == ScanKind.Tracking;

    /// <summary>
    /// A scan is a tracking scan when it read the tracked ref of its repository, or when
    /// the repository has no tracked ref yet and the scan read a ref. A scan of a bare
    /// commit or of a pull request follows no line of history and is never one.
    /// </summary>
    public static ScanKindInfo Of(ScanEntity scan, TrackedRefEntity? tracked)
    {
        if (scan.IsPullRequest())
            return new ScanKindInfo(ScanKind.Comparison, "pull-request");
        if (scan.IsBareCommit())
            return new ScanKindInfo(ScanKind.Comparison, "bare-commit");
        if (tracked is null || tracked.RefName == (scan.ResolvedRef ?? ""))
            return new ScanKindInfo(ScanKind.Tracking, null);
        // Links accepted before refs were recorded follow the default ref, whose name the
        // next scan without a ref supplies.
        if (tracked.RefName.Length == 0 && scan.RequestedRef is null)
            return new ScanKindInfo(ScanKind.Tracking, null);
        return new ScanKindInfo(ScanKind.Comparison, "other-ref");
    }

    /// <summary>A sentence for a comparison scan, for the scan and for each of its proposals.</summary>
    public static string? Describe(ScanKindInfo kind, TrackedRefEntity? tracked) => kind.Reason switch
    {
        "bare-commit" => "This scan read a commit and no ref. Its proposals are a preview and cannot be decided. Scan a branch or a tag to decide.",
        "pull-request" => "This scan read the head commit of a pull request. Its proposals are a preview and cannot be decided. Scan the tracked ref after the merge to decide.",
        "other-ref" => $"This scan read another ref than the one the accepted facts follow ({(tracked!.RefName.Length == 0 ? "the default ref" : tracked.RefName)}). " +
            "Its proposals are a preview and cannot be decided. To follow the ref of this scan, make it the tracked ref.",
        _ => null,
    };
}

/// <summary>
/// What the import tables say about one repository of one project, as a plan reads it. It is
/// read without a lock; <see cref="Stamp"/> lets the writer see, under the lock, whether it
/// still holds.
/// </summary>
internal sealed record ImportState(
    IReadOnlyList<LinkInfo> Links,
    IReadOnlySet<Guid> TakenEntityIds,
    IReadOnlySet<(string FactKey, string Fingerprint)> Rejections,
    TrackedRefEntity? Tracked,
    string Stamp)
{
    public static async Task<ImportState> LoadAsync(ImportDbContext db, ScanEntity scan, CancellationToken ct)
    {
        var projectLinks = await db.FactLinks.AsNoTracking().Where(l => l.ProjectId == scan.ProjectId).ToListAsync(ct);
        var links = projectLinks
            .Where(l => l.Host == scan.Host && l.Repository == scan.Repository)
            .OrderBy(l => l.Id)
            .Select(l => new LinkInfo(
                l.Id, FactMapping.KeyOf(l.Subject, l.FactKey), l.EntityKind, l.EntityId, l.CommitSha, l.RuleFingerprint,
                ProposalJson.ReadAttributes(l.Attributes), ProposalJson.ReadKeys(l.ConsumerKeys),
                ProposalJson.ReadValues(l.Accepted), ProposalJson.ReadEvidence(l.Evidence)))
            .ToList();
        var rejections = await db.Rejections.AsNoTracking()
            .Where(r => r.ProjectId == scan.ProjectId && r.Host == scan.Host && r.Repository == scan.Repository)
            .OrderBy(r => r.Id)
            .Select(r => new { r.Id, r.Subject, r.FactKey, r.Fingerprint })
            .ToListAsync(ct);
        var tracked = await db.TrackedRefs.AsNoTracking()
            .FirstOrDefaultAsync(t => t.ProjectId == scan.ProjectId && t.Host == scan.Host && t.Repository == scan.Repository, ct);

        // Everything a plan depends on, and everything a decision can change.
        var stamp = new StringBuilder();
        foreach (var link in projectLinks.OrderBy(l => l.Id))
            stamp.Append(link.Id).Append('|').Append(link.FactKeyHash).Append('|').Append(link.RuleFingerprint).Append('|')
                .Append(link.EntityId).Append('|').Append(link.AcceptedAt.UtcTicks).Append('\n');
        foreach (var rejection in rejections)
            stamp.Append(rejection.Id).Append('\n');
        stamp.Append(tracked?.RefName).Append('|').Append(tracked?.SetAt.UtcTicks);

        return new ImportState(
            links,
            projectLinks.Select(l => l.EntityId).ToHashSet(),
            rejections.Select(r => (FactMapping.KeyOf(r.Subject, r.FactKey), r.Fingerprint)).ToHashSet(),
            tracked,
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(stamp.ToString()))));
    }
}
