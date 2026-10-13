using System.Globalization;
using Vellum.Modules.GitHub;

namespace Vellum.Modules.Import.Scans;

/// <summary>Why a scan stopped: a fixed code, and one or two sentences that say what to do.</summary>
public sealed record ScanError(string Code, string Message);

/// <summary>Ends the run of a scan. Thrown inside the worker and never leaves it.</summary>
internal sealed class ScanStop : Exception
{
    private ScanStop(string status, ScanError? error, DateTimeOffset? retryAt) : base(error?.Code ?? status)
    {
        Status = status;
        Error = error;
        RetryAt = retryAt;
    }

    public string Status { get; }
    public ScanError? Error { get; }
    public DateTimeOffset? RetryAt { get; }

    public static ScanStop Failed(ScanError error) => new(ScanStatus.Failed, error, null);

    public static ScanStop Waiting(DateTimeOffset retryAt) => new(ScanStatus.Waiting, null, retryAt);

    public static ScanStop Cancelled() => new(ScanStatus.Cancelled, null, null);
}

/// <summary>
/// The text of every scan failure. The sentences are fixed: none holds repository content,
/// a token or a response body.
/// </summary>
internal static class ScanFailures
{
    public const string ResumeHint = " Then resume the scan.";

    public static readonly ScanError TreeTruncated = new("tree_truncated",
        "GitHub cut the file list of this repository, because it is too large for one request. No file was read, so this scan has no result. " +
        "Vellum cannot scan a repository of this size.");

    public static readonly ScanError ProjectGone = new("project_not_found",
        "The project of this scan no longer exists. The scan has no model to compare with.");

    public static readonly ScanError Internal = new("internal_error",
        "An unexpected error stopped the scan. Resume it. If it stops again, report the scan ID to the operator of this instance.");

    public static readonly ScanError RateLimitNeverCleared = new("rate_limited",
        "GitHub's rate limit stayed used up through many retries. Resume the scan later. A workspace token (github:token) has a higher limit than anonymous access.");

    public static ScanError NotConnected(GitHubFailure failure) => Describe(failure, hasToken: false, stage: "connect", reference: null);

    /// <param name="stage"><c>repository</c>, <c>ref</c>, <c>tree</c> or <c>blob</c>: what was being read.</param>
    public static ScanError Describe(GitHubFailure failure, bool hasToken, string stage, string? reference) => failure switch
    {
        GitHubFailure.Unauthorized => new("unauthorized",
            "GitHub refused the workspace's token: it is expired, revoked or mistyped. Save a new token in the workspace secrets (github:token)." + ResumeHint),
        GitHubFailure.Forbidden => new("forbidden",
            "The token is valid, and GitHub does not allow it to read this repository. Give the token read access to the repository's contents." + ResumeHint),
        // Only a ref that the caller typed is repeated. A default branch name is repository text.
        GitHubFailure.NotFound when stage == "ref" => new("ref_not_found", reference is null
            ? "The default ref of the repository does not resolve to a commit. Start a new scan and name a ref."
            : string.Create(CultureInfo.InvariantCulture, $"The repository has no branch, tag or commit named '{reference}'. Start a new scan with another ref.")),
        GitHubFailure.NotFound when stage == "tree" => new("commit_not_found",
            "GitHub no longer has the commit this scan is pinned to. Start a new scan."),
        GitHubFailure.NotFound when hasToken => new("not_found",
            "GitHub has no repository with this name that the workspace's token can see. Check the name, and check that the token has access to the repository." + ResumeHint),
        GitHubFailure.NotFound => new("not_found",
            "GitHub has no public repository with this name. Check the name. If the repository is private, save a token in the workspace secrets (github:token)." + ResumeHint),
        GitHubFailure.TooLarge => new("too_large",
            "The file list of this repository is larger than Vellum reads in one scan. Vellum cannot scan a repository of this size."),
        GitHubFailure.Truncated => TreeTruncated,
        GitHubFailure.RateLimited => RateLimitNeverCleared,
        GitHubFailure.Unavailable { Reason: "url_refused" } => new("url_refused",
            "The instance policy refuses the workspace's GitHub API base URL. Set github.apiBaseUrl to an address the policy allows." + ResumeHint),
        GitHubFailure.Unavailable { Reason: "secret_unavailable" } => new("secret_unavailable",
            "The stored token cannot be decrypted with the loaded master key. Save the token again (github:token)." + ResumeHint),
        GitHubFailure.Unavailable { Reason: "token_saved_for_another_host" } => new("token_saved_for_another_host",
            "The stored token was saved for another GitHub host. Save it again for the current github.apiBaseUrl." + ResumeHint),
        GitHubFailure.Unavailable { Reason: "redirect_refused" } => new("redirect_refused",
            "GitHub redirected the request to another host, which Vellum does not follow. Set github.apiBaseUrl to the final address." + ResumeHint),
        GitHubFailure.Unavailable { Reason: "timeout" } => new("network",
            "GitHub did not answer in time. What was read so far is kept. Resume the scan."),
        GitHubFailure.Unavailable { Reason: "server_error" } => new("network",
            "GitHub reported a server error. What was read so far is kept. Resume the scan later."),
        GitHubFailure.Unavailable { Reason: "network" } => new("network",
            "No connection to GitHub could be made. What was read so far is kept. Resume the scan when the connection works."),
        _ => new("unexpected_response",
            "The answer was not a GitHub API answer. Check github.apiBaseUrl." + ResumeHint),
    };
}
