using Deedbox;
using Microsoft.EntityFrameworkCore;
using Vellum.Modules.Promises;
using Vellum.Modules.Review.Decisions;
using Vellum.Modules.Review.Rules;
using Vellum.Modules.Workspaces.Authorization;
using Vellum.Shared;

namespace Vellum.Modules.Review;

/// <param name="Blocking">Findings that prevent Build Ready now.</param>
/// <param name="Concerns">Findings with the result <c>concern</c>, settled or not.</param>
/// <param name="Settled">Concerns and unknowns that a person answered and that did not change since.</param>
public sealed record ReviewCounts(int Blocking, int Concerns, int Unknown, int Verified, int NotApplicable, int Settled, int NeedsReReview)
{
    internal static ReviewCounts Of(IReadOnlyList<FindingView> findings) => new(
        findings.Count(f => f.Blocks),
        findings.Count(f => f.Finding.Result == RuleResult.Concern),
        findings.Count(f => f.Finding.Result == RuleResult.Unknown),
        findings.Count(f => f.Finding.Result == RuleResult.Verified),
        findings.Count(f => f.Finding.Result == RuleResult.NotApplicable),
        findings.Count(f => f.Finding.Fired && f.Settled),
        findings.Count(f => f.NeedsReReview));
}

/// <summary>
/// The findings of one pinned state. They are computed on every read and are a function of
/// that state: <paramref name="Hash"/> is the same for the same state. A finding says what
/// the declared model shows. It is no evidence about a running system (SDD guarantee 8).
/// </summary>
/// <param name="Branch">The model stream that was read.</param>
/// <param name="InputsFingerprint">A hash of the version of every input: the model, the scenarios, the schemas, the promises, their runs, the followed commits and the decisions.</param>
/// <param name="Hash">A hash of the rule set version and every finding. Dispositions are not part of it.</param>
public sealed record FindingsResponse(
    Guid ProjectId, Guid Branch, long ModelVersion, Guid? RevisionId, string InputsFingerprint,
    string RuleSetVersion, string Hash, ReviewCounts Counts, IReadOnlyList<FindingView> Findings)
{
    internal static FindingsResponse Of(ReviewState state) => new(
        state.Input.ProjectId, state.Input.Pin.StreamId, state.Input.Pin.Version, state.Input.Pin.RevisionId,
        PromiseJson.Hash(state.Input.Pinned), state.Report.RuleSetVersion, state.Report.Hash, ReviewCounts.Of(state.Findings), state.Findings);
}

public sealed record SaveSnapshotRequest(Guid? RevisionId = null, Guid? BranchId = null, string? Note = null);

public sealed record SnapshotSummary(
    Guid Id, Guid Branch, long ModelVersion, Guid? RevisionId, string InputsFingerprint, string RuleSetVersion, string Hash,
    string? Note, string CreatedBy, DateTimeOffset CreatedAt);

/// <summary>The findings of a pinned state as a person saved them, with the dispositions of that time.</summary>
public sealed record SnapshotDto(
    Guid Id, Guid ProjectId, Guid Branch, long ModelVersion, Guid? RevisionId, string InputsFingerprint, string RuleSetVersion, string Hash,
    string? Note, IReadOnlyList<FindingView> Findings, string CreatedBy, DateTimeOffset CreatedAt);

/// <param name="Status"><c>open</c>, <c>accepted-by-decision</c> or <c>wont-fix</c>.</param>
/// <param name="DecisionId">For <c>accepted-by-decision</c>: an accepted decision of the project.</param>
/// <param name="Reason">For <c>wont-fix</c>: required.</param>
/// <param name="RevisionId">The pinned state the person read the finding on. Default: the main model.</param>
public sealed record SetDispositionRequest(
    string RuleId, Guid SubjectId, string Status, Guid? DecisionId = null, string? Reason = null, Guid? RevisionId = null, Guid? BranchId = null);

