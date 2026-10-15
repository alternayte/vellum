using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Vellum.Modules.Import.Extraction;
using Vellum.Shared;

namespace Vellum.Modules.Import;

public enum ScanStatus
{
    Queued,
    Running,
    /// <summary>The scan starts again by itself at <c>RetryAt</c>: GitHub's rate limit is used up, or an unexpected error is retried.</summary>
    Waiting,
    Completed,
    Failed,
    Cancelled,
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
/// The kinds of <see cref="SourceEntity"/>: a closed set. A kind that this build cannot fetch
/// yet is refused when a Source of it is created, so a later fetcher adds no column and no
/// label. The API text is kebab case: <c>azure-devops</c>, <c>agent-push</c>, <c>model-file</c>.
/// </summary>
public enum SourceKind
{
    Github,
    Gitlab,
    Bitbucket,
    AzureDevops,
    Directory,
    Archive,
    AgentPush,
    ModelFile,
    Traces,
    Newrelic,
}

/// <summary>
/// A configured origin of architecture evidence for one project. Each completed scan of it is
/// one Snapshot. <see cref="Host"/> and <see cref="Locator"/> are the key the per-Source rows
/// (accepted facts, rejections, the tracked ref) are filed under: for a <c>github</c> Source
/// they are the web host and <c>owner/name</c> in lower case, so a citation reads
/// <c>github.com/acme/orders@&lt;sha&gt;:path:start-end</c>.
/// </summary>
public class SourceEntity
{
    public const int MaxName = 200;

    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public Guid WorkspaceId { get; set; }
    public SourceKind Kind { get; set; }
    /// <summary>What people call the Source. For display only; it can change.</summary>
    public string Name { get; set; } = null!;
    public string Host { get; set; } = null!;
    /// <summary>Where in <see cref="Host"/> the Source is: <c>owner/name</c> for a git host. It follows a rename of the repository.</summary>
    public string Locator { get; set; } = null!;
    /// <summary>A JSON object of the kind's connection details that are not part of the key. No credential is ever held here.</summary>
    public string Connection { get; set; } = "{}";
    public string CreatedBy { get; set; } = null!;
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>The <c>&lt;source&gt;</c> part of a citation <c>&lt;source&gt;@&lt;revision&gt;:path:start-end</c>.</summary>
    public string CitationName => $"{Host}/{Locator}";

    /// <summary>True for a kind whose Snapshots are commits: their revision is the commit SHA, else the Snapshot ID.</summary>
    public static bool IsGit(SourceKind kind) => kind is SourceKind.Github or SourceKind.Gitlab or SourceKind.Bitbucket or SourceKind.AzureDevops;
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
    /// <summary>The Source the scan reads. <see cref="Host"/> and <see cref="Repository"/> are its key.</summary>
    public Guid SourceId { get; set; }
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
    public ScanStatus Status { get; set; } = ScanStatus.Queued;
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
    public StoredCoverageGap? ExtractionGap { get; set; }
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
    /// <summary>
    /// True for a push of <c>vellum scan</c>: the machine that pushed read the files, and the
    /// instance holds only the manifest, the facts and the citations. No blob of the scan is
    /// stored, so nothing that needs the text of a file (the source view) can serve it.
    /// </summary>
    public bool ContentNotHeld { get; set; }
    /// <summary>For a push: the commit that <c>.git/HEAD</c> named on the machine that pushed. Recorded as reported, not verified.</summary>
    public string? ReportedCommitSha { get; set; }

    /// <summary>True when the caller asked for a commit SHA and no ref: such a scan follows no line of history.</summary>
    public bool IsBareCommit() => RequestedRef is { Length: 40 } requested && requested.All(char.IsAsciiHexDigit);

    /// <summary>True for a scan of the head commit of a pull request. Like a bare commit, it follows no line of history.</summary>
    public bool IsPullRequest() => PullRequestNumber is not null;

    /// <summary><see cref="ExtractionGap"/> as the coverage text of <see cref="Scans.CoverageGap"/>: <c>limit</c>, <c>failed</c> or null.</summary>
    public string? ExtractionGapText() => ExtractionGap is { } gap ? EnumText.Of(gap) : null;
}

/// <summary>
/// One file of the commit, as the scan listed it. Every file of the tree has a row, so the
/// scan can say for any path whether it looked at it: <c>read</c> and extracted, or not read
/// with the reason.
/// </summary>
public class ScanFileEntity
{
    /// <summary>The longest path a scan records. A longer path gets a note and no row.</summary>
    public const int MaxPath = Scans.ScanScope.MaxPath;

