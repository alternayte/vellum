using System.Text.Json.Serialization;
using Vellum.Modules.Trace.Engine;

namespace Vellum.Modules.Promises;

/// <summary>Who first wrote a promise. Only a person in an interactive session approves one, whatever its origin.</summary>
public static class PromiseOrigin
{
    public const string Human = "human";
    public const string AiSuggested = "ai-suggested";
    public const string AgentSuggested = "agent-suggested";

    public static readonly string[] All = [Human, AiSuggested, AgentSuggested];
}

public static class RevisionStatus
{
    public const string Draft = "draft";
    public const string Approved = "approved";
}

/// <summary>The state of a promise as a whole. It is derived from its revisions and never stored.</summary>
public static class PromiseLifecycle
{
    /// <summary>No revision was ever approved.</summary>
    public const string Draft = "draft";
    public const string Approved = "approved";
    /// <summary>The revision in force is an approved revision that retires the promise.</summary>
    public const string Retired = "retired";
}

public static class ObligationKind
{
    public const string ScenarioCheck = "scenario-check";
    public const string SourcePatternCheck = "source-pattern-check";
    public const string ContractTest = "contract-test";
    public const string IntegrationTest = "integration-test";
    public const string FailureTest = "failure-test";

    /// <summary>The kinds Vellum runs by itself, with the same result for the same inputs.</summary>
    public static bool IsDeterministic(string kind) => kind is ScenarioCheck or SourcePatternCheck;

    /// <summary>A reviewable test plan. Vellum cannot execute it; only external evidence gives it a result.</summary>
    public static bool IsPlan(string kind) => kind is ContractTest or IntegrationTest or FailureTest;

    /// <summary><c>deterministic</c>, <c>external-evidence</c> or <c>none</c> for a kind Vellum does not know.</summary>
    public static string Execution(string kind) =>
        IsDeterministic(kind) ? "deterministic" : IsPlan(kind) ? "external-evidence" : "none";
}

/// <summary>
/// The four results of SDD §8. There is no fifth, and none of them is "verified". A result
/// whose inputs moved on is still one of the four; the status shows it with <c>current: false</c>.
/// </summary>
public static class RunResult
{
    public const string Passed = "passed";
    public const string Failed = "failed";
    public const string NotRun = "not-run";
    public const string Unsupported = "unsupported";
}

/// <summary>The values of the <c>pattern</c> attribute that the import extractors emit.</summary>
public static class SourcePattern
{
    public const string TransactionalOutbox = "transactional-outbox";
    public const string InMemoryOutbox = "in-memory-outbox";

    public static readonly string[] Known = [TransactionalOutbox, InMemoryOutbox];
}

/// <summary>What a promise is bound to. Every ID names a thing of the project when the promise is written.</summary>
public sealed record PromiseLinks(
    IReadOnlyList<Guid> ScenarioIds,
    IReadOnlyList<Guid> OperationIds,
    IReadOnlyList<Guid> ChannelIds,
    IReadOnlyList<Guid> SubscriptionIds,
    IReadOnlyList<Guid> MessageIds,
    IReadOnlyList<Guid> ElementIds)
{
    public static PromiseLinks None { get; } = new([], [], [], [], [], []);

    /// <summary>Every link as <c>(kind, id)</c>, in a stable order.</summary>
    public IEnumerable<(string Kind, Guid Id)> All() =>
        ScenarioIds.Select(id => ("scenario", id))
            .Concat(OperationIds.Select(id => ("operation", id)))
            .Concat(ChannelIds.Select(id => ("channel", id)))
            .Concat(SubscriptionIds.Select(id => ("subscription", id)))
            .Concat(MessageIds.Select(id => ("message", id)))
            .Concat(ElementIds.Select(id => ("element", id)));
}

/// <summary>
/// A trace event that must not occur: the structured part of a forbidden outcome. It maps
/// exactly to an engine assertion that counts zero events of <paramref name="Kind"/>, for
/// <paramref name="Target"/> when one is given.
/// </summary>
public sealed record ForbiddenEvent(TraceEventKind Kind, Guid? Target);

/// <summary>The promise needs this pattern in the source of a repository, for one service when given.</summary>
/// <param name="Repository"><c>owner/name</c>, in lower case.</param>
/// <param name="Service">An element of the project that an accepted import links to a service of the repository.</param>
public sealed record RequiredPattern(string Repository, string Pattern, Guid? Service);

