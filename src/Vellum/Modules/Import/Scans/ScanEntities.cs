using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Vellum.Modules.Import.Scans;

public static class ScanStatus
{
    public const string Queued = "queued";
    public const string Running = "running";
    /// <summary>The scan starts again by itself at <c>RetryAt</c>: GitHub's rate limit is used up, or an unexpected error is retried.</summary>
    public const string Waiting = "waiting";
    public const string Completed = "completed";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";

    /// <summary>The statuses in which a worker has, or will take, the scan.</summary>
    public static readonly string[] Active = [Queued, Running, Waiting];
}

/// <summary>What a scan may conclude, from the line of history it read.</summary>
public static class ScanKind
{
    /// <summary>The scan read the tracked ref of its repository. It may propose removals, mark facts stale and be decided.</summary>
    public const string Tracking = "tracking";
    /// <summary>The scan read another ref or a bare commit. Its proposals are a preview: nothing is decided, marked or superseded.</summary>
    public const string Comparison = "comparison";
}

/// <summary>
/// One scan of one repository for one project. The row is the job, its checkpoint and its
/// report: a worker that takes it over reads the commit SHA and the file list from here and
/// goes on where the last one stopped.
/// </summary>
public class ScanEntity
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public Guid WorkspaceId { get; set; }
    /// <summary>The host of the repository's web pages, for example <c>github.com</c>.</summary>
    public string Host { get; set; } = null!;
    /// <summary>
    /// <c>owner/name</c>, in lower case. When GitHub answers with another name for the same
    /// repository ID (a rename), the scan and every row of the repository take the new name.
    /// </summary>
    public string Repository { get; set; } = null!;
    /// <summary>GitHub's numeric ID of the repository. It does not change when the repository is renamed. Null until the scan asked GitHub.</summary>
    public long? RepositoryId { get; set; }
    /// <summary>The name as GitHub writes it, with its case. For display only.</summary>
    public string? RepositoryName { get; set; }
    /// <summary>The ref the caller asked for. Null means the default ref of the workspace or of the repository.</summary>
    public string? RequestedRef { get; set; }
    /// <summary>
    /// The name of the ref the scan read: the requested ref, else the workspace's default ref,
    /// else the repository's default branch. Null for a scan of a bare commit SHA, and for a
    /// scan made before refs were recorded that asked for no ref.
    /// </summary>
    public string? ResolvedRef { get; set; }
    /// <summary>Set once, when the ref is resolved. Every later read of the scan uses it.</summary>
    public string? CommitSha { get; set; }
    /// <summary>The number of the pull request the caller asked for. Such a scan reads the head commit of the pull request and names no ref.</summary>
    public int? PullRequestNumber { get; set; }
    /// <summary>A JSON <see cref="ScanPullRequest"/>: what GitHub said of the pull request when the scan pinned its head commit. Null until then.</summary>
    public string? PullRequest { get; set; }
    public bool? RepositoryIsPrivate { get; set; }
    public string[] IncludePaths { get; set; } = [];
    public string[] ExcludePaths { get; set; } = [];
    /// <summary>The <c>import.*</c> settings as they were when the scan was asked for (<see cref="ScanLimits"/>).</summary>
    public string Limits { get; set; } = null!;
    public string Status { get; set; } = ScanStatus.Queued;
    /// <summary>True when every file of the commit has a row in <c>scan_files</c>.</summary>
    public bool TreeListed { get; set; }
    public int FilesListed { get; set; }
    public int FilesRead { get; set; }
    public int FilesSkipped { get; set; }
    /// <summary>The files whose content the extractors read in full. Only these count as looked at.</summary>
    public int FilesExtracted { get; set; }
    public long BytesRead { get; set; }
    /// <summary>True when a bound was reached or a file could not be read. It never goes back to false.</summary>
    public bool Partial { get; set; }
    /// <summary>True only for a completed scan that no limit and no failure cut short. It says nothing about the scope.</summary>
    public bool IsComplete { get; set; }
    /// <summary>
    /// Null when the extraction left out nothing that the file states do not show. Else
    /// <c>limit</c> or <c>failed</c>: facts are missing for a reason no single file explains,
    /// so no linked fact counts as covered.
    /// </summary>
    public string? ExtractionGap { get; set; }
    /// <summary>A JSON <see cref="LinkCoverage"/>: how many accepted facts this scan could judge, and why not the others. Null until the scan completes.</summary>
    public string? Coverage { get; set; }
    /// <summary>Links that the scan moved to a new fact key after the extraction rules changed.</summary>
    public int Relinked { get; set; }
    /// <summary>Links that the scan could not move by the exact rule. Each has a <c>relink</c> proposal.</summary>
    public int NeedsReview { get; set; }
    /// <summary>A JSON list of <see cref="ScanNote"/>, at most <see cref="ScanNotes.Max"/>.</summary>
    public string Notes { get; set; } = "[]";
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTimeOffset? RetryAt { get; set; }
    public string RequestedBy { get; set; } = null!;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    /// <summary>How many times a worker took the scan since it was queued or resumed.</summary>
    public int Attempts { get; set; }
    /// <summary>Runs in a row that an unexpected error stopped. An orderly end sets it to 0.</summary>
    public int InternalFailures { get; set; }
    /// <summary>Runs in a row whose worker stopped renewing the lease. An orderly end sets it to 0.</summary>
    public int Crashes { get; set; }
    /// <summary>How many times a person resumed the scan.</summary>
    public int ManualResumes { get; set; }
    /// <summary>When a worker last took the scan. The queue serves the workspace that ran least recently.</summary>
    public DateTimeOffset? ClaimedAt { get; set; }
    public string? LeaseOwner { get; set; }
    /// <summary>On the clock of the database, which every worker shares.</summary>
    public DateTimeOffset? LeaseExpiresAt { get; set; }
    /// <summary>
    /// The fencing token: it grows by one each time a worker takes the scan. Every write of a
    /// run names the token it was given, so a run that lost the scan writes nothing.
    /// </summary>
    public long LeaseToken { get; set; }
    public bool CancelRequested { get; set; }
    /// <summary>Who made this scan's ref the tracked ref of the repository, when a person did so with <c>track</c>.</summary>
    public string? TrackedBy { get; set; }
    public DateTimeOffset? TrackedAt { get; set; }

    /// <summary>True when the caller asked for a commit SHA and no ref: such a scan follows no line of history.</summary>
    public bool IsBareCommit() => RequestedRef is { Length: 40 } requested && requested.All(char.IsAsciiHexDigit);

    /// <summary>True for a scan of the head commit of a pull request. Like a bare commit, it follows no line of history.</summary>
    public bool IsPullRequest() => PullRequestNumber is not null;
}