    public Guid ScanId { get; set; }
    /// <summary>SHA-256 of <see cref="Path"/>, in hex: the key, so a long path is never part of an index.</summary>
    public string PathHash { get; set; } = null!;
    public string Path { get; set; } = null!;
    public string BlobSha { get; set; } = null!;
    public long Size { get; set; }
    public ScanFileState State { get; set; } = ScanFileState.Pending;
    /// <summary>Why a skipped file was not read: a note code, or one of the codes of <see cref="CoverageGap"/>.</summary>
    public string? Reason { get; set; }
}

/// <summary>What the scan did with one file of the commit.</summary>
public enum ScanFileState
{
    Pending,
    /// <summary>The content was fetched and cleared. When the scan is completed, it was also given to the extractors.</summary>
    Read,
    /// <summary>The content was not read, or was read and not used. <see cref="ScanFileEntity.Reason"/> says why.</summary>
    Skipped,
}

/// <summary>
/// The class of <see cref="ScanEntity.ExtractionGap"/> as the column stores it. The same four
/// classes are the text of <see cref="Scans.CoverageGap"/>, which the coverage reports use.
/// </summary>
public enum StoredCoverageGap
{
    Scope,
    Limit,
    Withheld,
    Failed,
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

/// <summary>
/// The <c>RepositorySnapshot</c> of SDD §4: what one scan established about one commit. It is
/// written once, when the scan completes, and never changed.
/// </summary>
public class SnapshotEntity
{
    public Guid ScanId { get; set; }
    public Guid ProjectId { get; set; }
    public Guid SourceId { get; set; }
    public string Host { get; set; } = null!;
    public string Repository { get; set; } = null!;
    public string CommitSha { get; set; } = null!;
    /// <summary>
    /// The Snapshot ID: SHA-256 of the sorted manifest of (path, size, git blob SHA) of the
    /// files the scan read (<see cref="Sources.SnapshotManifest"/>). Two Snapshots with the same
    /// files have the same ID, whatever the kind of their Source.
    /// </summary>
    public string SnapshotId { get; set; } = null!;
    /// <summary>The number of files in the manifest.</summary>
    public int ManifestFiles { get; set; }
    /// <summary>False when a bound or a failure cut the scan or the extraction short.</summary>
    public bool IsComplete { get; set; }
    /// <summary>A JSON list of <c>{ rule, version }</c>: the extraction rules this snapshot was made with.</summary>
    public string RuleVersions { get; set; } = "[]";
    /// <summary>
    /// <see cref="RuleSet.Fingerprint"/> of <see cref="RuleVersions"/>. Null for a snapshot
    /// made before fingerprints were recorded.
    /// </summary>
    public string? RuleFingerprint { get; set; }
    public string Limits { get; set; } = null!;
    public int FactCount { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public enum FactDisposition
{
    /// <summary>The fact maps to a model entity. A proposal exists for it unless nothing differs.</summary>
    Mapped,
    /// <summary>The fact is part of another fact's entity: a publication gives a message its producer.</summary>
    Folded,
    /// <summary>The model has no place for the fact yet. It stays readable as evidence.</summary>
    EvidenceOnly,
    /// <summary>The fact maps to an entity, and something it needs is missing, was rejected or waits for a relink decision.</summary>
    Blocked,
}

/// <summary>One extracted fact of a snapshot, with what the importer did with it.</summary>
public class SnapshotFactEntity
{
    public Guid ScanId { get; set; }
    public int Seq { get; set; }
    public FactKind Kind { get; set; }
    public string FactKey { get; set; } = null!;
    public string Name { get; set; } = null!;
    /// <summary>A JSON object of attribute name to value.</summary>
    public string Attributes { get; set; } = "{}";
    /// <summary>A JSON list of <see cref="EvidenceRef"/>.</summary>
    public string Evidence { get; set; } = "[]";
    public FactDisposition Disposition { get; set; }
    /// <summary>A fixed code for a fact that is not <c>mapped</c>, for example <c>no-model-home</c>.</summary>
    public string? Reason { get; set; }
}

public enum ProposalStatus
{
    Pending,
    Accepted,
    Rejected,
    /// <summary>A later tracking scan of the same repository completed before a person decided, or the scan was planned again.</summary>
    Superseded,
}

public enum ProposalAction
{
    Add,
    Change,
    Remove,
    Conflict,
    /// <summary>
    /// The extraction rules changed and the exact rule did not say which new fact an accepted
    /// fact is. A person names the fact, or says that none is. It changes the link only.
    /// </summary>
    Relink,
    /// <summary>
    /// "Same as X?": an element fact whose key no element is bound to has the normalised name
    /// of an element that another Source holds. A person answers. Yes binds the element to
    /// the key of this Source; no makes the fact a new element. Nothing is bound by name alone.
    /// </summary>
    Bind,
}

/// <summary>What a link or a proposal is about: a fact of an extractor, or the repository itself.</summary>
public enum Subject
{
    Fact,
    /// <summary>The repository as the system that holds its services. No extractor makes this one; its fact key is empty.</summary>
    Repository,
}

/// <summary>
/// The kind of model entity a proposal or a link names, as the column stores it. The same
/// kinds are the text of <see cref="Proposals.EntityKinds"/>, which the model lookups and the
/// impact reports use.
/// </summary>
public enum StoredEntityKind
{
    Element,
    Operation,
    Channel,
    Subscription,
    Binding,
    Message,
    Schema,
    Relationship,
}

/// <summary>
/// The <c>FactProposal</c> of SDD §4. A proposal changes nothing. Only a decision of a person
/// with the Editor role applies it.
/// </summary>
public class ProposalEntity
{
    public Guid Id { get; set; }
    public Guid ScanId { get; set; }
    public Guid ProjectId { get; set; }
    public int Seq { get; set; }
    public ProposalAction Action { get; set; }
    public StoredEntityKind EntityKind { get; set; }
    /// <summary>The entity the proposal is about. For <c>add</c> it is the ID the new entity gets.</summary>
    public Guid EntityId { get; set; }
    public Subject Subject { get; set; } = Import.Subject.Fact;
    public string FactKey { get; set; } = null!;
    public string Name { get; set; } = null!;
    /// <summary>A JSON object: the values the source establishes. Null for <c>remove</c>. For <c>relink</c>: <c>{ candidates: [...] }</c>.</summary>
    public string? Proposed { get; set; }
    /// <summary>A JSON object: the values of the entity when the scan completed. Null for <c>add</c>.</summary>
    public string? Current { get; set; }
    /// <summary>A JSON list of <c>{ field, from, to }</c>.</summary>
    public string Changes { get; set; } = "[]";
    /// <summary>A JSON list of <see cref="EvidenceRef"/>.</summary>
    public string Evidence { get; set; } = "[]";
    /// <summary>The fact's attributes, kept on the link when the proposal is accepted.</summary>
    public string Attributes { get; set; } = "{}";
    /// <summary>For a message: a JSON list of the fact keys of the services whose subscriptions name it.</summary>
    public string ConsumerKeys { get; set; } = "[]";
    public Guid[] DependsOn { get; set; } = [];
    /// <summary>
    /// A JSON list of <see cref="Proposals.OtherSourceFact"/>: what other Sources of the project
    /// accepted for the same entity. A proposal that disagrees with them is a conflict, and
    /// the review shows every side. No Source wins by itself.
    /// </summary>
    public string OtherSources { get; set; } = "[]";
    public ProposalStatus Status { get; set; } = ProposalStatus.Pending;
    /// <summary>True when a limit or a failure cut the scan short.</summary>
    public bool Partial { get; set; }
    /// <summary>Identifies what is proposed, so a rejected proposal is not made again.</summary>
    public string Fingerprint { get; set; } = null!;
    public string? DecidedBy { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
}

/// <summary>
/// An accepted proposal: the fact of a repository and the model entity that holds it. The row
/// keeps all the evidence the fact was accepted with; the model keeps the first pointer.
/// </summary>
public class FactLinkEntity
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public string Host { get; set; } = null!;
    /// <summary>The repository's name in lower case. It follows a rename; <see cref="RepositoryId"/> does not change.</summary>
    public string Repository { get; set; } = null!;
    public long? RepositoryId { get; set; }
    public Subject Subject { get; set; } = Import.Subject.Fact;
    public string FactKey { get; set; } = null!;
    /// <summary><see cref="Scans.KeyHash"/> of <see cref="FactKey"/>: the indexed form.</summary>
    public string FactKeyHash { get; set; } = null!;
    public StoredEntityKind EntityKind { get; set; }
    public Guid EntityId { get; set; }
    public Guid ScanId { get; set; }
    public string CommitSha { get; set; } = null!;
    /// <summary>
    /// The fingerprint of the extraction rules that made <see cref="FactKey"/>. Null when it
    /// is unknown. A scan with other rules relinks the row before it compares it.
    /// </summary>
    public string? RuleFingerprint { get; set; }
    /// <summary>The fact's attributes as accepted.</summary>
    public string Attributes { get; set; } = "{}";
    /// <summary>For a message: a JSON list of the fact keys of the consumers it was accepted with.</summary>
    public string ConsumerKeys { get; set; } = "[]";
    /// <summary>The entity's values as accepted. An entity that differs from them was edited by a person since.</summary>
    public string Accepted { get; set; } = "{}";
    public string Evidence { get; set; } = "[]";
    public string AcceptedBy { get; set; } = null!;
    public DateTimeOffset AcceptedAt { get; set; }
}

/// <summary>A rejected proposal, remembered by its fingerprint.</summary>
public class RejectionEntity
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public string Host { get; set; } = null!;
    public string Repository { get; set; } = null!;
    public long? RepositoryId { get; set; }
    public Subject Subject { get; set; } = Import.Subject.Fact;
    public string FactKey { get; set; } = null!;
    public string FactKeyHash { get; set; } = null!;
    public string Fingerprint { get; set; } = null!;
    public Guid ScanId { get; set; }
    public string RejectedBy { get; set; } = null!;
    public DateTimeOffset RejectedAt { get; set; }
}

/// <summary>Where a fact is established: lines of one file at one commit.</summary>
/// <param name="CommitSha">Null for the commit of the scan the evidence belongs to.</param>
public sealed record EvidenceRef(string Path, int StartLine, int EndLine, string Rule, int RuleVersion, string? CommitSha = null);
