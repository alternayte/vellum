using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Vellum.Modules.GitHub;
using Vellum.Modules.Import;
using Vellum.Modules.Settings.Document;
using Vellum.Shared;

namespace Vellum.Modules.Promises.Checks;

/// <param name="CommitSha">
/// The commit the result is for. Null means the commit the model follows: the last accepted
/// commit of the repository's tracked ref. A commit that is given is shown as the scope.
/// </param>
/// <param name="Conclusion">What the caller says the run concluded with. Optional; Vellum reads the conclusion itself.</param>
public sealed record ExternalSourceRequest(string System, string RunUrl, string? CommitSha = null, string? Conclusion = null);

/// <param name="Result"><c>passed</c> or <c>failed</c>: what the caller says the external test showed.</param>
/// <param name="ArtifactDigest"><c>sha256:&lt;64 hex&gt;</c> of a test report, when the caller has one. Recorded as claimed.</param>
/// <param name="Revision">The promise revision. Default: the approved revision in force.</param>
public sealed record RecordEvidenceRequest(string Result, ExternalSourceRequest Source, string? ArtifactDigest, int? Revision = null);

/// <summary>
/// A result of a test that ran outside Vellum, for a test-plan obligation. It counts only for
/// the obligation it is bound to: the obligation names its evidence source (a workflow of a
/// repository the project follows, and a job when given), and Vellum reads from GitHub that
/// the run is of that repository and that workflow, ran on the commit, is completed, and
/// that the job ran in it. A run of any other repository or workflow is not evidence here.
/// Anything Vellum cannot read so is stored as a claim, and the obligation stays
/// <c>not-run</c> with the claim shown (SDD guarantee 11).
/// </summary>
public static partial class ExternalEvidence
{
    public const string GitHubActions = PromiseRules.GitHubActions;
    public const string Other = "other";

    [GeneratedRegex(@"\A(?:[0-9a-f]{40}|[0-9a-f]{64})\z")]
    private static partial Regex Sha();

    [GeneratedRegex(@"\Asha256:[0-9a-f]{64}\z")]
    private static partial Regex Digest();

    [GeneratedRegex(@"\A[a-z_]{1,40}\z")]
    private static partial Regex Word();