/// <summary>
/// Review mode (SDD §3 and §8): the deterministic findings of a pinned state, saved
/// snapshots of them, and what a person did about a finding. No endpoint here calls an AI.
/// </summary>
public static class ReviewEndpoints
{
    public const int MaxSnapshotsPerProject = 200;

    public static IResult Rules() => Results.Ok(RuleSet.Catalogue);

    public static async Task<IResult> Findings(
        Guid projectId, Guid? revisionId, Guid? branchId, HttpContext http, WorkspaceAuthorizationService auth, CancellationToken ct)
    {
        await auth.RequireProjectRoleAsync(projectId, http.User.UserId(), WorkspaceRole.Viewer, ct);
        var (state, refusal) = await ReviewEngine.LoadAsync(http.RequestServices, projectId, revisionId, branchId, ct);
        return state is null ? refusal! : Results.Ok(FindingsResponse.Of(state));
    }

    public static async Task<IResult> SaveSnapshot(
        Guid projectId, HttpContext http, WorkspaceAuthorizationService auth, AppDbContext db, TimeProvider time, CancellationToken ct)
    {
        var userId = http.User.UserId();
        await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Editor, ct);
        var (request, error) = await PromiseAccess.ReadAsync<SaveSnapshotRequest>(http, allowEmpty: true);
        if (error is not null)
            return error;
        if (TextRule.Lines("note", request?.Note) is { } invalid)
            return ReviewAccess.Invalid(invalid.Field, invalid.Message);
        if (await db.ReviewSnapshots.CountAsync(s => s.ProjectId == projectId, ct) >= MaxSnapshotsPerProject)
            return ReviewAccess.Conflict($"A project holds at most {MaxSnapshotsPerProject} review snapshots. Delete one first.");

        var (state, refusal) = await ReviewEngine.LoadAsync(http.RequestServices, projectId, request?.RevisionId, request?.BranchId, ct);
        if (state is null)
            return refusal!;