/// <summary>The text and bindings of one revision of a promise.</summary>
public sealed record PromiseContent(
    string Title,
    string Claim,
    string? Scope,
    string? Preconditions,
    string? ExpectedOutcome,
    string? ForbiddenOutcome,
    string? EvidenceCriteria,
    IReadOnlyList<ForbiddenEvent> ForbiddenEvents,
    RequiredPattern? RequiresPattern,
    PromiseLinks Links);

/// <summary>
/// Plays a scenario with these faults added to its own and evaluates these assertions. A
/// fault here never names a target the scenario already faults: an obligation adds failures,
/// it does not weaken or replace one the scenario declares.
/// </summary>
public sealed record ScenarioCheckRecipe(Guid ScenarioId, IReadOnlyList<Fault> Faults, IReadOnlyList<Assertion> Expect);

/// <param name="RequireUnconditional">
/// True (the default, also when null) when the pattern must be declared with no compile-time
/// condition. A fact that exists only under <c>#if</c> then fails the check. False accepts a
/// conditional declaration, and the run says so in a limitation.
/// </param>
public sealed record SourcePatternRecipe(string Repository, string Pattern, Guid? Service, bool? RequireUnconditional = null)
{
    [JsonIgnore]
    public bool NeedsUnconditional => RequireUnconditional ?? true;
}

/// <summary>
/// Where the evidence of a test plan must come from. A result counts only when Vellum reads
/// from that system that the run is of this repository, of this workflow and, when
/// <paramref name="Job"/> is given, that this job ran in it and succeeded.
/// </summary>
/// <param name="System"><c>github-actions</c>.</param>
/// <param name="Repository"><c>owner/name</c>, in lower case: a repository the project has an accepted import link or a tracked ref for.</param>
/// <param name="Workflow">The file name of the workflow (<c>ci.yml</c>), its path, or its name.</param>
public sealed record EvidenceSource(string System, string Repository, string Workflow, string? Job);

/// <summary>A test plan a person or an agent implements outside Vellum.</summary>
/// <param name="EvidenceSource">Null until a person names it. A plan without it cannot receive a result.</param>
public sealed record TestPlanRecipe(
    IReadOnlyList<string> Steps,
    IReadOnlyList<string> Inputs,
    IReadOnlyList<string> ExpectedObservations,
    IReadOnlyList<string> ForbiddenObservations,
    IReadOnlyList<Guid> ContractIds,
    EvidenceSource? EvidenceSource = null);

/// <summary>What to run or check: text for a reader, and the typed part that fits the obligation's kind.</summary>
public sealed record ObligationRecipe(
    string Text,
    ScenarioCheckRecipe? ScenarioCheck = null,
    SourcePatternRecipe? SourcePattern = null,
    TestPlanRecipe? Plan = null);

/// <param name="Id"><c>&lt;requirement ID&gt;/O&lt;number&gt;</c>, for example <c>ORD-014/O2</c>. Never reused inside a promise.</param>
/// <param name="Source">
/// Who wrote the obligation first: <c>generated</c> (the generator made it from the links),
/// <c>manual</c> (a person wrote it) or <c>agent</c> (an agent token wrote it).
/// </param>
/// <param name="GeneratorKey">What a generated obligation was made for, so generating again adds nothing twice.</param>
public sealed record Obligation(
    string Id, int Number, string Kind, string Title, ObligationRecipe Recipe, string Source, string? GeneratorKey)
{
    public const string Generated = "generated";
    public const string Manual = "manual";
    public const string Agent = "agent";

    /// <summary>The last part of the ID (<c>O2</c>): what a URL names the obligation by. Derived, so it is not stored.</summary>
    [JsonIgnore]
    public string Key => $"O{Number}";
}

/// <summary>The model a check ran on: a branch stream at a version, and the named revision when one was given.</summary>
public sealed record ModelPin(Guid StreamId, long Version, Guid? RevisionId, string? RevisionName);

/// <param name="JobName">The job of the obligation's evidence source, when it names one.</param>
/// <param name="Conclusion">What Vellum read from the system when it could read the run, else what the caller said.</param>
/// <param name="Repository">The repository Vellum read the run from. Null when it could not read it.</param>
/// <param name="Workflow">The workflow file path Vellum read from the run.</param>
public sealed record ExternalSource(
    string System, string RunUrl, string CommitSha, string? JobName, string Conclusion,
    string? Repository = null, string? Workflow = null, long? RunId = null);

