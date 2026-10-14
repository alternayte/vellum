using System.Globalization;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Vellum.Modules.Build;
using Vellum.Modules.Identity.AgentTokens;
using Vellum.Modules.Import;
using Vellum.Modules.Import.Proposals;
using Vellum.Modules.Import.Scans;
using Vellum.Modules.Settings;
using Vellum.Modules.Workspaces;
using Vellum.Shared;
using static Vellum.Shared.ResultExtensions;

namespace Vellum.Modules.Promises;

/// <summary>
/// The gate policy of the impact report of a project. <c>off</c>: the report has no verdict
/// that fails. <c>fail-on-broken</c>: the verdict is <c>fail</c> only when an approved
/// promise is <c>broken</c>, that is, when a deterministic check of it failed against the
/// scan of the report. <c>at-risk</c>, <c>possible</c> and <c>unknown</c> never fail.
/// </summary>
public enum ImpactGatePolicy
{
    Off,
    FailOnBroken,
}

public static class ImpactGateVerdict
{
    public const string Pass = "pass";
    public const string Fail = "fail";
    /// <summary>The policy is <c>off</c>: the report gives no verdict.</summary>
    public const string Off = "off";
}

/// <param name="Policy"><c>off</c> or <c>fail-on-broken</c>.</param>
/// <param name="Verdict"><c>pass</c>, <c>fail</c>, or <c>off</c> when the policy is <c>off</c>.</param>
/// <param name="Reasons">Why, as sentences. For <c>fail</c>: one for each approved promise that a failed check broke.</param>
public sealed record ImpactGateDto(string Policy, string Verdict, IReadOnlyList<string> Reasons);

/// <param name="ChangedBy">The user who set the policy. Null while it was never set.</param>
/// <param name="CanChange">True when the caller may change the policy: a person with the owner role.</param>
public sealed record ImpactGateSettingDto(string Policy, string? ChangedBy, DateTimeOffset? ChangedAt, bool CanChange);

/// <param name="Policy"><c>off</c> or <c>fail-on-broken</c>.</param>
public sealed record SetImpactGateRequest(string? Policy);

/// <param name="Reference"><c>path:start-end@commit</c>.</param>
/// <param name="Url">The permanent link to the lines at the commit.</param>
public sealed record ImpactReportReferenceDto(string Reference, string Url);

/// <param name="Fields">For a contract change: the fields that differ.</param>
/// <param name="ReferencesOmitted">Source references of the change that the report does not list.</param>
public sealed record ImpactReportChangeDto(
    string Id, string Action, string Kind, string EntityKind, string Name, string Certainty, string Basis,
    IReadOnlyList<string> Fields, IReadOnlyList<ImpactReportReferenceDto> References, int ReferencesOmitted);

/// <param name="ChangeIds">The changes that reach the step. Their source references are in <c>changes</c>.</param>
public sealed record ImpactReportStepDto(string StepId, string Kind, string? Label, string Certainty, IReadOnlyList<string> ChangeIds);

public sealed record ImpactReportFlowDto(Guid ScenarioId, string Name, string Certainty, IReadOnlyList<ImpactReportStepDto> Steps, int StepsOmitted);

/// <param name="Result">
/// What the check showed against this scan, and nothing else: <c>passed</c>, <c>failed</c>,
/// <c>not-run</c> (no run read this scan) or <c>unknown</c> (a run read this scan and could
/// not decide; <paramref name="Detail"/> says why).
/// </param>
public sealed record ImpactReportCheckDto(string ObligationId, string Key, string Title, string Result, string? Detail);

public sealed record ImpactReportPromiseDto(
    Guid Id, string RequirementId, string Title, string State, int Revision, string Standing, string Certainty,
    IReadOnlyList<ImpactReasonDto> Reasons, IReadOnlyList<ImpactReportCheckDto> Checks);

/// <summary>How many entries of each list the report leaves out to stay within its bounds.</summary>
public sealed record ImpactReportOmittedDto(int Changes, int Flows, int Promises);

