using System.Globalization;
using System.Text.RegularExpressions;
using Vellum.Modules.GitHub;
using Vellum.Modules.Settings.Document;
using Vellum.Shared;

namespace Vellum.Modules.Promises.Checks;

public sealed record ExternalSourceRequest(string System, string RunUrl, string CommitSha, string? JobName, string Conclusion);

/// <param name="Result"><c>passed</c> or <c>failed</c>: what the caller says the external test showed.</param>
/// <param name="ArtifactDigest"><c>sha256:&lt;64 hex&gt;</c> of a test report, when the caller has one. Recorded as claimed.</param>
public sealed record RecordEvidenceRequest(string Result, ExternalSourceRequest Source, string? ArtifactDigest, int? Revision = null);

/// <summary>
/// A result of a test that ran outside Vellum, for a test-plan obligation. It counts only
/// when Vellum can check its identity: for GitHub Actions, the run is read from the
/// workspace's GitHub and must exist for the repository in the URL, be completed, be on the
/// claimed commit and have the claimed conclusion. Anything else is stored as a claim, and
/// the obligation stays <c>not-run</c> with the claim shown (SDD guarantee 11).
/// </summary>
public static partial class ExternalEvidence
{
    public const string GitHubActions = "github-actions";
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
        if (TextRule.Line("source.runUrl", source.RunUrl, TextRule.MaxPointer) is { } urlError)
            errors.Add(urlError);
        else if (!Uri.TryCreate(source.RunUrl, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps || url.UserInfo.Length > 0)
            errors.Add(new("source.runUrl", "The run URL is an https address with no user information"));
        if (source.CommitSha is null || !Sha().IsMatch(source.CommitSha))
            errors.Add(new("source.commitSha", "A commit SHA is 40 or 64 lower-case hex digits"));
        if (TextRule.Line("source.jobName", source.JobName) is { } jobError)
            errors.Add(jobError);
        if (source.Conclusion is null || !Word().IsMatch(source.Conclusion))
            errors.Add(new("source.conclusion", "A conclusion is one lower-case word, such as success or failure"));
        else if ((request.Result == RunResult.Passed) != (source.Conclusion == "success"))
            errors.Add(new("source.conclusion", "A passed result has the conclusion success, and a failed result has another conclusion"));
        if (request.ArtifactDigest is not null && !Digest().IsMatch(request.ArtifactDigest))
            errors.Add(new("artifactDigest", "An artifact digest is sha256: followed by 64 lower-case hex digits"));

        return errors;
    }

    public static async Task<CheckOutcome> AssessAsync(
        RecordEvidenceRequest request, Guid workspaceId, GitHubReader gitHub, SettingsResolver settings, CancellationToken ct)
    {
        var source = request.Source;
        var (reason, repository, runId) = source.System == GitHubActions
            ? await CheckGitHubRunAsync(source, workspaceId, gitHub, settings, ct)
            : ("system-not-checkable", null, null);
        var checkedByVellum = reason is null;

        var evidence = new EvidenceItem(
            EvidenceKind.External,
            Source: new ExternalSource(source.System, source.RunUrl, source.CommitSha, source.JobName, source.Conclusion),
            ArtifactDigest: request.ArtifactDigest,
            Identity: checkedByVellum ? EvidenceIdentity.Checked : EvidenceIdentity.Claimed,
            IdentityReason: reason,
            ClaimedResult: request.Result);

        var environment = $"{source.System}@{source.CommitSha}";
        if (!checkedByVellum)
        {
            return new CheckOutcome(
                RunResult.NotRun, environment,
                $"A claim of '{request.Result}' from {source.System} for commit {source.CommitSha}.",
                $"Vellum could not check the identity of this result ({reason}). It is stored as a claim and does not count as a result.",
                [evidence],
                ["Only the caller says that this test ran and what it showed."],
                CommitSha: source.CommitSha);
        }

        return new CheckOutcome(
            request.Result, environment,
            string.Create(CultureInfo.InvariantCulture,
                $"GitHub Actions run {runId} of {repository} at commit {source.CommitSha}, concluded '{source.Conclusion}'."),
            "Vellum read the run from GitHub: it exists for this repository, is completed, ran on this commit and has this conclusion.",
            [evidence],
            [
                "Vellum did not run this test. It checked only that the named run exists for the repository and the commit and concluded as claimed.",
                "Vellum did not check that the run executed the steps of this plan. The job name and the artifact digest are recorded as claimed.",
                "A passing bounded test is not proof for other inputs, commits or environments.",
            ],
            CommitSha: source.CommitSha);
    }

    /// <summary>Null when the run is what the caller said; else a fixed code that says why it is not.</summary>
    private static async Task<(string? Reason, string? Repository, long? RunId)> CheckGitHubRunAsync(
        ExternalSourceRequest source, Guid workspaceId, GitHubReader gitHub, SettingsResolver settings, CancellationToken ct)
    {
        // https://<host>/<owner>/<repo>/actions/runs/<number>[/...]
        var url = new Uri(source.RunUrl);
        var segments = url.AbsolutePath.Trim('/').Split('/');
        if (segments.Length < 5 || segments[2] != "actions" || segments[3] != "runs"
            || !long.TryParse(segments[4], NumberStyles.None, CultureInfo.InvariantCulture, out var runId) || runId < 1)
            return ("run-url-not-recognised", null, null);
        var (owner, repo) = (segments[0], segments[1]);

        var workspace = await settings.GetWorkspaceAsync(workspaceId, ct);
        if (workspace.GithubApiBaseUrl is not SettingsUrl.Usable api)
            return ("github-not-configured", null, null);
        // The run must be on the GitHub this workspace reads: a page on another host proves nothing here.
        if (!string.Equals(WebHost(api.Url), url.IsDefaultPort ? url.IdnHost : $"{url.IdnHost}:{url.Port}", StringComparison.OrdinalIgnoreCase))
            return ("run-url-host-differs", null, null);

        var connection = await gitHub.ConnectAsync(workspaceId, ct);
        if (connection.Value is not { } connected)
            return ("github-unavailable", null, null);

        var run = await connected.GetWorkflowRunAsync(owner, repo, runId, ct);
        if (run.Value is not { } found)
        {
            return (run.Failure switch
            {
                GitHubFailure.NotFound => "run-not-found",
                GitHubFailure.Unauthorized or GitHubFailure.Forbidden => "github-access-refused",
                GitHubFailure.RateLimited => "github-rate-limited",
                _ => "github-unavailable",
            }, null, null);
        }

        if (found.HeadSha != source.CommitSha)
            return ("commit-differs", null, null);
        if (found.Status != "completed")
            return ("run-not-completed", null, null);
        if (found.Conclusion != source.Conclusion)
            return ("conclusion-differs", null, null);

        return (null, $"{owner}/{repo}".ToLowerInvariant(), runId);
    }

    /// <summary>The host of the repository pages for an API address: <c>github.com</c> for <c>api.github.com</c>, else the API host.</summary>
    private static string WebHost(Uri apiBaseUrl)
    {
        var host = apiBaseUrl.IdnHost.ToLowerInvariant();
        if (host == "api.github.com")
            return "github.com";
        return apiBaseUrl.IsDefaultPort ? host : string.Create(CultureInfo.InvariantCulture, $"{host}:{apiBaseUrl.Port}");
    }
}