    public static IReadOnlyList<ValidationError> Check(RecordEvidenceRequest request)
    {
        var errors = new List<ValidationError>();
        if (request.Result is not (RunResult.Passed or RunResult.Failed))
            errors.Add(new("result", "The result is passed or failed"));
        if (request.Source is not { } source)
        {
            errors.Add(new("source", "A source is required"));
            return errors;
        }

        if (source.System is not (GitHubActions or Other))
            errors.Add(new("source.system", $"The system is {GitHubActions} or {Other}"));
        if (source.RunUrl is null)
            errors.Add(new("source.runUrl", "A run URL is required"));
        else if (TextRule.Line("source.runUrl", source.RunUrl, TextRule.MaxPointer) is { } urlError)
            errors.Add(urlError);
        else if (!Uri.TryCreate(source.RunUrl, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps || url.UserInfo.Length > 0)
            errors.Add(new("source.runUrl", "The run URL is an https address with no user information"));
        if (source.CommitSha is not null && !Sha().IsMatch(source.CommitSha))
            errors.Add(new("source.commitSha", "A commit SHA is 40 or 64 lower-case hex digits"));
        if (source.Conclusion is not null)
        {
            if (!Word().IsMatch(source.Conclusion))
                errors.Add(new("source.conclusion", "A conclusion is one lower-case word, such as success or failure"));
            else if ((request.Result == RunResult.Passed) != (source.Conclusion == "success"))
                errors.Add(new("source.conclusion", "A passed result has the conclusion success, and a failed result has another conclusion"));
        }
        if (request.ArtifactDigest is not null && !Digest().IsMatch(request.ArtifactDigest))
            errors.Add(new("artifactDigest", "An artifact digest is sha256: followed by 64 lower-case hex digits"));

        return errors;
    }

    /// <summary>What Vellum read from GitHub about a run, when it is the run the obligation is bound to.</summary>
    private sealed record Read(
        string Repository, long RunId, string Commit, bool CommitIsTracked, string? RefName, string Workflow, string? Job,
        string Conclusion, string Result);

    public static async Task<CheckOutcome> AssessAsync(
        RecordEvidenceRequest request, EvidenceSource? bound, Guid workspaceId, Guid projectId, string obligationHash,
        GitHubReader gitHub, SettingsResolver settings, ImportDbContext importDb, CancellationToken ct)
    {
        var source = request.Source;
        var (reason, read, commit, repositoryKey) = await CheckAsync(request, bound, workspaceId, projectId, gitHub, settings, importDb, ct);

        var shownCommit = read?.Commit ?? commit ?? source.CommitSha;
        var environment = shownCommit is null ? source.System : $"{source.System}@{shownCommit}";
        var inputs = new RunInputs(obligationHash, Repository: repositoryKey, CommitSha: shownCommit, Ref: read?.RefName);
        if (read is null)
        {
            var claim = new EvidenceItem(
                EvidenceKind.External,
                Source: new ExternalSource(source.System, source.RunUrl, shownCommit ?? "", bound?.Job, source.Conclusion ?? ""),
                ArtifactDigest: request.ArtifactDigest,
                Identity: EvidenceIdentity.Claimed,
                IdentityReason: reason,
                ClaimedResult: request.Result);
            return new CheckOutcome(
                RunResult.NotRun, environment,
                $"A claim of '{request.Result}' from {source.System}{(shownCommit is null ? "" : $" for commit {shownCommit}")}.",
                $"Vellum could not check that this result is evidence for this obligation ({reason}). It is stored as a claim and does not count as a result.",
                [claim],
                ["Only the caller says that this test ran and what it showed."],
                CommitSha: shownCommit, Inputs: inputs);
        }

        var evidence = new EvidenceItem(
            EvidenceKind.External,
            Source: new ExternalSource(source.System, source.RunUrl, read.Commit, read.Job, read.Conclusion, read.Repository, read.Workflow, read.RunId),
            ArtifactDigest: request.ArtifactDigest,
            Identity: EvidenceIdentity.Checked,
            ClaimedResult: request.Result);
        return new CheckOutcome(
            read.Result, environment,
            string.Create(CultureInfo.InvariantCulture,
                $"GitHub Actions run {read.RunId} of {read.Repository}, workflow '{read.Workflow}'{(read.Job is null ? "" : $", job '{read.Job}'")}, at commit {read.Commit} ({(read.CommitIsTracked ? $"the commit the model follows on '{read.RefName}'" : "a commit the caller named")}), concluded '{read.Conclusion}'."),
            "Vellum read the run from GitHub: it is a run of the repository and the workflow that this obligation names, it is completed, it ran on this commit"
            + (read.Job is null ? "" : ", and the named job ran in it") + ".",
            [evidence],
            [
                "Vellum did not run this test. It checked only that the named run is of the bound repository and workflow, ran on the commit and concluded so.",
                "Vellum did not check that the workflow executes the steps of this plan. The artifact digest is recorded as claimed.",
                read.CommitIsTracked
                    ? "The result is for this commit only. When the model follows a later commit, this result is no longer current."
                    : "The result is for a commit the caller named. It is current only while the model follows that commit.",
                "A passing bounded test is not proof for other inputs, commits or environments.",
            ],
            CommitSha: read.Commit, Inputs: inputs);
    }

    /// <summary>The run as Vellum read it, or a fixed code that says why the result is not evidence for this obligation.</summary>
    private static async Task<(string? Reason, Read? Read, string? Commit, string? RepositoryKey)> CheckAsync(
        RecordEvidenceRequest request, EvidenceSource? bound, Guid workspaceId, Guid projectId,
        GitHubReader gitHub, SettingsResolver settings, ImportDbContext importDb, CancellationToken ct)
    {
        var source = request.Source;
        if (bound is null)
            return ("no-evidence-source", null, null, null);
        if (source.System != GitHubActions || bound.System != GitHubActions)
            return ("system-not-checkable", null, null, null);

        // https://<host>/<owner>/<repo>/actions/runs/<number>[/...]
        var url = new Uri(source.RunUrl);
        var segments = url.AbsolutePath.Trim('/').Split('/');
        if (segments.Length < 5 || segments[2] != "actions" || segments[3] != "runs"
            || !long.TryParse(segments[4], NumberStyles.None, CultureInfo.InvariantCulture, out var runId) || runId < 1)
            return ("run-url-not-recognised", null, null, null);
        var (owner, repo) = (segments[0], segments[1]);
        if (!string.Equals($"{owner}/{repo}", bound.Repository, StringComparison.OrdinalIgnoreCase))
            return ("repository-differs", null, null, null);

        if (await PromiseWorld.GitHubHostAsync(settings, workspaceId, ct) is not { } host)
            return ("github-not-configured", null, null, null);
        // The run must be on the GitHub this workspace reads: a page on another host proves nothing here.
        if (!string.Equals(host, url.IsDefaultPort ? url.IdnHost : $"{url.IdnHost}:{url.Port}", StringComparison.OrdinalIgnoreCase))
            return ("run-url-host-differs", null, null, null);

        // The project must still follow the repository, and the commit must be one the result can be shown for.
        var repositoryKey = PromiseWorld.RepositoryKey(host, bound.Repository);
        var tracked = await importDb.TrackedRefs.AsNoTracking()
            .FirstOrDefaultAsync(t => t.ProjectId == projectId && t.Host == host && t.Repository == bound.Repository, ct);
        if (tracked is null && !await importDb.FactLinks.AsNoTracking().AnyAsync(l => l.ProjectId == projectId && l.Host == host && l.Repository == bound.Repository, ct))
            return ("repository-not-followed", null, null, repositoryKey);
        if ((source.CommitSha ?? tracked?.CommitSha) is not { } commit)
            return ("no-tracked-commit", null, null, repositoryKey);
        var commitIsTracked = tracked?.CommitSha == commit;

        var connection = await gitHub.ConnectAsync(workspaceId, ct);
        if (connection.Value is not { } connected)
            return ("github-unavailable", null, commit, repositoryKey);

        var run = await connected.GetWorkflowRunAsync(owner, repo, runId, ct);
        if (run.Value is not { } found)
            return (Failure(run.Failure, "run-not-found"), null, commit, repositoryKey);

        // GitHub answers a renamed repository under its old name; the run then belongs to another name than the obligation's.
        if (found.Repository is null)
            return ("run-repository-unknown", null, commit, repositoryKey);
        if (found.Repository != bound.Repository)
            return ("run-repository-differs", null, commit, repositoryKey);
        if (!WorkflowMatches(bound.Workflow, found))
            return ("workflow-differs", null, commit, repositoryKey);
        if (found.HeadSha != commit)
            return ("commit-differs", null, commit, repositoryKey);
        if (found.Status != "completed" || found.Conclusion is not { } conclusion)
            return ("run-not-completed", null, commit, repositoryKey);

        string result;
        if (bound.Job is { } jobName)
        {
            var jobs = await connected.GetWorkflowRunJobsAsync(owner, repo, runId, ct);
            if (jobs.Value is not { } listed)
                return (Failure(jobs.Failure, "jobs-unavailable"), null, commit, repositoryKey);
            var named = listed.Jobs.Where(j => j.Name == jobName).ToList();
            if (named.Count == 0)
                return (listed.Incomplete ? "jobs-incomplete" : "job-not-found", null, commit, repositoryKey);
            // Every job of that name counts when the list is whole: a matrix gives several.
            if (listed.Incomplete)
                return ("jobs-incomplete", null, commit, repositoryKey);
            if (named.Any(j => j.Status != "completed" || j.Conclusion is null))
                return ("job-not-completed", null, commit, repositoryKey);

            if (conclusion == "success" && named.All(j => j.Conclusion == "success"))
                result = RunResult.Passed;
            else if (named.Any(j => j.Conclusion is "failure" or "timed_out"))
                (result, conclusion) = (RunResult.Failed, named.First(j => j.Conclusion is "failure" or "timed_out").Conclusion!);
            // The named job succeeded and the run did not: another job decided the run. That is neither a pass nor a failure of this plan.
            else if (named.All(j => j.Conclusion == "success"))
                return ("run-not-success", null, commit, repositoryKey);
            else
                return ("job-not-conclusive", null, commit, repositoryKey);
        }
        else if (conclusion == "success")
            result = RunResult.Passed;
        else if (conclusion is "failure" or "timed_out")
            result = RunResult.Failed;
        else
            return ("run-not-conclusive", null, commit, repositoryKey);

        // What the caller says must be what GitHub says.
        if (result != request.Result)
            return ("result-differs", null, commit, repositoryKey);

        return (null, new Read(found.Repository, runId, commit, commitIsTracked, tracked?.RefName, found.Path ?? found.Name ?? bound.Workflow, bound.Job, conclusion, result),
            commit, repositoryKey);
    }

    private static string Failure(GitHubFailure? failure, string notFound) => failure switch
    {
        GitHubFailure.NotFound => notFound,
        GitHubFailure.Unauthorized or GitHubFailure.Forbidden => "github-access-refused",
        GitHubFailure.RateLimited => "github-rate-limited",
        _ => "github-unavailable",
    };

    /// <summary>
    /// A workflow is named by its file (<c>ci.yml</c>, or a path that ends in the file) or by
    /// its name. A value that ends in <c>.yml</c> or <c>.yaml</c> is a file and is compared with
    /// the path GitHub gives; any other value is compared with the workflow's name. A workflow
    /// name that looks like a file name therefore never stands in for a file.
    /// </summary>
    internal static bool WorkflowMatches(string wanted, GitHubWorkflowRun run)
    {
        var isFile = wanted.EndsWith(".yml", StringComparison.OrdinalIgnoreCase) || wanted.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase);
        if (!isFile)
            return run.Name == wanted;
        if (run.Path is not { } path)
            return false;
        return wanted.Contains('/', StringComparison.Ordinal)
            ? path == wanted
            : path[(path.LastIndexOf('/') + 1)..] == wanted;
    }
}