/// <summary>
/// The provenance of a pull request scan: the pull request as GitHub described it when the
/// scan pinned its head commit. Both SHAs are immutable; the title, the state and the
/// names are untrusted text, shown as text. The scan reads <see cref="HeadSha"/> and writes
/// nothing to GitHub.
/// </summary>
/// <param name="State"><c>open</c> or <c>closed</c>. A merged pull request is closed and has <paramref name="Merged"/> set.</param>
/// <param name="HeadRepository">The <c>owner/name</c> of the head repository. Null when GitHub names none (a deleted fork).</param>
/// <param name="FromFork">True when the head repository is not the repository of the scan. The scan then read the head commit through the repository of the scan.</param>
public sealed record ScanPullRequest(
    int Number, string? Title, string State, bool Merged, string BaseRef, string BaseSha, string HeadRef, string HeadSha,
    string? HeadRepository, bool FromFork)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static ScanPullRequest? FromJson(string? json) => json is null ? null : JsonSerializer.Deserialize<ScanPullRequest>(json, Json);
}

/// <summary>
/// One file of the commit, as the scan listed it. Every file of the tree has a row, so the
/// scan can say for any path whether it looked at it: <c>read</c> and extracted, or not read
/// with the reason.
/// </summary>
public class ScanFileEntity
{
    public const string Pending = "pending";
    /// <summary>The content was fetched and cleared. When the scan is completed, it was also given to the extractors.</summary>
    public const string Read = "read";
    /// <summary>The content was not read, or was read and not used. <see cref="Reason"/> says why.</summary>
    public const string Skipped = "skipped";

    /// <summary>The longest path a scan records. A longer path gets a note and no row.</summary>
    public const int MaxPath = 1024;