/// <summary>
/// The compact architecture impact report of one completed scan, for a pull request or a CI
/// job. It is the change impact (<see cref="ChangeImpact"/>) cut to fixed bounds, with the
/// verdict of the gate policy of the project. It is no score. The same scan, model,
/// promises, runs and policy give the same report, byte for byte.
/// </summary>
/// <param name="Format">The version of this shape.</param>
/// <param name="ScanCompletedAt">When the scan completed. The report holds no other time.</param>
/// <param name="Unknowns">What the report cannot say, entry by entry.</param>
/// <param name="Limits">What the report does not show, as sentences.</param>
public sealed record ImpactReportDto(
    int Format, Guid ScanId, string Repository, string Kind, ImpactSideDto Base, ImpactSideDto Head,
    ScanPullRequest? PullRequest, DateTimeOffset? ScanCompletedAt, ImpactGateDto Gate,
    IReadOnlyList<ImpactReportChangeDto> Changes, IReadOnlyList<ImpactReportFlowDto> Flows, IReadOnlyList<ImpactReportPromiseDto> Promises,
    int PromisesNotAffected, IReadOnlyList<string> Unknowns, IReadOnlyList<string> Limits, ImpactReportOmittedDto Omitted);

/// <summary>
/// <c>GET …/imports/{scanId}/impact/report</c>: the compact report as Markdown, or as JSON for
/// <c>Accept: application/json</c>. It reads and writes nothing else, and Vellum sends it
/// nowhere: a CI job reads it and does with it what its own workflow says.
/// </summary>
public static class ImpactReport
{
    public const int Format = 1;
    public const int MaxChanges = 40;
    public const int MaxReferencesPerChange = 3;
    public const int MaxFlows = 20;
    public const int MaxStepsPerFlow = 15;
    public const int MaxPromises = 40;
    public const int MaxReasonsPerPromise = 3;
    public const int MaxUnknowns = 20;
    /// <summary>The most characters of the Markdown. A GitHub comment takes 65,536.</summary>
    public const int MaxMarkdownChars = 60_000;

    public const string MarkdownType = "text/markdown; charset=utf-8";

    public static async Task<IResult> Handle(
        Guid projectId, Guid importId, ClaimsPrincipal user, HttpContext http,
        WorkspaceAuthorizationService auth, CancellationToken ct)
    {
        await auth.RequireProjectRoleAsync(projectId, user.UserId(), WorkspaceRole.Viewer, ct);
        var (report, refusal) = await ComputeAsync(http.RequestServices, projectId, importId, ct);
        if (report is null)
            return refusal!;
        return WantsJson(http.Request) ? Results.Ok(report) : Results.Text(Markdown(report, projectId), MarkdownType);
    }

