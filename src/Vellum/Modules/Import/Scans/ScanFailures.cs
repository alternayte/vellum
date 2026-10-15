using System.Collections.Frozen;
using System.Globalization;
using Vellum.Modules.GitHub;
using Vellum.Shared;

namespace Vellum.Modules.Import.Scans;

/// <summary>Why a scan stopped: a fixed code, and one or two sentences that say what to do.</summary>
public sealed record ScanError(string Code, string Message);

/// <summary>Ends the run of a scan. Thrown inside the worker and never leaves it.</summary>
internal sealed class ScanStop : Exception
{
    private ScanStop(ScanStatus status, ScanError? error, DateTimeOffset? retryAt) : base(error?.Code ?? EnumText.Of(status))
    {
        Status = status;
        Error = error;
        RetryAt = retryAt;
    }

    public ScanStatus Status { get; }
    public ScanError? Error { get; }
    public DateTimeOffset? RetryAt { get; }

    public static ScanStop Failed(ScanError error) => new(ScanStatus.Failed, error, null);

    public static ScanStop Waiting(DateTimeOffset retryAt) => new(ScanStatus.Waiting, null, retryAt);

    public static ScanStop Cancelled() => new(ScanStatus.Cancelled, null, null);

    /// <summary>The scan waits and is taken again at <paramref name="retryAt"/>, with the error visible until then.</summary>
    public static ScanStop Retry(ScanError error, DateTimeOffset retryAt) => new(ScanStatus.Waiting, error, retryAt);
}

/// <summary>
/// The text of every scan failure. The sentences are fixed: none holds repository content,
/// a token or a response body.
/// </summary>
internal static class ScanFailures
{
    public const string ResumeHint = " Then resume the scan.";

    /// <summary>
    /// The codes of a failure that a resume cannot cure: the scan would ask the same of GitHub
    /// or of the project and get the same answer. Each message says to start a new scan.
    /// </summary>
    public static readonly FrozenSet<string> Final = FrozenSet.ToFrozenSet(
    [
        "tree_truncated", "too_large", "project_not_found", "ref_not_found", "commit_not_found",
        "pull_request_not_found", "pull_request_head_unreadable", "push_interrupted",
    ], StringComparer.Ordinal);

    /// <summary>True for a failed or cancelled scan whose failure a resume can cure.</summary>
    public static bool CanResume(ScanStatus status, string? errorCode) =>
        status is ScanStatus.Failed or ScanStatus.Cancelled && (errorCode is null || !Final.Contains(errorCode));

    /// <summary>A push of <c>vellum scan</c> whose request ended before its Snapshot was written. The instance holds no file of it to go on with.</summary>
    public static readonly ScanError PushInterrupted = new("push_interrupted",
        "The push stopped before its Snapshot was written. Run vellum scan again.");

    public static readonly ScanError TreeTruncated = new("tree_truncated",
        "GitHub cut the file list of this repository, because it is too large for one request. No file was read, so this scan has no result. " +
        "Vellum cannot scan a repository of this size.");

    public static readonly ScanError ProjectGone = new("project_not_found",
        "The project of this scan no longer exists. The scan has no model to compare with.");

    public static readonly ScanError Internal = new("internal_error",
        "An unexpected error stopped the scan on every automatic attempt. Report the scan ID to the operator of this instance. You can resume the scan.");

    public static readonly ScanError InternalRetry = new("internal_error",
        "An unexpected error stopped the scan. It starts again by itself at the time in retryAt.");

    public static readonly ScanError WorkerCrashed = new("worker_crashed",
        "The worker that ran this scan stopped without a word, twice in a row. The scan is not started again by itself. Report the scan ID to the operator of this instance. You can resume the scan.");

    public static readonly ScanError ModelBusy = new("model_busy",
        "The project's model changed each time the scan compared its result with it. Nothing was written. Resume the scan when the model is not being edited.");

    public static readonly ScanError StoredContentMissing = new("stored_content_missing",
        "The stored content of a file of this scan is missing, so the scan has no result. Resume the scan: it fetches the file again at the same commit.");

    public static readonly ScanError RepositoryRenamed = new("scan_in_progress",
        "GitHub knows this repository under another name, and a scan of that name is already queued or running for this project. Wait for it, or cancel it.");

    public static readonly ScanError RateLimitNeverCleared = new("rate_limited",
        "GitHub's rate limit stayed used up through many retries. Resume the scan later. A workspace token (github:token) has a higher limit than anonymous access.");

    public static ScanError PullRequestNotFound(int number, bool hasToken) => new("pull_request_not_found", hasToken
        ? string.Create(CultureInfo.InvariantCulture, $"The repository has no pull request #{number} that the workspace's token can see. Start a new scan with another number.")
        : string.Create(CultureInfo.InvariantCulture, $"The repository has no pull request #{number} that anonymous access can see. Check the number. If the repository is private, save a token in the workspace secrets (github:token)."));