    public Guid ScanId { get; set; }
    /// <summary>SHA-256 of <see cref="Path"/>, in hex: the key, so a long path is never part of an index.</summary>
    public string PathHash { get; set; } = null!;
    public string Path { get; set; } = null!;
    public string BlobSha { get; set; } = null!;
    public long Size { get; set; }
    public string State { get; set; } = Pending;
    /// <summary>Why a skipped file was not read: a note code, or one of the codes of <see cref="CoverageGap"/>.</summary>
    public string? Reason { get; set; }
}

/// <summary>The key form of a text of any length: its SHA-256 in lower-case hex. The same as <c>encode(sha256(convert_to(t, 'UTF8')), 'hex')</c> in the database.</summary>
public static class KeyHash
{
    public static string Of(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}

/// <summary>Why a scan did not look at a file, in the four classes a person can act on.</summary>
public static class CoverageGap
{
    /// <summary>The scope, the exclude globs or the path filter left the file out.</summary>
    public const string Scope = "scope";
    /// <summary>A bound of the scan or of the extraction left the file out.</summary>
    public const string Limit = "limit";
    /// <summary>The content is a secret, is not text, or is dense with credentials. It is not kept.</summary>
    public const string Withheld = "withheld";
    /// <summary>The file could not be fetched, verified or parsed.</summary>
    public const string Failed = "failed";

    public const string OutOfScope = "out-of-scope";
    public const string ExcludedGlob = "excluded-glob";
    public const string NotRelevant = "not-relevant";
    public const string ShaMismatch = "blob-sha-mismatch";
    public const string CredentialDense = "credential-dense";
    public const string ParseFailure = "parse-failure";
    public const string FactRefused = "fact-refused";

    /// <summary>The class of a skip reason.</summary>
    public static string Of(string? reason) => reason switch
    {
        OutOfScope or ExcludedGlob or NotRelevant or "excluded-directory" or "lock-file" or "minified"
            or "binary-extension" or "invalid-path" or "duplicate-path" or "template-not-evaluated" => Scope,
        "secret-file" or "binary-content" or CredentialDense => Withheld,
        not null when reason.StartsWith("limit:", StringComparison.Ordinal) => Limit,
        _ => Failed,
    };
}

/// <summary>How many accepted facts of the repository a scan could judge, and why it could not judge the others.</summary>
public sealed record LinkCoverage(int Covered, int Scope, int Limit, int Withheld, int Failed)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static LinkCoverage? FromJson(string? json) => json is null ? null : JsonSerializer.Deserialize<LinkCoverage>(json, Json);
}

/// <summary>
/// The bytes of one git blob, kept so that a resumed scan and the source view do not ask
/// GitHub again. The key is the blob SHA, so the content can never change.
/// </summary>
public class BlobEntity
{
    public Guid WorkspaceId { get; set; }
    public string BlobSha { get; set; } = null!;
    /// <summary>Null when the content is withheld (<see cref="WithheldReason"/>). Withheld content is never stored.</summary>
    public byte[]? Content { get; set; }
    public long Size { get; set; }
    /// <summary>For example <c>secret-file</c>: the content declares a secret, so its bytes are not kept.</summary>
    public string? WithheldReason { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// One unit of <c>usage.maxScansPerDay</c>: a scan that was started, or a resume that has to
/// ask GitHub again. It has no link to the scan row, so deleting a project gives no units back.
/// </summary>
public class ScanChargeEntity
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid ScanId { get; set; }
    public DateTimeOffset ChargedAt { get; set; }
}

/// <summary>
/// The line of history that the accepted facts of one repository follow in one project. The
/// first accepted decision sets it; <c>track</c> changes it. Only a scan of this ref may
/// propose removals, mark facts stale or be decided.
/// </summary>
public class TrackedRefEntity
{
    public Guid ProjectId { get; set; }
    public string Host { get; set; } = null!;
    public string Repository { get; set; } = null!;
    public long? RepositoryId { get; set; }
    /// <summary>
    /// The ref name. Empty for links accepted before refs were recorded from a scan that
    /// named none: the next scan that names no ref gives it the name it resolves.
    /// </summary>
    public string RefName { get; set; } = null!;
    /// <summary>The commit of the last accepted decision.</summary>
    public string? CommitSha { get; set; }
    public Guid? ScanId { get; set; }
    public string SetBy { get; set; } = null!;
    public DateTimeOffset SetAt { get; set; }
}

/// <summary>The bounds of one scan, taken from the workspace's effective <c>import.*</c> settings.</summary>
public sealed record ScanLimits(int MaxFiles, long MaxFileBytes, long MaxTotalBytes, IReadOnlyList<string> ExcludeGlobs)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static ScanLimits FromJson(string json) => JsonSerializer.Deserialize<ScanLimits>(json, Json)!;
}

