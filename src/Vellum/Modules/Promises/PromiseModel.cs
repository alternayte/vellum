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

/// <summary>The four results of SDD §8. There is no fifth, and none of them is "verified".</summary>
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

/// <summary>Plays a scenario with these faults merged over its own (by target) and evaluates these assertions.</summary>
public sealed record ScenarioCheckRecipe(Guid ScenarioId, IReadOnlyList<Fault> Faults, IReadOnlyList<Assertion> Expect);

public sealed record SourcePatternRecipe(string Repository, string Pattern, Guid? Service);

/// <summary>A test plan a person or an agent implements outside Vellum.</summary>
public sealed record TestPlanRecipe(
    IReadOnlyList<string> Steps,
    IReadOnlyList<string> Inputs,
    IReadOnlyList<string> ExpectedObservations,
    IReadOnlyList<string> ForbiddenObservations,
    IReadOnlyList<Guid> ContractIds);

/// <summary>What to run or check: text for a reader, and the typed part that fits the obligation's kind.</summary>
public sealed record ObligationRecipe(
    string Text,
    ScenarioCheckRecipe? ScenarioCheck = null,
    SourcePatternRecipe? SourcePattern = null,
    TestPlanRecipe? Plan = null);

/// <param name="Id"><c>&lt;requirement ID&gt;/O&lt;number&gt;</c>, for example <c>ORD-014/O2</c>. Never reused inside a promise.</param>
/// <param name="Source"><c>generated</c> or <c>manual</c>.</param>
/// <param name="GeneratorKey">What a generated obligation was made for, so generating again adds nothing twice.</param>
public sealed record Obligation(
    string Id, int Number, string Kind, string Title, ObligationRecipe Recipe, string Source, string? GeneratorKey)
{
    public const string Generated = "generated";
    public const string Manual = "manual";

    /// <summary>The last part of the ID (<c>O2</c>): what a URL names the obligation by. Derived, so it is not stored.</summary>
    [JsonIgnore]
    public string Key => $"O{Number}";
}

/// <summary>The model a check ran on: a branch stream at a version, and the named revision when one was given.</summary>
public sealed record ModelPin(Guid StreamId, long Version, Guid? RevisionId, string? RevisionName);

public sealed record ExternalSource(string System, string RunUrl, string CommitSha, string? JobName, string Conclusion);

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

/// <summary>
/// One check of one obligation of one promise revision. Runs are appended and never changed.
/// <paramref name="ObligationHash"/> is the hash of the obligation as it was when the run was
/// made: a run of an obligation that was edited since says nothing about the edited one.
/// </summary>
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
    string Detail);
