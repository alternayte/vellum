using Vellum.Modules.Build;
using Vellum.Modules.Promises;

namespace Vellum.Modules.Review.Rules;

/// <summary>
/// The deterministic review rules (SDD §8). Each rule is a pure function of the pinned state:
/// it reads the model, the scenarios, the schemas and the promises of one
/// <see cref="BuildInput"/>, and gives one result per subject. No rule calls an AI, reads a
/// clock or reads the database, so the rules work with no AI key (SDD guarantee 6) and the
/// same state gives the same findings and the same hash (guarantee 4).
/// <para>
/// A rule reads a fact as stated only when its truth label is <c>declared</c>,
/// <c>observed-in-source</c> or <c>measured</c> and it is not stale. For any other fact the
/// result is <c>unknown</c> (guarantee 2: an inference is never read as a fact).
/// </para>
/// </summary>
public static class RuleSet
{
    /// <summary>Changes when a rule is added or removed, or when the version of a rule changes.</summary>
    public const string Version = "review-rules/1";

    internal static readonly IReadOnlyList<Rule> Rules =
        [.. ResilienceRules.All, .. ContractRules.All, .. TruthRules.All, .. OverengineeringRules.All];

    public static IReadOnlyList<RuleDto> Catalogue { get; } =
        [.. Rules.Select(r => new RuleDto(r.Id, r.Version, r.Category, r.Title, r.Reads, r.Settle))];

    public static bool IsRule(string? id) => Rules.Any(r => r.Id == id);

    /// <summary>The version of a rule, or null for an ID that names none.</summary>
    public static int? VersionOf(string? id) => Rules.FirstOrDefault(r => r.Id == id)?.Version;

    /// <summary>Every finding of the input, by rule ID and then by subject ID.</summary>
    public static ReviewReport Run(BuildInput input) => Run(new BuildView(input));

    internal static ReviewReport Run(BuildView view)
    {
        var facts = new ReviewFacts(view);
        var findings = new List<Finding>();
        foreach (var rule in Rules)
        {
            // One result for one subject: the first outcome of a rule for a subject stands.
            foreach (var outcome in rule.Check(facts).DistinctBy(o => o.SubjectId).OrderBy(o => o.SubjectId))
            {
                var cites = outcome.Cites.Where(id => id != outcome.SubjectId).Distinct().Select(facts.Cite).ToList();
                var fingerprint = PromiseJson.Hash(new
                {
                    rule.Id, outcome.Result, outcome.Severity, outcome.SubjectId, cites = cites.Select(c => c.Id).Order(), outcome.Reason,
                });
                findings.Add(new Finding(
                    rule.Id, rule.Version, rule.Category, outcome.Severity, outcome.Result,
                    facts.Cite(outcome.SubjectId), cites, outcome.Reason, outcome.WouldChange, outcome.Question, fingerprint));
            }
        }
        return ReviewReport.Of(findings);
    }
}