        var row = new SnapshotEntity
        {
            Id = Guid.NewGuid(), ProjectId = projectId,
            Branch = state.Input.Pin.StreamId, ModelVersion = state.Input.Pin.Version, RevisionId = state.Input.Pin.RevisionId,
            InputsFingerprint = PromiseJson.Hash(state.Input.Pinned), RuleSetVersion = state.Report.RuleSetVersion, Hash = state.Report.Hash,
            Note = string.IsNullOrWhiteSpace(request?.Note) ? null : request.Note.Trim(),
            Findings = PromiseJson.Write(state.Findings), CreatedBy = userId, CreatedAt = time.GetUtcNow(),
        };
        db.ReviewSnapshots.Add(row);
        await db.SaveChangesAsync(ct);
        return Results.Created($"/api/projects/{projectId}/review/snapshots/{row.Id}", ToDto(row));
    }

    public static async Task<IResult> ListSnapshots(
        Guid projectId, HttpContext http, WorkspaceAuthorizationService auth, AppDbContext db, CancellationToken ct)
    {
        await auth.RequireProjectRoleAsync(projectId, http.User.UserId(), WorkspaceRole.Viewer, ct);
        var rows = await db.ReviewSnapshots.AsNoTracking().Where(s => s.ProjectId == projectId)
            .OrderByDescending(s => s.CreatedAt).ThenBy(s => s.Id)
            .Select(s => new SnapshotSummary(s.Id, s.Branch, s.ModelVersion, s.RevisionId, s.InputsFingerprint, s.RuleSetVersion, s.Hash, s.Note, s.CreatedBy, s.CreatedAt))
            .ToListAsync(ct);
        return Results.Ok(new Page<SnapshotSummary>(rows, null));
    }

    public static async Task<IResult> GetSnapshot(
        Guid projectId, Guid snapshotId, HttpContext http, WorkspaceAuthorizationService auth, AppDbContext db, CancellationToken ct)
    {
        await auth.RequireProjectRoleAsync(projectId, http.User.UserId(), WorkspaceRole.Viewer, ct);
        return await db.ReviewSnapshots.AsNoTracking().FirstOrDefaultAsync(s => s.Id == snapshotId && s.ProjectId == projectId, ct) is { } row
            ? Results.Ok(ToDto(row))
            : ReviewAccess.NotFound("Snapshot");
    }

    public static async Task<IResult> DeleteSnapshot(
        Guid projectId, Guid snapshotId, HttpContext http, WorkspaceAuthorizationService auth, AppDbContext db, CancellationToken ct)
    {
        await auth.RequireProjectRoleAsync(projectId, http.User.UserId(), WorkspaceRole.Editor, ct);
        return await db.ReviewSnapshots.Where(s => s.Id == snapshotId && s.ProjectId == projectId).ExecuteDeleteAsync(ct) == 1
            ? Results.NoContent()
            : ReviewAccess.NotFound("Snapshot");
    }

    private static SnapshotDto ToDto(SnapshotEntity row) => new(
        row.Id, row.ProjectId, row.Branch, row.ModelVersion, row.RevisionId, row.InputsFingerprint, row.RuleSetVersion, row.Hash,
        row.Note, PromiseJson.Read<List<FindingView>>(row.Findings), row.CreatedBy, row.CreatedAt);

    /// <summary>
    /// Records what a person does about a finding. Only a person in an interactive session
    /// calls this: no agent token, no import and no AI call sets a disposition.
    /// <list type="bullet">
    /// <item><c>wont-fix</c> needs the Editor role and a reason. It never settles a blocking finding.</item>
    /// <item><c>accepted-by-decision</c> needs the Owner role and an accepted decision in force.
    /// The decision records the finding as it is now; that is also how a finding that changed
    /// is reviewed again.</item>
    /// <item><c>open</c> removes the answer. Taking a finding out of a decision needs the Owner role.</item>
    /// </list>
    /// The disposition names the rule, the subject and what the finding was. When the version of
    /// the rule or the facts of the subject change, it shows as <c>needsReReview</c> and settles nothing.
    /// </summary>
    public static async Task<IResult> SetDisposition(
        Guid projectId, HttpContext http, WorkspaceAuthorizationService auth, IEventStore store, AppDbContext db, TimeProvider time, CancellationToken ct)
    {
        var userId = http.User.UserId();
        await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Editor, ct);
        var (request, error) = await PromiseAccess.ReadAsync<SetDispositionRequest>(http);
        if (request is null)
            return error!;
        if (request.Status is not (DispositionStatus.Open or DispositionStatus.AcceptedByDecision or DispositionStatus.WontFix))
            return ReviewAccess.Invalid("status", "The status is open, accepted-by-decision or wont-fix");
        if (!RuleSet.IsRule(request.RuleId))
            return ReviewAccess.Invalid("ruleId", "This is not the ID of a review rule");

        var (state, refusal) = await ReviewEngine.LoadAsync(http.RequestServices, projectId, request.RevisionId, request.BranchId, ct);
        if (state is null)
            return refusal!;
        if (state.Findings.FirstOrDefault(f => f.Finding.RuleId == request.RuleId && f.Finding.Subject.Id == request.SubjectId) is not { } view)
            return ReviewAccess.NotFound("Finding");

        var finding = view.Finding;
        var at = time.GetUtcNow();
        var settledBy = state.Book.InForce
            .Where(d => d.State.Content!.Settles.Any(s => s.RuleId == finding.RuleId && s.SubjectId == finding.Subject.Id)).ToList();
        // Each write of the answer is one statement, so two people who answer one finding at
        // the same time both succeed and the later answer stands.
        var answer = db.Dispositions.Where(d => d.ProjectId == projectId && d.RuleId == finding.RuleId && d.SubjectId == finding.Subject.Id);

        switch (request.Status)
        {
            case DispositionStatus.WontFix:
                if (string.IsNullOrWhiteSpace(request.Reason))
                    return ReviewAccess.Invalid("reason", "A reason is required: say why the finding stands");
                if (TextRule.Lines("reason", request.Reason) is { } invalid)
                    return ReviewAccess.Invalid(invalid.Field, invalid.Message);
                if (!finding.Fired)
                    return ReviewAccess.Conflict($"The finding is {finding.Result}. It has nothing to answer.");
                if (finding.Severity == Severity.Blocking)
                    return ReviewAccess.Conflict("A blocking finding is settled by an accepted decision only. Write a decision that names the rule and the subject, and accept it.");
                if (settledBy.Count > 0)
                    return ReviewAccess.Conflict($"{DecisionState.Label(settledBy[0].State.Number)} names this finding. Set the finding to open first.");

                var (ruleId, subjectId, reason) = (finding.RuleId, finding.Subject.Id, request.Reason.Trim());
                await db.Database.ExecuteSqlAsync($"""
                    INSERT INTO review.dispositions (project_id, rule_id, subject_id, rule_version, fingerprint, status, reason, decided_by, decided_at)
                    VALUES ({projectId}, {ruleId}, {subjectId}, {finding.RuleVersion}, {finding.Fingerprint}, {DispositionStatus.WontFix}, {reason}, {userId}, {at})
                    ON CONFLICT (project_id, rule_id, subject_id) DO UPDATE SET
                        rule_version = EXCLUDED.rule_version, fingerprint = EXCLUDED.fingerprint, status = EXCLUDED.status,
                        reason = EXCLUDED.reason, decided_by = EXCLUDED.decided_by, decided_at = EXCLUDED.decided_at
                    """, ct);
                break;

            case DispositionStatus.AcceptedByDecision:
                await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Owner, ct);
                if (request.DecisionId is not { } decisionId || state.Book.All.FirstOrDefault(d => d.Id == decisionId) is not { } decision)
                    return ReviewAccess.Invalid("decisionId", "Name a decision of the project");
                if (state.Book.StatusOf(decision) != DecisionStatus.Accepted)
                    return ReviewAccess.Conflict($"{DecisionState.Label(decision.State.Number)} is {state.Book.StatusOf(decision)}. Only an accepted decision in force settles a finding.");
                if (!finding.Fired)
                    return ReviewAccess.Conflict($"The finding is {finding.Result}. It has nothing to settle.");
                if (settledBy.FirstOrDefault(d => d.Id != decisionId) is { } other)
                    return ReviewAccess.Conflict($"{DecisionState.Label(other.State.Number)} names this finding already. Set the finding to open first.");

                var recorded = await DecisionEndpoints.DecideAsync(store, decisionId, s => DecisionDecider.RecordSettlement(
                    s, projectId, new SettlementStamp(finding.RuleId, finding.Subject.Id, finding.RuleVersion, finding.Fingerprint), userId, at), ct);
                if (recorded is not CommandResult<DecisionState>.Success)
                    return recorded.ToHttpResult();
                await answer.ExecuteDeleteAsync(ct);
                break;

            default:
                if (settledBy.Count > 0)
                    await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Owner, ct);
                foreach (var named in settledBy)
                {
                    var removed = await DecisionEndpoints.DecideAsync(store, named.Id,
                        s => DecisionDecider.RemoveSettlement(s, projectId, finding.RuleId, finding.Subject.Id, userId, at), ct);
                    if (removed is not CommandResult<DecisionState>.Success)
                        return removed.ToHttpResult();
                }
                await answer.ExecuteDeleteAsync(ct);
                break;
        }

        // The answer is the finding as every reader sees it now.
        (state, refusal) = await ReviewEngine.LoadAsync(http.RequestServices, projectId, request.RevisionId, request.BranchId, ct);
        return state is null ? refusal!
            : state.Findings.FirstOrDefault(f => f.Finding.RuleId == finding.RuleId && f.Finding.Subject.Id == finding.Subject.Id) is { } now ? Results.Ok(now)
            : ReviewAccess.NotFound("Finding");
    }
}