    /// <summary>JSON when the first of <c>application/json</c> and <c>text/markdown</c> in the order of preference of <c>Accept</c> is JSON. Markdown otherwise.</summary>
    internal static bool WantsJson(HttpRequest request)
    {
        foreach (var media in request.GetTypedHeaders().Accept.OrderByDescending(m => m.Quality ?? 1))
        {
            if (media.MediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase))
                return true;
            if (media.MediaType.Equals("text/markdown", StringComparison.OrdinalIgnoreCase))
                return false;
        }
        return false;
    }

    public static async Task<(ImpactReportDto? Report, IResult? Refusal)> ComputeAsync(
        IServiceProvider services, Guid projectId, Guid scanId, CancellationToken ct)
    {
        var (impact, refusal) = await ChangeImpact.ComputeAsync(services, projectId, scanId, ct);
        if (impact is null)
            return (null, refusal);
        var scan = await services.GetRequiredService<AppDbContext>().Scans.AsNoTracking()
            .Where(s => s.Id == scanId && s.ProjectId == projectId)
            .Select(s => new { s.Host, s.CompletedAt })
            .FirstAsync(ct);
        var policy = await ImpactGate.PolicyAsync(services.GetRequiredService<AppDbContext>(), projectId, ct);
        return (Of(impact, $"{scan.Host}/{impact.Repository}", scan.CompletedAt, policy), null);
    }

    /// <summary>The verdict of <paramref name="policy"/> on a report. Only an approved promise that a failed check broke fails it.</summary>
    public static ImpactGateDto Gate(ImpactGatePolicy policy, ChangeImpactDto impact)
    {
        if (policy != ImpactGatePolicy.FailOnBroken)
            return new ImpactGateDto(EnumText.Of(ImpactGatePolicy.Off), ImpactGateVerdict.Off, ["The gate policy of this project is off. The report never fails a build."]);

        var broken = impact.Promises
            .Where(p => p.State == PromiseLifecycle.Approved && p.Standing == ImpactStanding.Broken)
            .OrderBy(p => p.RequirementId, StringComparer.Ordinal).ThenBy(p => p.Id)
            .ToList();
        if (broken.Count > 0)
        {
            return new ImpactGateDto(EnumText.Of(policy), ImpactGateVerdict.Fail, [.. broken.Select(p =>
                $"{p.RequirementId} '{p.Title}' (approved revision {p.Revision.ToString(CultureInfo.InvariantCulture)}) is broken: "
                + $"{string.Join(", ", p.Checks.Where(c => c.Result == RunResult.Failed).Select(c => $"the check '{c.Key}'"))} failed against this scan.")]);
        }

        var reasons = new List<string> { "No deterministic check of an approved promise failed against this scan." };
        var open = impact.Promises.Count(p => p.Standing != ImpactStanding.Broken);
        var draftBroken = impact.Promises.Count(p => p.Standing == ImpactStanding.Broken);
        if (draftBroken > 0)
            reasons.Add(Count(draftBroken, "draft promise is", "draft promises are") + " broken. A draft is not a requirement, so it does not fail the gate.");
        if (open > 0)
            reasons.Add(Count(open, "promise is", "promises are") + " at risk, possibly affected or unknown. These never fail the gate.");
        var approvedChecks = impact.Promises.Where(p => p.State == PromiseLifecycle.Approved).SelectMany(p => p.Checks).ToList();
        var notRun = approvedChecks.Count(c => c.Result == RunResult.NotRun);
        if (notRun > 0)
            reasons.Add(Count(notRun, "source pattern check of an approved promise was", "source pattern checks of approved promises were") + " not run against this scan. A check that did not run cannot fail the gate: run the checks against this scan before the gate is read.");
        var undecided = approvedChecks.Count(c => c.Result == RunResult.Unsupported);
        if (undecided > 0)
            reasons.Add(Count(undecided, "source pattern check of an approved promise", "source pattern checks of approved promises") + " could not decide: the scan did not look everywhere the pattern would be. That never fails the gate.");
        return new ImpactGateDto(EnumText.Of(policy), ImpactGateVerdict.Pass, reasons);
    }

    private static string Count(int n, string one, string many) =>
        string.Create(CultureInfo.InvariantCulture, $"{n} {(n == 1 ? one : many)}");

    private static int Rank(string certainty) => certainty switch
    {
        ImpactCertainty.Definite => 0,
        ImpactCertainty.Possible => 1,
        _ => 2,
    };

    private static int StandingRank(string standing) => standing switch
    {
        ImpactStanding.Broken => 0,
        ImpactStanding.AtRisk => 1,
        ImpactStanding.Possible => 2,
        _ => 3,
    };

    /// <summary>The result of a check as the report states it: <c>unsupported</c> is <c>unknown</c>.</summary>
    private static string Result(string result) => result == RunResult.Unsupported ? "unknown" : result;

    /// <summary><c>path:start-end@commit</c>, from the pointer <c>host/owner/name@commit:path:start-end</c>.</summary>
    internal static string Reference(EvidenceDto evidence)
    {
        var at = evidence.Pointer.IndexOf('@', StringComparison.Ordinal);
        var colon = at < 0 ? -1 : evidence.Pointer.IndexOf(':', at);
        var commit = colon < 0 ? "" : evidence.Pointer[(at + 1)..colon];
        var lines = evidence.EndLine > evidence.StartLine
            ? string.Create(CultureInfo.InvariantCulture, $"{evidence.StartLine}-{evidence.EndLine}")
            : evidence.StartLine.ToString(CultureInfo.InvariantCulture);
        return $"{evidence.Path}:{lines}@{commit}";
    }

    internal static ImpactReportDto Of(ChangeImpactDto impact, string repository, DateTimeOffset? completedAt, ImpactGatePolicy policy)
    {
        var changes = impact.Changes
            .Select((c, i) => (Change: c, Index: i))
            .OrderBy(c => Rank(c.Change.Certainty)).ThenBy(c => c.Index)
            .Take(MaxChanges)
            .Select(c => new ImpactReportChangeDto(
                c.Change.Id, c.Change.Action, c.Change.Kind, c.Change.EntityKind, c.Change.Name, c.Change.Certainty, c.Change.Basis,
                [.. c.Change.Diff.Select(d => d.Field)],
                [.. c.Change.Evidence.Take(MaxReferencesPerChange).Select(e => new ImpactReportReferenceDto(Reference(e), e.Url))],
                Math.Max(0, c.Change.Evidence.Count - MaxReferencesPerChange)))
            .ToList();

        var flows = impact.Flows
            .OrderBy(f => Rank(f.Certainty)).ThenBy(f => f.Name, StringComparer.Ordinal).ThenBy(f => f.ScenarioId)
            .Take(MaxFlows)
            .Select(f => new ImpactReportFlowDto(
                f.ScenarioId, f.Name, f.Certainty,
                [.. f.Steps.Take(MaxStepsPerFlow).Select(s => new ImpactReportStepDto(
                    s.StepId, s.Kind, s.Label, s.Certainty, [.. s.Reasons.Select(r => r.ChangeId).Distinct(StringComparer.Ordinal)]))],
                Math.Max(0, f.Steps.Count - MaxStepsPerFlow)))
            .ToList();

        var promises = impact.Promises
            .OrderBy(p => StandingRank(p.Standing)).ThenBy(p => p.RequirementId, StringComparer.Ordinal).ThenBy(p => p.Id)
            .Take(MaxPromises)
            .Select(p => new ImpactReportPromiseDto(
                p.Id, p.RequirementId, p.Title, p.State, p.Revision, p.Standing, p.Certainty,
                [.. p.Reasons.OrderBy(r => Rank(r.Certainty)).Take(MaxReasonsPerPromise)],
                [.. p.Checks.Select(c => new ImpactReportCheckDto(c.ObligationId, c.Key, c.Title, Result(c.Result), c.Detail))]))
            .ToList();

        var unknowns = new List<string>();
        foreach (var change in impact.Changes.Where(c => c.Certainty == ImpactCertainty.Unknown))
            unknowns.Add($"{change.Action} {(change.Kind == "source-pattern" ? "pattern" : change.EntityKind)} '{change.Name}': {change.Basis}");
        var unknownSteps = impact.Flows.Sum(f => f.Steps.Count(s => s.Certainty == ImpactCertainty.Unknown));
        if (unknownSteps > 0)
            unknowns.Add(Count(unknownSteps, "step of an affected flow is", "steps of affected flows are") + " unknown: the scan did not look where the answer is.");
        var checks = impact.Promises.SelectMany(p => p.Checks).ToList();
        var notRun = checks.Count(c => c.Result == RunResult.NotRun);
        if (notRun > 0)
            unknowns.Add(Count(notRun, "source pattern check was not run against this scan, so it has", "source pattern checks were not run against this scan, so they have") + " no result here.");
        var unsupported = checks.Count(c => c.Result == RunResult.Unsupported);
        if (unsupported > 0)
            unknowns.Add(Count(unsupported, "source pattern check", "source pattern checks") + " ran against this scan and could not decide. Each says why.");
        if (unknowns.Count > MaxUnknowns)
        {
            var cut = unknowns.Count - MaxUnknowns + 1;
            unknowns = [.. unknowns.Take(MaxUnknowns - 1), Count(cut, "more unknown is", "more unknowns are") + " not listed."];
        }

        return new ImpactReportDto(
            Format, impact.ScanId, repository, impact.Kind, impact.Base, impact.Head, impact.PullRequest, completedAt,
            Gate(policy, impact), changes, flows, promises, impact.PromisesNotAffected, unknowns, impact.Limits,
            new ImpactReportOmittedDto(
                impact.Changes.Count - changes.Count, impact.Flows.Count - flows.Count, impact.Promises.Count - promises.Count));
    }

    /// <summary>
    /// The report as Markdown for a CI summary or a pull request comment. Every name, title,
    /// path and reason in it is text of a repository or a person, so it is written as data:
    /// it cannot make a link, a heading, HTML or a table row (<see cref="MarkdownText"/>).
    /// </summary>
    public static string Markdown(ImpactReportDto report, Guid projectId)
    {
        var md = new Markdown();
        var subject = report.PullRequest is { } pr ? $"pull request #{pr.Number.ToString(CultureInfo.InvariantCulture)}" : $"ref {report.Head.Ref ?? "none"}";
        md.Heading(2, Md.Of($"Architecture impact of {subject} on {Md.Code(report.Repository)}"));

        var gate = report.Gate;
        md.Line(Md.Of($"**Gate: {Md.Raw(gate.Verdict)}** (policy {Md.Code(gate.Policy)})")).Blank();
        foreach (var reason in gate.Reasons)
            md.Line(Md.Of($"- {reason}"));
        md.Blank();

        if (report.PullRequest is { } pull)
        {
            md.Line(Md.Of($"Pull request #{pull.Number} {Md.Text(pull.Title ?? "no title")} ({Md.Raw(PullState(pull))}): head {Md.Code(pull.HeadRef)} at {Md.Code(pull.HeadSha)}, base {Md.Code(pull.BaseRef)} at {Md.Code(pull.BaseSha)}{(pull.FromFork ? Md.Raw(", from another repository") : Md.Empty)}.")).Blank();
        }
        var completed = report.ScanCompletedAt?.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture) ?? "unknown";
        md.Line(Md.Of($"Scan {Md.Code(report.ScanId)}, a {Md.Raw(report.Kind)} scan of commit {Md.Code(report.Head.CommitSha ?? "none")}, completed {Md.Raw(completed)}. Compared with {(report.Base.ScanId is null ? Md.Raw("the model") : Md.Of($"ref {Md.Code(report.Base.Ref ?? "none")} at commit {Md.Code(report.Base.CommitSha ?? "none")}"))}.")).Blank();
        md.Line(Md.Raw("`definite`: stands on a contract diff or source lines that the report cites. `possible`: reached only through a flow or a service. `unknown`: the scan did not look where the answer is. This report is no quality score.")).Blank();

        md.Heading(3, Md.Of($"Changed in the source ({report.Changes.Count + report.Omitted.Changes})"));
        if (report.Changes.Count == 0)
            md.Line(Md.Raw("The source matches the model, and its source patterns match those of the base.")).Blank();
        else
        {
            md.Table(["Change", "Name", "Certainty", "Source"], report.Changes.Select(c => new[]
            {
                Md.Of($"{Md.Raw(c.Action)} {(c.Kind == "source-pattern" ? "pattern" : c.EntityKind)}{(c.Fields.Count == 0 ? Md.Empty : Md.Of($" ({string.Join(", ", c.Fields)})"))}"),
                Md.Text(c.Name),
                Md.Raw(c.Certainty),
                c.References.Count == 0 ? Md.Raw("no source lines")
                    : Md.Of($"{Md.Join(", ", c.References.Select(r => Md.Code(r.Reference)))}{(c.ReferencesOmitted > 0 ? Md.Of($", {c.ReferencesOmitted} more") : Md.Empty)}"),
            }));
        }
        Omitted(md, report.Omitted.Changes, "change", "changes");

        md.Heading(3, Md.Of($"Affected flows ({report.Flows.Count + report.Omitted.Flows})"));
        if (report.Flows.Count == 0)
            md.Line(Md.Raw("No stored scenario names anything that changed.")).Blank();
        else
        {
            // Two changes can share a name, such as a message and its schema, so each is named with its kind.
            var names = report.Changes.ToDictionary(c => c.Id, c => $"{(c.Kind == "source-pattern" ? "pattern" : c.EntityKind)} {c.Name}", StringComparer.Ordinal);
            var rows = new List<Md[]>();
            foreach (var flow in report.Flows)
            {
                rows.AddRange(flow.Steps.Select(s => new[]
                {
                    Md.Text(flow.Name),
                    Md.Of($"{Md.Code(s.StepId)} {Md.Raw(s.Kind)}{(s.Label is null ? Md.Empty : Md.Of($" {s.Label}"))}"),
                    Md.Raw(s.Certainty),
                    Md.Join(", ", s.ChangeIds.Select(id => Md.Text(names.GetValueOrDefault(id, id)))),
                }));
                if (flow.StepsOmitted > 0)
                    rows.Add([Md.Text(flow.Name), Md.Of($"{flow.StepsOmitted} more steps"), Md.Empty, Md.Empty]);
            }
            md.Table(["Flow", "Step", "Certainty", "Through"], rows);
        }
        Omitted(md, report.Omitted.Flows, "flow", "flows");

        md.Heading(3, Md.Of($"Promises ({report.Promises.Count + report.Omitted.Promises})"));
        if (report.Promises.Count == 0)
            md.Line(Md.Raw("No promise names anything that changed.")).Blank();
        else
        {
            md.Line(Md.Raw("`broken`: a deterministic check failed against this scan. `at-risk`: the change touches something the promise names, and no check failed against this scan. A check result counts only for this scan: `passed`, `failed`, `not-run` or `unknown`.")).Blank();
            md.Table(["Requirement", "Title", "State", "Standing", "Checks against this scan"], report.Promises.Select(p => new[]
            {
                Md.Code(p.RequirementId),
                Md.Text(p.Title),
                Md.Of($"{Md.Raw(p.State)} r{p.Revision}"),
                Md.Raw(p.Standing),
                p.Checks.Count == 0 ? Md.Raw("none") : Md.Join(", ", p.Checks.Select(c => Md.Of($"{Md.Code(c.Key)} {Md.Raw(c.Result)}"))),
            }));
            foreach (var promise in report.Promises)
            {
                md.Line(Md.Of($"- {Md.Code(promise.RequirementId)}: {Md.Join(" ", promise.Reasons.Select(r => Md.Of($"({Md.Raw(r.Certainty)}) {r.Text}")))}"));
                foreach (var check in promise.Checks.Where(c => c.Detail is not null && c.Result != RunResult.NotRun))
                    md.Line(Md.Of($"  - {Md.Code(check.Key)} {Md.Raw(check.Result)}: {check.Detail}"));
            }
            md.Blank();
        }
        Omitted(md, report.Omitted.Promises, "promise", "promises");
        if (report.PromisesNotAffected > 0)
            md.Line(Md.Of($"{Count(report.PromisesNotAffected, "other promise names", "other promises name")} nothing that changed. The report says nothing about them.")).Blank();

        md.Heading(3, Md.Raw("Unknowns and limits"));
        foreach (var line in report.Unknowns.Concat(report.Limits))
            md.Line(Md.Of($"- {line}"));
        md.Blank();
        md.Line(Md.Of($"The full report: {Md.Code($"GET /api/projects/{projectId}/imports/{report.ScanId}/impact")}"));

        return Bounded(md.ToString());
    }

    /// <summary>The state of a pull request in one of three words. GitHub's text is never written as it is.</summary>
    internal static string PullState(ScanPullRequest pull) =>
        pull.Merged ? "merged" : pull.State == "closed" ? "closed" : pull.State == "open" ? "open" : "unknown";

    private static void Omitted(Markdown md, int count, string one, string many)
    {
        if (count > 0)
            md.Line(Md.Of($"{Count(count, $"more {one} is", $"more {many} are")} not shown. The full report lists them.")).Blank();
    }

    /// <summary>The Markdown cut at a line end to <see cref="MaxMarkdownChars"/>, with a line that says so.</summary>
    internal static string Bounded(string markdown)
    {
        if (markdown.Length <= MaxMarkdownChars)
            return markdown;
        const int Room = 200;
        var end = markdown.LastIndexOf('\n', MaxMarkdownChars - Room);
        var kept = markdown[..(end + 1)];
        return kept + "\n" + string.Create(CultureInfo.InvariantCulture,
            $"The report is cut here: {markdown.Length - kept.Length} more characters are not shown. The full report lists everything.\n");
    }
}

