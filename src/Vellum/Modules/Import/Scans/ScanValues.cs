using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Vellum.Shared;

namespace Vellum.Modules.Import.Scans;

public static class ScanSnapshots
{
    /// <summary>
    /// The snapshots of a project whose scans read a ref: no pull request and no bare commit.
    /// Only such a snapshot can stand for the newest state of a repository; a pull request or
    /// a commit follows no line of history. The database filters them.
    /// </summary>
    public static IQueryable<SnapshotEntity> OfRefs(AppDbContext db, Guid projectId) =>
        db.ImportSnapshots.Where(s => s.ProjectId == projectId && db.Scans.Any(c => c.Id == s.ScanId && c.PullRequestNumber == null
            && !(c.RequestedRef != null && System.Text.RegularExpressions.Regex.IsMatch(c.RequestedRef, "^[0-9A-Fa-f]{40}$"))));
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