    public static ScanError PullRequestHeadUnreadable(int number) => new("pull_request_head_unreadable",
        string.Create(CultureInfo.InvariantCulture, $"Pull request #{number} comes from another repository, and GitHub does not serve its head commit through this repository. Vellum reads only the repository of the scan, so nothing was read. Push the branch to this repository and scan it as a ref."));

    public static ScanError NotConnected(GitHubFailure failure, HostWords? host = null) =>
        Describe(failure, hasToken: false, stage: "connect", reference: null, host);

    /// <summary>How a failure names the host, its token and its setting. GitHub when none is given.</summary>
    public sealed record HostWords(string Name, string Secret, string Field)
    {
        public static readonly HostWords GitHub = new("GitHub", "github:token", "github.apiBaseUrl");
    }

    /// <param name="stage"><c>repository</c>, <c>ref</c>, <c>tree</c> or <c>blob</c>: what was being read.</param>
    public static ScanError Describe(GitHubFailure failure, bool hasToken, string stage, string? reference, HostWords? host = null)
    {
        var (n, s, f) = host ?? HostWords.GitHub;
        return failure switch
        {
            GitHubFailure.Unauthorized => new("unauthorized",
                $"{n} refused the workspace's token: it is expired, revoked or mistyped. Save a new token in the workspace secrets ({s})." + ResumeHint),
            GitHubFailure.Forbidden => new("forbidden",
                $"The token is valid, and {n} does not allow it to read this repository. Give the token read access to the repository's contents." + ResumeHint),
            // Only a ref that the caller typed is repeated. A default branch name is repository text.
            GitHubFailure.NotFound when stage == "ref" => new("ref_not_found", reference is null
                ? "The default ref of the repository does not resolve to a commit. Start a new scan and name a ref."
                : string.Create(CultureInfo.InvariantCulture, $"The repository has no branch, tag or commit named '{reference}'. Start a new scan with another ref.")),
            GitHubFailure.NotFound when stage == "tree" => new("commit_not_found",
                $"{n} no longer has the commit this scan is pinned to. Start a new scan."),
            GitHubFailure.NotFound when hasToken => new("not_found",
                $"{n} has no repository with this name that the workspace's token can see. Check the name, and check that the token has access to the repository." + ResumeHint),
            GitHubFailure.NotFound => new("not_found",
                $"{n} has no public repository with this name. Check the name. If the repository is private, save a token in the workspace secrets ({s})." + ResumeHint),
            GitHubFailure.TooLarge => new("too_large",
                "The file list of this repository is larger than Vellum reads in one scan. Vellum cannot scan a repository of this size."),
            GitHubFailure.Truncated => host is null ? TreeTruncated : new("tree_truncated",
                $"{n} cut the file list of this repository. No file was read, so this scan has no result. Vellum cannot scan a repository of this size."),
            GitHubFailure.RateLimited => host is null ? RateLimitNeverCleared : new("rate_limited",
                $"{n}'s rate limit stayed used up through many retries. Resume the scan later. A workspace token ({s}) can have a higher limit than anonymous access."),
            GitHubFailure.Unavailable { Reason: "url_refused" } => new("url_refused",
                $"The instance policy refuses the workspace's {n} API base URL, or it is empty. Set {f} to an address the policy allows." + ResumeHint),
            GitHubFailure.Unavailable { Reason: "secret_unavailable" } => new("secret_unavailable",
                $"The stored token cannot be decrypted with the loaded master key. Save the token again ({s})." + ResumeHint),
            GitHubFailure.Unavailable { Reason: "token_saved_for_another_host" } => new("token_saved_for_another_host",
                $"The stored token was saved for another {n} host. Save it again for the current {f}." + ResumeHint),
            GitHubFailure.Unavailable { Reason: "invalid_setting" } => new("invalid_setting",
                $"The stored token or the {n} API base URL holds a character that cannot be sent. Nothing was sent. Save the token again ({s}) and check {f}." + ResumeHint),
            GitHubFailure.Unavailable { Reason: "redirect_refused" } => new("redirect_refused",
                $"{n} redirected the request to another host, which Vellum does not follow. Set {f} to the final address." + ResumeHint),
            GitHubFailure.Unavailable { Reason: "timeout" } => new("network",
                $"{n} did not answer in time. What was read so far is kept. Resume the scan."),
            GitHubFailure.Unavailable { Reason: "server_error" } => new("network",
                $"{n} reported a server error. What was read so far is kept. Resume the scan later."),
            GitHubFailure.Unavailable { Reason: "network" } => new("network",
                $"No connection to {n} could be made. What was read so far is kept. Resume the scan when the connection works."),
            _ => new("unexpected_response",
                $"The answer was not a {n} API answer. Check {f}." + ResumeHint),
        };
    }
}