/// <summary>The gate policy of a project: read by any member, changed by a person with the owner role, and audited.</summary>
public static class ImpactGate
{
    public const string AuditAction = "project.impact-gate-changed";

    public static async Task<ImpactGatePolicy> PolicyAsync(AppDbContext db, Guid projectId, CancellationToken ct) =>
        await db.ImpactGates.AsNoTracking().Where(g => g.ProjectId == projectId).Select(g => (ImpactGatePolicy?)g.Policy).FirstOrDefaultAsync(ct)
        ?? ImpactGatePolicy.Off;

    public static async Task<IResult> Get(
        Guid projectId, HttpContext http, WorkspaceAuthorizationService auth, AppDbContext db, CancellationToken ct)
    {
        var userId = http.User.UserId();
        await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Viewer, ct);
        var row = await db.ImpactGates.AsNoTracking().FirstOrDefaultAsync(g => g.ProjectId == projectId, ct);
        var role = await auth.GetRoleAsync(await auth.GetProjectWorkspaceIdAsync(projectId, ct), userId, ct);
        return Results.Ok(new ImpactGateSettingDto(
            EnumText.Of(row?.Policy ?? ImpactGatePolicy.Off), row?.ChangedBy, row?.ChangedAt,
            role == WorkspaceRole.Owner && AgentPrincipal.IsHuman(http)));
    }

    public static async Task<IResult> Set(
        Guid projectId, HttpContext http, WorkspaceAuthorizationService auth, AppDbContext db, TimeProvider time, CancellationToken ct)
    {
        var userId = http.User.UserId();
        await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Owner, ct);
        var (request, error) = await PromiseAccess.ReadAsync<SetImpactGateRequest>(http);
        if (request is null)
            return error!;
        if (!EnumText.TryParse(request.Policy, out ImpactGatePolicy policy))
            return Invalid("policy", $"The policy is {EnumText.Of(ImpactGatePolicy.Off)} or {EnumText.Of(ImpactGatePolicy.FailOnBroken)}");

        var workspaceId = await auth.GetProjectWorkspaceIdAsync(projectId, ct);
        var now = time.GetUtcNow();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        // Changes of one project take turns until the commit, so the audit log names each one with the policy it replaced.
        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtextextended({"impact-gate:" + projectId}, 0))", ct);
        var previous = await PolicyAsync(db, projectId, ct);
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO promises.impact_gates (project_id, policy, changed_by, changed_at) VALUES ({projectId}, {policy}, {userId}, {now})
            ON CONFLICT (project_id) DO UPDATE SET policy = excluded.policy, changed_by = excluded.changed_by, changed_at = excluded.changed_at
            """, ct);
        await AuditLog.AppendAsync(db, workspaceId, userId, AuditAction, $"project:{projectId} impact-gate:{EnumText.Of(previous)}->{EnumText.Of(policy)}", ct);
        await transaction.CommitAsync(ct);

        return Results.Ok(new ImpactGateSettingDto(request.Policy!, userId, now, true));
    }
}
