using System.Security.Claims;
using Deedbox;
using Microsoft.EntityFrameworkCore;
using Vellum.Modules.Build;
using Vellum.Modules.Promises;
using Vellum.Modules.Review.Decisions;
using Vellum.Modules.Review.Rules;
using Vellum.Shared;

namespace Vellum.Modules.Review;

public static class DispositionStatus
{
    public const string Open = "open";
    public const string AcceptedByDecision = "accepted-by-decision";
    public const string WontFix = "wont-fix";
}

/// <summary>
/// A finding with what a person did about it. The finding is computed; the status is a
/// person's act. Only a person sets a status other than <c>open</c>.
/// </summary>
/// <param name="Status"><c>open</c>, <c>accepted-by-decision</c> or <c>wont-fix</c>.</param>
/// <param name="DecisionId">For <c>accepted-by-decision</c>: the accepted decision that names the rule and the subject.</param>
/// <param name="Decision">Its number, for example <c>ADR-0003</c>.</param>
/// <param name="Reason">For <c>wont-fix</c>: the reason the person gave.</param>
/// <param name="NeedsReReview">
/// True when the finding is not the one the person answered: the version of the rule or the
/// facts of the subject changed since. The status then settles nothing until a person reviews it again.
/// </param>
public sealed record FindingView(Finding Finding, string Status, Guid? DecisionId, string? Decision, string? Reason, bool NeedsReReview)
{
    /// <summary>True when a person answered this exact finding.</summary>
    public bool Settled => Status != DispositionStatus.Open && !NeedsReReview;

    /// <summary>
    /// True when the finding prevents Build Ready: a blocking concern that no accepted
    /// decision settles. A <c>wont-fix</c> never unblocks.
    /// </summary>
    public bool Blocks => Finding.Severity == Severity.Blocking && Finding.Result == RuleResult.Concern
                          && !(Status == DispositionStatus.AcceptedByDecision && !NeedsReReview);
}

/// <summary>The review of one pinned state: its findings with their dispositions, and the decisions they were read against.</summary>
internal sealed record ReviewState(BuildInput Input, ReviewReport Report, DecisionBook Book, IReadOnlyList<FindingView> Findings);

internal static class ReviewEngine
{
    /// <summary>
    /// Puts the dispositions on the findings. A decision in force that names a finding gives
    /// <c>accepted-by-decision</c>; it wins over a <c>wont-fix</c>. A proposed, rejected or
    /// superseded decision gives nothing, so the finding is open again.
    /// </summary>
    public static IReadOnlyList<FindingView> Evaluate(
        ReviewReport report, DecisionBook book, IReadOnlyDictionary<(string RuleId, Guid SubjectId), DispositionEntity>? wontFix = null)
    {
        return [.. report.Findings.Select(finding =>
        {
            if (book.Settling(finding.RuleId, finding.Subject.Id) is { } settling)
            {
                var current = settling.Stamp is { } stamp && stamp.RuleVersion == finding.RuleVersion && stamp.Fingerprint == finding.Fingerprint;
                return new FindingView(finding, DispositionStatus.AcceptedByDecision, settling.Decision.Id,
                    DecisionState.Label(settling.Decision.State.Number), null, !current);
            }
            if (wontFix is not null && wontFix.TryGetValue((finding.RuleId, finding.Subject.Id), out var row))
            {
                return new FindingView(finding, DispositionStatus.WontFix, null, null, row.Reason,
                    row.RuleVersion != finding.RuleVersion || row.Fingerprint != finding.Fingerprint);
            }
            return new FindingView(finding, DispositionStatus.Open, null, null, null, false);
        })];
    }

    /// <summary>The review of an input with the decisions it pins. No <c>wont-fix</c> is read: an export depends on pinned inputs only.</summary>
    public static IReadOnlyList<FindingView> Evaluate(BuildView view) =>
        Evaluate(RuleSet.Run(view), view.Decisions);

    /// <summary>
    /// The review of a project for a pinned model: a named revision, or a branch at its
    /// version now. The scenarios, schemas, promises and decisions are read at their versions
    /// now, as an export reads them.
    /// </summary>
    public static async Task<(ReviewState? State, IResult? Refusal)> LoadAsync(
        IServiceProvider services, Guid projectId, Guid? revisionId, Guid? branchId, CancellationToken ct)
    {
        var (pinned, refusal) = await PinnedModel.ResolveAsync(services, projectId, revisionId, branchId, ct);
        if (pinned is null)
            return (null, refusal);

        BuildInput input;
        try
        {
            input = await BuildInput.LoadAsync(services, projectId, pinned, services.GetRequiredService<BuildLimits>(), ct);
        }
        catch (BundleTooLargeException tooLarge)
        {
            return (null, Results.Json(new ErrorResponse("review_too_large", tooLarge.Message), statusCode: StatusCodes.Status413PayloadTooLarge));
        }

        var report = RuleSet.Run(input);
        var book = new DecisionBook(input.Decisions);
        var wontFix = await services.GetRequiredService<AppDbContext>().Dispositions.AsNoTracking()
            .Where(d => d.ProjectId == projectId).ToDictionaryAsync(d => (d.RuleId, d.SubjectId), ct);
        return (new ReviewState(input, report, book, Evaluate(report, book, wontFix)), null);
    }
}

/// <summary>What the review handlers share.</summary>
internal static class ReviewAccess
{
}

/// <summary>Reads the decisions of a project, as they are now or at pinned versions of their streams.</summary>
public static class DecisionReads
{
    /// <summary>Every decision of the project as its row holds it. A row commits with each event of its stream.</summary>
    public static async Task<IReadOnlyList<PinnedDecision>> CurrentAsync(AppDbContext db, Guid projectId, CancellationToken ct) =>
        [.. (await db.Decisions.AsNoTracking().Where(d => d.ProjectId == projectId)
                .OrderBy(d => d.Number).ThenBy(d => d.Id)
                .Select(d => new { d.Id, d.Document, d.Version }).ToListAsync(ct))
            .Select(d => new PinnedDecision(d.Id, PromiseJson.Read<DecisionState>(d.Document), d.Version))];

    /// <summary>
    /// Each decision as its stream held it at the pinned version. A decision that cannot be
    /// read there is named in <paramref name="unavailable"/>.
    /// </summary>
    public static async Task<IReadOnlyList<PinnedDecision>> AtAsync(
        IEventStore store, Guid projectId, IEnumerable<(Guid Id, long Version)> pins, List<string> unavailable, CancellationToken ct)
    {
        var decisions = new List<PinnedDecision>();
        foreach (var (id, version) in pins.OrderBy(p => p.Id))
        {
            if (await StreamAt.FoldAsync<DecisionState>(store, id, version, ct) is { } folded && folded.State.ExistsIn(projectId))
                decisions.Add(new PinnedDecision(id, folded.State, folded.Version));
            else
                unavailable.Add($"decision {id} at version {version}");
        }
        return decisions;
    }
}
