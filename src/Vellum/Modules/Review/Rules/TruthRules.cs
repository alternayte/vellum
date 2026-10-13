using Vellum.Modules.Modelling.Model;
using Vellum.Modules.Promises;
using static Vellum.Modules.Review.Rules.ResilienceRules;

namespace Vellum.Modules.Review.Rules;

/// <summary>The rules about what an approved promise stands on: the truth labels on its path and its evidence.</summary>
internal static class TruthRules
{
    public static readonly Rule[] All =
    [
        new("TRU-001", 1, RuleCategory.Truth, "An approved promise stands on confirmed facts",
            "Each fact on the path of an approved promise: the truth label and the stale mark. The path is the links of the promise, the steps of its linked scenarios and, from each of those to the end, the service of an operation, the channel, the consumer and the dead-letter channel of a subscription, the producer of a message, and the subscriptions and bindings of a channel.",
            "Confirm the fact or remove it. An accepted decision settles the finding, and it does not change the label: the Build blocker of the fact itself stays until a person confirms the fact.",
            UnconfirmedOnPath),
        new("TRU-002", 2, RuleCategory.Truth, "A promise in force has current evidence",
            "Each promise: the evidence status that the Promises module computes for the revision in force. The rule does not compute it again.",
            "Run the checks on the pinned inputs, or record evidence for the commit the model follows.",
            PromiseEvidence),
    ];

    private static IEnumerable<RuleOutcome> UnconfirmedOnPath(ReviewFacts facts)
    {
        foreach (var id in facts.PathFacts)
        {
            var fact = facts.Fact(id)!.Value;
            var promises = facts.PromisesOn(id);
            Guid[] cites = [.. promises.Select(p => p.Id)];
            var requirements = string.Join(", ", promises.Select(p => p.RequirementId));
            var problem =
                fact.Stale is { } stale ? $"is marked stale since {stale.SinceRevision}"
                : fact.Status == FactStatus.Inferred ? "is inferred and no person confirmed it"
                : fact.Status == FactStatus.Unknown ? "has an unknown truth label"
                : null;
            yield return problem is null
                ? new(RuleResult.Verified, id, cites,
                    $"The declared model satisfies the rule: the {fact.Kind} '{fact.Name}' on the path of {requirements} is {fact.Status.ToApiString()} and is not stale.",
                    "The result is a concern when the fact is inferred, unknown or stale.", Severity.Blocking)
                : new(RuleResult.Concern, id, cites,
                    $"The {fact.Kind} '{fact.Name}' {problem}, and the approved promise {requirements} stands on it.",
                    "The result is verified when a person confirms the fact, or when a complete scan finds its evidence again.", Severity.Blocking);
        }
    }

    private static IEnumerable<RuleOutcome> PromiseEvidence(ReviewFacts facts)
    {
        foreach (var promise in facts.View.Promises)
        {
            var id = promise.Id;
            if (promise.State != PromiseLifecycle.Approved)
            {
                yield return new(RuleResult.NotApplicable, id, [],
                    $"{promise.RequirementId} is {promise.State}. It is not in force, so it needs no evidence.",
                    "The rule applies when a revision of the promise is approved and in force.");
                continue;
            }

            // The status is the one the Promises module computed for the pinned inputs.
            var status = promise.Current.EvidenceStatus;
            var stale = status.Obligations.Where(o => o.Result is RunResult.Passed or RunResult.Failed && o.Current != true).Select(o => o.ObligationId).ToList();
            var missing = status.Obligations.Where(o => o.Result is RunResult.NotRun or RunResult.Unsupported).Select(o => o.ObligationId).ToList();
            var parts = new List<string>();
            if (status.Obligations.Count == 0) parts.Add("it has no obligation");
            if (status.Counts.Failed > 0) parts.Add($"{Count(status.Counts.Failed, "obligation")} failed the newest check");
            if (stale.Count > 0) parts.Add($"the newest result of {string.Join(", ", stale)} is for inputs that moved");
            if (missing.Count > 0) parts.Add($"{string.Join(", ", missing)} has no result");
            if (status.Gaps.Count > 0) parts.Add($"its status lists {Count(status.Gaps.Count, "gap")}");

            // The sentence counts the failed obligations and the gaps; the fingerprint holds each of them.
            string[] basis =
            [
                .. status.Obligations.Select(o => $"{o.ObligationId} {o.Result} {(o.Current == true ? "current" : "not current")}").Order(StringComparer.Ordinal),
                .. status.Gaps.Order(StringComparer.Ordinal),
            ];
            yield return parts.Count == 0
                ? new(RuleResult.Verified, id, [],
                    $"The declared model satisfies the rule: every obligation of {promise.RequirementId} has a result that passed on the pinned inputs. Each result covers only what its check names.",
                    "The result is a concern when a result is missing, failed or for inputs that moved.")
                : new(RuleResult.Concern, id, [],
                    $"The evidence of {promise.RequirementId} is not current: {string.Join("; ", parts)}.",
                    "The result is verified when every obligation has a passed result on the pinned inputs and the status has no gap.", Basis: basis);
        }
    }
}
