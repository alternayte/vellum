namespace Vellum.Modules.Import.Proposals;

/// <summary>
/// The <c>RepositorySnapshot</c> of SDD §4: what one scan established about one commit. It is
/// written once, when the scan completes, and never changed.
/// </summary>
public class SnapshotEntity
{
    public Guid ScanId { get; set; }
    public Guid ProjectId { get; set; }
    public string Host { get; set; } = null!;
    public string Repository { get; set; } = null!;
    public string CommitSha { get; set; } = null!;
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

public static class FactDisposition
{
    /// <summary>The fact maps to a model entity. A proposal exists for it unless nothing differs.</summary>
    public const string Mapped = "mapped";
    /// <summary>The fact is part of another fact's entity: a publication gives a message its producer.</summary>
    public const string Folded = "folded";
    /// <summary>The model has no place for the fact yet. It stays readable as evidence.</summary>
    public const string EvidenceOnly = "evidence-only";
    /// <summary>The fact maps to an entity, and something it needs is missing, was rejected or waits for a relink decision.</summary>
    public const string Blocked = "blocked";
}

/// <summary>One extracted fact of a snapshot, with what the importer did with it.</summary>
public class SnapshotFactEntity
{
    public Guid ScanId { get; set; }
    public int Seq { get; set; }
    public string Kind { get; set; } = null!;
    public string FactKey { get; set; } = null!;
    public string Name { get; set; } = null!;
    /// <summary>A JSON object of attribute name to value.</summary>
    public string Attributes { get; set; } = "{}";
    /// <summary>A JSON list of <see cref="EvidenceRef"/>.</summary>
    public string Evidence { get; set; } = "[]";
    public string Disposition { get; set; } = null!;
    /// <summary>A fixed code for a fact that is not <c>mapped</c>, for example <c>no-model-home</c>.</summary>
    public string? Reason { get; set; }
}

public static class ProposalStatus
{
    public const string Pending = "pending";
    public const string Accepted = "accepted";
    public const string Rejected = "rejected";
    /// <summary>A later tracking scan of the same repository completed before a person decided, or the scan was planned again.</summary>
    public const string Superseded = "superseded";
}

public static class ProposalAction
{
    public const string Add = "add";
    public const string Change = "change";
    public const string Remove = "remove";
    public const string Conflict = "conflict";
    /// <summary>
    /// The extraction rules changed and the exact rule did not say which new fact an accepted
    /// fact is. A person names the fact, or says that none is. It changes the link only.
    /// </summary>
    public const string Relink = "relink";
}

/// <summary>What a link or a proposal is about: a fact of an extractor, or the repository itself.</summary>
public static class Subject
{
    public const string Fact = "fact";
    /// <summary>The repository as the system that holds its services. No extractor makes this one; its fact key is empty.</summary>
    public const string Repository = "repository";
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
    public string Action { get; set; } = null!;
    public string EntityKind { get; set; } = null!;
    /// <summary>The entity the proposal is about. For <c>add</c> it is the ID the new entity gets.</summary>
    public Guid EntityId { get; set; }
    public string Subject { get; set; } = Proposals.Subject.Fact;
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
    public string Status { get; set; } = ProposalStatus.Pending;
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
    public string Subject { get; set; } = Proposals.Subject.Fact;
    public string FactKey { get; set; } = null!;
    /// <summary><see cref="Scans.KeyHash"/> of <see cref="FactKey"/>: the indexed form.</summary>
    public string FactKeyHash { get; set; } = null!;
    public string EntityKind { get; set; } = null!;
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
    public string Subject { get; set; } = Proposals.Subject.Fact;
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
