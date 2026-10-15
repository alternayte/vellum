namespace Vellum.Modules.Import.Sources;

// The body of a push and its answer. The server binds these types and `vellum scan` sends
// them: the CLI compiles this same file, so the two cannot drift apart. A push holds no file
// content: a manifest of paths, sizes and blob SHAs, the facts with their citations, and the
// rule versions the facts were extracted with.

/// <param name="Name">The name of the agent-push Source. A push finds its Source by the slug of the name (<c>Orders API</c> and <c>orders-api</c> are one Source) and creates it when the project has none.</param>
/// <param name="CommitSha">The commit that <c>.git/HEAD</c> named on the machine that pushed, read as text. Null when the directory has no readable <c>.git/HEAD</c>. Vellum records it and does not verify it.</param>
/// <param name="Rules">The extraction rules with their versions. They must be the rules of this server.</param>
/// <param name="Scope">The scope rules the files were selected with.</param>
/// <param name="Files">The manifest: each file the extractors read whole, with its size in bytes and its git blob SHA.</param>
/// <param name="Skipped">Files the scope would read that were not read, with the reason: a limit, withheld content (a secret, binary bytes), or a parse failure. Files that the path filter, the scope or a glob leave out are not listed.</param>
/// <param name="Facts">The facts, each with its citations.</param>
/// <param name="IsComplete">False when a limit or a failure cut the scan or the extraction short.</param>
/// <param name="ExtractionGap">Null, <c>limit</c> or <c>failed</c>: the extraction left out facts for a reason that no single file explains.</param>
/// <param name="FilesListed">How many files the directory holds outside excluded directories.</param>
public sealed record AgentPushRequest(
    string Name, string? CommitSha, IReadOnlyList<AgentPushRule> Rules, AgentPushScope Scope,
    IReadOnlyList<AgentPushFile> Files, IReadOnlyList<AgentPushSkip> Skipped, IReadOnlyList<AgentPushFact> Facts,
    bool IsComplete, string? ExtractionGap, int FilesListed);

public sealed record AgentPushRule(string Id, int Version);

/// <param name="IncludePaths">Path prefixes in scope. None: the whole directory.</param>
/// <param name="ExcludePaths">Path prefixes left out.</param>
/// <param name="ExcludeGlobs">The globs left out. They hold every glob of the workspace's <c>import.excludeGlobs</c>.</param>
public sealed record AgentPushScope(
    int MaxFiles, long MaxFileBytes, long MaxTotalBytes, IReadOnlyList<string> ExcludeGlobs,
    IReadOnlyList<string> IncludePaths, IReadOnlyList<string> ExcludePaths);

/// <param name="Path">Relative to the pushed directory, with <c>/</c> separators.</param>
/// <param name="BlobSha">The git blob SHA of the file's bytes: 40 lower-case hex characters.</param>
public sealed record AgentPushFile(string Path, long Size, string BlobSha);

/// <param name="Reason">A fixed code such as <c>secret-file</c>, <c>binary-content</c>, <c>credential-dense</c>, <c>parse-failure</c> or <c>limit:file-bytes</c>.</param>
public sealed record AgentPushSkip(string Path, string Reason);

/// <param name="Kind">One of the fact kinds of the extractors, such as <c>Service</c> or <c>Operation</c>.</param>
/// <param name="Evidence">The citations: lines of files of the manifest, and the rule that read them.</param>
public sealed record AgentPushFact(
    string Kind, string Key, string Name, IReadOnlyDictionary<string, string> Attributes, IReadOnlyList<AgentPushCitation> Evidence);

public sealed record AgentPushCitation(string Path, int StartLine, int EndLine, string Rule, int RuleVersion);

/// <summary>What a push needs to know before it reads a file: the rules of this server and the workspace's import limits.</summary>
/// <param name="MaxFacts">The most facts one push holds.</param>
public sealed record AgentPushSettings(
    IReadOnlyList<AgentPushRule> Rules, int MaxFiles, long MaxFileBytes, long MaxTotalBytes, IReadOnlyList<string> ExcludeGlobs, int MaxFacts);

/// <param name="ScanId">The scan that holds the Snapshot. Its proposals wait for a person at <c>/api/projects/{projectId}/imports/{scanId}</c>.</param>
/// <param name="SnapshotId">SHA-256 of the manifest: the same files give the same ID as a scan of a git host.</param>
/// <param name="SourceCreated">True when this push created the Source.</param>
/// <param name="Proposals">Proposals that wait for a person. A push accepts nothing.</param>
public sealed record AgentPushResponse(
    Guid SourceId, string SourceName, bool SourceCreated, Guid ScanId, string SnapshotId, int Files, int Facts, int Proposals, bool IsComplete);