public static class EvidenceKind
{
    public const string Trace = "trace";
    public const string Source = "source";
    public const string External = "external";
}

public static class EvidenceIdentity
{
    /// <summary>Vellum read the run from the system that holds it, and it is what the caller said.</summary>
    public const string Checked = "verified";
    /// <summary>Only the caller says so.</summary>
    public const string Claimed = "claimed";
}

/// <summary>
/// One piece of evidence of a run. <c>trace</c>: the playback hash and the scenario.
/// <c>source</c>: a pointer to lines at a commit and their page. <c>external</c>: a result of
/// another system, with whether Vellum could check its identity.
/// </summary>
public sealed record EvidenceItem(
    string Kind,
    string? Hash = null,
    Guid? ScenarioId = null,
    string? Pointer = null,
    string? Url = null,
    ExternalSource? Source = null,
    string? ArtifactDigest = null,
    string? Identity = null,
    string? IdentityReason = null,
    string? ClaimedResult = null);

/// <summary>A schema a check validated against, at one content version.</summary>
public sealed record SchemaInput(Guid Id, int Version);

/// <summary>
/// Everything a run read, at the exact version it read it. A result is evidence only for
/// these inputs: the status compares each with what is current and shows any difference.
/// </summary>
/// <param name="ScenarioHash">The hash of the scenario document that was played.</param>
/// <param name="Schemas">Every schema the pinned model references that exists, by ID.</param>
/// <param name="Repository"><c>host/owner/name</c> of the source or the external run.</param>
/// <param name="Ref">The ref the scan read. Null for a scan of a bare commit and for external evidence.</param>
/// <param name="ScanKind"><c>tracking</c> or <c>comparison</c>.</param>
/// <param name="Coverage"><c>covered</c>, or the class of the reason the scan did not look where the pattern would be.</param>
public sealed record RunInputs(
    string ObligationHash,
    Guid? ModelStreamId = null,
    long? ModelVersion = null,
    Guid? ScenarioId = null,
    long? ScenarioVersion = null,
    string? ScenarioHash = null,
    IReadOnlyList<SchemaInput>? Schemas = null,
    string? Repository = null,
    Guid? ScanId = null,
    string? CommitSha = null,
    string? Ref = null,
    string? ScanKind = null,
    string? Coverage = null);

/// <summary>
/// One assertion of a scenario check as the run evaluated it. <paramref name="Vacuous"/>:
/// the assertion holds without the scenario exercising what it asserts.
/// </summary>
public sealed record AssertionOutcome(string Id, bool Passed, string Detail, bool Vacuous, string? VacuousReason);

/// <summary>
/// One check of one obligation of one promise revision. Runs are appended and never changed.
/// <paramref name="ObligationHash"/> is the hash of the obligation as it was when the run was
/// made: a run of an obligation that was edited since says nothing about the edited one.
/// </summary>
/// <param name="Inputs">Null only for a run recorded before inputs were bound. Such a run is never shown as current.</param>
/// <param name="Assertions">For a scenario check: each assertion with its result.</param>
/// <param name="EffectiveFaults">For a scenario check: the faults that were played, the scenario's and the obligation's.</param>
/// <param name="Draft">True when the revision was a draft at the time of the run. Such a run never counts for an approved revision.</param>
/// <param name="StartedBy">The user of the call. For an agent token that is the user who created the token: <paramref name="Actor"/> names the token.</param>
/// <param name="Actor">
/// Who made the call: <c>user:&lt;id&gt;</c> for a person, <c>agent-token:&lt;id&gt;</c> for an
/// agent token. Null in a run recorded before the actor was kept.
/// </param>
public sealed record VerificationRun(
    Guid Id,
    string ObligationId,
    int PromiseRevision,
    string ObligationHash,
    string StartedBy,
    DateTimeOffset At,
    ModelPin? ModelRevision,
    string? CommitSha,
    Guid? ScanId,
    string Environment,
    string Result,
    string Scope,
    IReadOnlyList<EvidenceItem> Evidence,
    IReadOnlyList<string> Limitations,
    string Detail,
    RunInputs? Inputs = null,
    IReadOnlyList<AssertionOutcome>? Assertions = null,
    IReadOnlyList<Fault>? EffectiveFaults = null,
    bool? Draft = null,
    string? Actor = null);