/// <param name="Code">A fixed code such as <c>secret-file</c> or <c>limit:files</c>. Never repository content.</param>
/// <param name="Path">A path of the repository, or "" when the note is about the whole scan.</param>
/// <param name="Line">0 when the note is about the whole file.</param>
/// <param name="Message">A fixed sentence for the code.</param>
public sealed record ScanNote(string Code, string Path, int Line, string Message);

public static class ScanNotes
{
    public const int Max = 500;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static List<ScanNote> Read(string json) => JsonSerializer.Deserialize<List<ScanNote>>(json, Json) ?? [];

    public static string Write(IEnumerable<ScanNote> notes)
    {
        var distinct = notes.Distinct().ToList();
        if (distinct.Count > Max)
        {
            distinct = distinct[..(Max - 1)];
            distinct.Add(Of("notes-truncated", ""));
        }
        return JsonSerializer.Serialize(distinct, Json);
    }

    public static ScanNote Of(string code, string path, int line = 0) => new(code, path, line, Message(code));

    /// <summary>One sentence for a note code. The codes are those of the scan and of the extractors.</summary>
    public static string Message(string code) => code switch
    {
        "secret-file" => "The file looks like it holds secrets. It was not read.",
        "excluded-directory" => "The directory holds dependencies or build output. It was not read.",
        "lock-file" => "A lock file. It was not read.",
        "minified" => "A minified or generated file. It was not read.",
        "binary-extension" => "A binary file. It was not read.",
        "binary-content" => "The file is not text. It was not read.",
        "invalid-path" => "The path is not a usable repository path. The file was not read.",
        "duplicate-path" => "The path appears twice. The second copy was not read.",
        "template-not-evaluated" => "The file is a template. Vellum does not evaluate templates, so it was not read.",
        "limit:files" => "The scan reached import.maxFiles. This file and the files after it in path order were not read.",
        "limit:total-bytes" => "The scan reached import.maxTotalBytes. This file and the files after it in path order were not read.",
        "limit:file-bytes" => "The file is larger than import.maxFileBytes. It was not read.",
        "limit:facts" => "The scan reached the most facts one snapshot holds. Later facts were left out.",
        "limit:notes" => "More notes exist than one extraction keeps.",
        "blob-missing" => "GitHub did not return this file. It was not read.",
        "notes-truncated" => "More notes exist than one scan keeps.",
        "tree-truncated" => "GitHub cut the file list of this repository. No file was read.",
        "path-too-long" => "The path is longer than a scan records. The file was not read.",
        CoverageGap.ShaMismatch => "The bytes GitHub returned are not the file of this commit. They were not used.",
        CoverageGap.CredentialDense => "Many lines of the file look like credentials. It was not kept.",
        CoverageGap.FactRefused => "A fact of this file holds a control character or is longer than a fact may be. It was left out.",
        "pull-request-fork" => "The pull request comes from another repository. GitHub serves its head commit through this repository, and the scan read it from here.",
        "pull-request-closed" => "The pull request is closed. The scan read its head commit as it was when the pull request closed.",
        "pull-request-merged" => "The pull request is merged. The scan read its head commit, which is not the merge commit.",
        "relink-needs-review" => "The extraction rules changed, and an accepted fact could not be matched to a new one by the exact rule. Review its relink proposal.",
        _ when code.StartsWith("limit:", StringComparison.Ordinal) => "A bound of the extraction was reached. Part of this file was left out.",
        _ when code.StartsWith("extractor-failure", StringComparison.Ordinal) => "An extractor failed. Its facts are missing from this scan.",
        _ when code.StartsWith("credential-like-value", StringComparison.Ordinal) => "A value looked like a credential and was left out.",
        _ when code.StartsWith("value-too-long", StringComparison.Ordinal) => "A value was too long and was left out.",
        _ when code.StartsWith("attribute:", StringComparison.Ordinal) => "Two sources gave different values for one attribute. The first was kept.",
        _ => "The extractor could not read this part of the file.",
    };
}
