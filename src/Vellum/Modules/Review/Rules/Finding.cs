using Vellum.Modules.Promises;

namespace Vellum.Modules.Review.Rules;

/// <summary>
/// The four results of SDD §8. <c>verified</c> means only that the declared model satisfies
/// the structural rule; it says nothing about a running system. A rule that needs a fact the
/// model does not hold gives <c>unknown</c>, never <c>verified</c> and never <c>concern</c>.
/// </summary>
public static class RuleResult
{
    public const string Verified = "verified";
    public const string Concern = "concern";
    public const string Unknown = "unknown";
    public const string NotApplicable = "not-applicable";
}

public static class Severity
{
    /// <summary>The finding prevents Build Ready until an accepted decision settles it or the model changes.</summary>
    public const string Blocking = "blocking";
    public const string Warning = "warning";
    public const string Note = "note";

    public static int Order(string severity) => severity switch { Blocking => 0, Warning => 1, _ => 2 };
}

public static class RuleCategory
{
    public const string Resilience = "resilience";
    public const string Contracts = "contracts";
    public const string Truth = "truth";
    public const string Overengineering = "overengineering";
}

/// <summary>A thing a finding cites, by its stable ID.</summary>
/// <param name="Kind"><c>element</c>, <c>operation</c>, <c>channel</c>, <c>subscription</c>, <c>binding</c>, <c>message</c>, <c>schema</c>, <c>scenario</c> or <c>promise</c>.</param>
public sealed record Cited(string Kind, Guid Id, string Name);

/// <summary>
/// The result of one rule for one subject, on one pinned state. It is a function of that
/// state and of nothing else, so the same state gives the same finding.
/// </summary>
/// <param name="Severity">Of this finding. A rule gives <c>blocking</c> only for a subject on the path of an approved promise.</param>
/// <param name="Reason">One sentence built from facts of the model.</param>
/// <param name="WouldChange">What change of the model gives another result.</param>
/// <param name="Question">For an overengineering rule: the question the finding asks. A decision answers it.</param>
/// <param name="Fingerprint">
/// A hash of the result, the severity, the cited IDs and the reason. A disposition records
/// it; when it differs later, the facts of the subject changed and the disposition needs a
/// new review.
/// </param>
public sealed record Finding(
    string RuleId, int RuleVersion, string Category, string Severity, string Result,
    Cited Subject, IReadOnlyList<Cited> Cites, string Reason, string WouldChange, string? Question, string Fingerprint)
{
    /// <summary>True for a result that asks for attention: a concern, or a fact the rule could not read.</summary>
    public bool Fired => Result is RuleResult.Concern or RuleResult.Unknown;
}

/// <summary>What a rule gives for one subject, before the engine adds the rule's identity and the fingerprint.</summary>
internal sealed record RuleOutcome(
    string Result, Guid SubjectId, IReadOnlyList<Guid> Cites, string Reason, string WouldChange,
    string Severity = Severity.Warning, string? Question = null);

/// <param name="Reads">What the rule reads, for the reference page and the rule list.</param>
/// <param name="Settle">How a concern of the rule is settled.</param>
internal sealed record Rule(
    string Id, int Version, string Category, string Title, string Reads, string Settle,
    Func<ReviewFacts, IEnumerable<RuleOutcome>> Check);

/// <summary>A rule as the API lists it.</summary>
public sealed record RuleDto(string Id, int Version, string Category, string Title, string Reads, string Settle);

/// <summary>Every finding of one pinned state, in a fixed order, with a hash of all of them.</summary>
/// <param name="Hash">SHA-256 of the canonical JSON of the rule set version and the findings.</param>
public sealed record ReviewReport(string RuleSetVersion, string Hash, IReadOnlyList<Finding> Findings)
{
    internal static ReviewReport Of(IReadOnlyList<Finding> findings) =>
        new(RuleSet.Version, PromiseJson.Hash(new { ruleSetVersion = RuleSet.Version, findings }), findings);
}
