using System.Text.Json;

namespace Vellum.Modules.Import.Scans;

public static class ScanStatus
{
    public const string Queued = "queued";
    public const string Running = "running";
    /// <summary>GitHub's rate limit is used up. The scan starts again by itself at <c>RetryAt</c>.</summary>
    public const string Waiting = "waiting";
    public const string Completed = "completed";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";

    /// <summary>The statuses in which a worker has, or will take, the scan.</summary>
    public static readonly string[] Active = [Queued, Running, Waiting];
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
    /// <summary><c>owner/name</c>, in lower case.</summary>
    public string Repository { get; set; } = null!;
    /// <summary>The ref the caller asked for. Null means the default ref of the workspace or of the repository.</summary>
    public string? RequestedRef { get; set; }
    /// <summary>Set once, when the ref is resolved. Every later read of the scan uses it.</summary>
    public string? CommitSha { get; set; }
    public bool? RepositoryIsPrivate { get; set; }
    public string[] IncludePaths { get; set; } = [];
    public string[] ExcludePaths { get; set; } = [];
    /// <summary>The <c>import.*</c> settings as they were when the scan was asked for (<see cref="ScanLimits"/>).</summary>
    public string Limits { get; set; } = null!;
    public string Status { get; set; } = ScanStatus.Queued;
    /// <summary>True when the file list of the commit is stored in <c>scan_files</c>.</summary>
    public bool TreeListed { get; set; }
    public int FilesListed { get; set; }
    public int FilesRead { get; set; }
    public int FilesSkipped { get; set; }
    public long BytesRead { get; set; }
    /// <summary>True when a bound was reached or a file could not be read. It never goes back to false.</summary>
    public bool Partial { get; set; }
    /// <summary>True only for a completed scan that read everything in its scope.</summary>
    public bool IsComplete { get; set; }
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
    /// <summary>How many times a worker took the scan.</summary>
    public int Attempts { get; set; }
    public string? LeaseOwner { get; set; }
    public DateTimeOffset? LeaseExpiresAt { get; set; }
    public bool CancelRequested { get; set; }
}

/// <summary>A file of the commit that the scan reads. Files that the filters refuse get no row.</summary>
public class ScanFileEntity
{
    public const string Pending = "pending";
    public const string Read = "read";
    public const string Skipped = "skipped";

    public Guid ScanId { get; set; }
    public string Path { get; set; } = null!;
    public string BlobSha { get; set; } = null!;
    public long Size { get; set; }
    public string State { get; set; } = Pending;
    /// <summary>Why a skipped file was not read: a note code.</summary>
    public string? Reason { get; set; }
}

/// <summary>
/// The bytes of one git blob, kept so that a resumed scan and the source view do not ask
/// GitHub again. The key is the blob SHA, so the content can never change.
/// </summary>
public class BlobEntity
{
    public Guid WorkspaceId { get; set; }
    public string BlobSha { get; set; } = null!;
    /// <summary>Null when the content was read once and is withheld (<see cref="WithheldReason"/>).</summary>
    public byte[]? Content { get; set; }
    public long Size { get; set; }
    /// <summary>For example <c>secret-file</c>: the file declares a secret, so its bytes are not kept.</summary>
    public string? WithheldReason { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
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
        _ when code.StartsWith("limit:", StringComparison.Ordinal) => "A bound of the extraction was reached. Part of this file was left out.",
        _ when code.StartsWith("extractor-failure", StringComparison.Ordinal) => "An extractor failed. Its facts are missing from this scan.",
        _ when code.StartsWith("credential-like-value", StringComparison.Ordinal) => "A value looked like a credential and was left out.",
        _ when code.StartsWith("value-too-long", StringComparison.Ordinal) => "A value was too long and was left out.",
        _ when code.StartsWith("attribute:", StringComparison.Ordinal) => "Two sources gave different values for one attribute. The first was kept.",
        _ => "The extractor could not read this part of the file.",
    };
}
