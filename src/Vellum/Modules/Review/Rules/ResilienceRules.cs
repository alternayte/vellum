using Vellum.Modules.Modelling;
using Vellum.Modules.Trace.Engine;

namespace Vellum.Modules.Review.Rules;

/// <summary>The rules that look for a missing answer to failure.</summary>
internal static class ResilienceRules
{
    /// <summary>
    /// A chain of synchronous calls is deeper than this when a callee calls on while its own
    /// caller waits: two calls or more, one inside the other.
    /// </summary>
    public const int MaxChainDepthWithoutTimeouts = 1;

    public static readonly Rule[] All =
    [
        new("REL-001", 2, RuleCategory.Resilience, "A failed delivery has somewhere to go",
            "Each subscription: its channel, its consumer, its retry attempts and its dead-letter channel.",
            "Give the subscription more than one attempt or a dead-letter channel, or accept the loss in a decision.",
            DeliveryLoss),
        new("REL-002", 2, RuleCategory.Resilience, "A chain of synchronous calls has a timeout on every hop",
            "Each scenario: the call steps that run inside another call, and the timeout of each operation in the chain.",
            "Declare a timeout on each operation of the chain, or record why the chain may wait without limit.",
            UnboundedChains),
        new("REL-003", 1, RuleCategory.Resilience, "A retried operation is safe to repeat",
            "Each operation: its retry attempts. The model holds no idempotency fact for an operation.",
            "The model cannot state idempotency, so the result stays unknown. Record in a decision how a repeated call is made harmless.",
            RetryWithoutIdempotency),
        new("REL-004", 2, RuleCategory.Resilience, "A published message can reach a subscription",
            "Each channel that a scenario publishes to (a topic, an exchange or a queue), its bindings and the subscriptions on it and behind them.",
            "Add a subscription or a binding that leads to one, or remove the publish step.",
            UnreachablePublish),
        new("REL-005", 2, RuleCategory.Resilience, "A dead-letter channel has a consumer",
            "Each channel that a subscription names as its dead-letter channel, those subscriptions, and the subscriptions on it and behind its bindings.",
            "Add a subscription to the dead-letter channel, or record who reads it outside the model.",
            UnreadDeadLetters),
        new("REL-006", 2, RuleCategory.Resilience, "A binding with a routing key passes a published message",
            "Each binding with a routing key, the routing keys of the scenario steps that publish to its source, and the channels and bindings between such a step and the binding.",
            "State the routing key on a publish step that matches the binding, or correct the key of the binding.",
            UnmatchedBindings),
    ];

    private static IEnumerable<RuleOutcome> DeliveryLoss(ReviewFacts facts)
    {
        foreach (var subscription in facts.View.Subscriptions)
        {
            Guid id = subscription.Id;
            var name = facts.Name(id);
            if (facts.Doubt(id, subscription.ChannelId, subscription.ConsumerId, subscription.DeadLetterChannelId?.Value) is { } doubt)
            {
                yield return new(RuleResult.Unknown, id, [subscription.ChannelId, subscription.ConsumerId],
                    $"The rule does not read the subscription '{name}': {doubt}.",
                    "Confirm the fact, or correct it.");
            }
            else if (subscription.Retry.MaxAttempts <= 1 && subscription.DeadLetterChannelId is null)
            {
                yield return new(RuleResult.Concern, id, [subscription.ChannelId, subscription.ConsumerId],
                    $"The subscription '{name}' has one delivery attempt and no dead-letter channel, so a message whose delivery fails is lost.",
                    "The result is verified when the subscription has more than one attempt or a dead-letter channel.");
            }
            else
            {
                var has = subscription.DeadLetterChannelId is { } deadLetter
                    ? $"the dead-letter channel '{facts.Name(deadLetter)}'"
                    : $"{subscription.Retry.MaxAttempts} delivery attempts";
                yield return new(RuleResult.Verified, id,
                    [.. new Guid?[] { subscription.ChannelId, subscription.ConsumerId, subscription.DeadLetterChannelId?.Value }.OfType<Guid>()],
                    $"The declared model satisfies the rule: the subscription '{name}' has {has}.",
                    "The result is a concern when the subscription has one attempt and no dead-letter channel.");
            }
        }
    }

    private static IEnumerable<RuleOutcome> UnboundedChains(ReviewFacts facts)
    {
        foreach (var scenario in facts.View.Scenarios)
        {
            var chains = new List<List<CallStep>>();
            Collect(scenario.Steps, [], chains);
            var deep = chains.Where(c => c.Count > MaxChainDepthWithoutTimeouts).ToList();
            Guid id = scenario.Id;
            if (deep.Count == 0)
            {
                yield return new(RuleResult.NotApplicable, id, [],
                    $"The scenario '{scenario.Name}' has no call that runs inside another call.",
                    "The rule applies when a callee calls another operation while its caller waits.");
                continue;
            }

            var operations = deep.SelectMany(c => c.Select(s => s.Operation.Value)).Distinct().ToList();
            var cites = operations.Where(o => facts.Model.Operations.ContainsKey(o)).Order().ToList();
            if (facts.Doubt([.. operations.Select(o => (Guid?)o)]) is { } doubt)
            {
                yield return new(RuleResult.Unknown, id, cites,
                    $"The rule does not read a call chain of the scenario '{scenario.Name}': {doubt}.",
                    "Confirm the fact, or correct it.");
                continue;
            }

            // A timeout of 0 declares none. On an operation that a person declared, that is the
            // statement "no timeout". On an operation read from source it means that the import
            // read no timeout, which is no statement about the system.
            var none = operations.Select(o => facts.Model.Operations[o]).Where(o => o.TimeoutMs == 0).OrderBy(o => o.Id.Value).ToList();
            var unread = none.Where(o => o.FactStatus != FactStatus.Declared).ToList();
            var depth = deep.Max(c => c.Count);
            // Two operations can have one name, so the fingerprint holds the IDs.
            string[] basis = [.. none.Select(o => $"{o.Id.Value} {(o.FactStatus == FactStatus.Declared ? "declares no timeout" : "timeout not stated")}")];
            if (none.Count == 0)
            {
                yield return new(RuleResult.Verified, id, cites,
                    $"The declared model satisfies the rule: every operation in the call chains of the scenario '{scenario.Name}' (deepest: {depth} calls) declares a timeout.",
                    "The result is a concern when an operation of a chain declares no timeout.");
            }
            else if (unread.Count == none.Count)
            {
                yield return new(RuleResult.Unknown, id, cites,
                    $"The scenario '{scenario.Name}' has a chain of {depth} synchronous calls, and the timeout of {Names(facts, unread)} is not stated: an import does not read a timeout.",
                    "State the timeout of each operation, or declare the operation.", Basis: basis);
            }
            else
            {
                yield return new(RuleResult.Concern, id, cites,
                    $"The scenario '{scenario.Name}' has a chain of {depth} synchronous calls, and {Names(facts, none.Except(unread))} declares no timeout, so a slow callee holds every caller above it.",
                    "The result is verified when every operation of the chain declares a timeout.", Basis: basis);
            }
        }

        // A chain is the calls that are open at the same time: a call and the calls its callee makes before it answers.
        static void Collect(IReadOnlyList<Step>? steps, List<CallStep> open, List<List<CallStep>> chains)
        {
            foreach (var step in steps ?? [])
            {
                switch (step)
                {
                    case CallStep call:
                        List<CallStep> chain = [.. open, call];
                        chains.Add(chain);
                        Collect(call.Then, chain, chains);
                        // The caller handles the failure after the call ended.
                        Collect(call.OnError, open, chains);
                        break;
                    case ParallelStep parallel:
                        foreach (var branch in parallel.Branches ?? [])
                            Collect(branch, open, chains);
                        break;
                    case BranchStep branch:
                        Collect(branch.Then, open, chains);
                        Collect(branch.Else, open, chains);
                        break;
                    // A consumer runs after the publisher went on, so a handler starts a new chain.
                    case PublishStep publish:
                        foreach (var handler in publish.Handlers?.Values ?? [])
                            Collect(handler, [], chains);
                        break;
                }
            }
        }
    }

    private static IEnumerable<RuleOutcome> RetryWithoutIdempotency(ReviewFacts facts)
    {
        foreach (var operation in facts.View.Operations)
        {
            Guid id = operation.Id;
            yield return operation.Retry.MaxAttempts > 1
                ? new(RuleResult.Unknown, id, [operation.OwnerId],
                    $"The operation '{operation.Name}' is tried up to {operation.Retry.MaxAttempts} times, and the model holds no fact that says whether a repeated call is harmless.",
                    "The model cannot state idempotency. Record in a decision how a repeated call is made harmless, or remove the retry.")
                : new(RuleResult.NotApplicable, id, [operation.OwnerId],
                    $"The operation '{operation.Name}' declares no retry.",
                    "The rule applies when the operation has more than one attempt.");
        }
    }

    private static IEnumerable<RuleOutcome> UnreachablePublish(ReviewFacts facts)
    {
        var published = facts.Publishes.ToLookup(p => p.Step.Channel.Value);
        foreach (var channel in facts.View.Channels)
        {
            Guid id = channel.Id;
            var steps = published[id].ToList();
            if (steps.Count == 0)
            {
                yield return new(RuleResult.NotApplicable, id, [],
                    $"No scenario publishes to the channel '{channel.Name}'.",
                    "The rule applies to a channel that a scenario publishes to.");
                continue;
            }

            var kind = channel.Kind.ToString().ToLowerInvariant();
            var scenarios = steps.Select(s => s.Scenario.Id.Value).Distinct().Order().ToList();
            // Blocking only when the publish is part of a scenario that an approved promise links.
            var severity = scenarios.Any(facts.IsOnPath) ? Severity.Blocking : Severity.Warning;
            var route = facts.Route(channel.Id);
            var reached = facts.ReachableSubscriptions(channel.Id);
            Guid[] read = [.. route, .. reached.Select(s => s.Id.Value)];
            if (facts.DoubtAmong([id, .. read]) is { } doubt)
            {
                yield return new(RuleResult.Unknown, id, scenarios,
                    $"The rule does not read the routes of the channel '{channel.Name}': {doubt}.",
                    "Confirm the fact, or correct it.", severity);
            }
            else if (reached.Count == 0)
            {
                // A queue keeps the message, and the model still shows nothing that takes it.
                yield return new(RuleResult.Concern, id, [.. scenarios, .. route],
                    $"Steps of {Count(scenarios.Count, "scenario")} publish to the {kind} '{channel.Name}', and no subscription is on it or behind its bindings, so nothing receives the message.{facts.PathNote(scenarios.FirstOrDefault(facts.IsOnPath))}",
                    "The result is verified when a subscription is on the channel or on a channel its bindings lead to.", severity);
            }
            else
            {
                yield return new(RuleResult.Verified, id, [.. scenarios, .. read],
                    $"The declared model satisfies the rule: {Count(reached.Count, "subscription")} can be reached from the {kind} '{channel.Name}'. Routing keys are not read here.",
                    "The result is a concern when no subscription is on the channel or behind its bindings.", severity);
            }
        }
    }

    private static IEnumerable<RuleOutcome> UnreadDeadLetters(ReviewFacts facts)
    {
        var deadLetterOf = facts.View.Subscriptions.Where(s => s.DeadLetterChannelId is not null).ToLookup(s => s.DeadLetterChannelId!.Value.Value);
        foreach (var channel in facts.View.Channels)
        {
            Guid id = channel.Id;
            var sources = deadLetterOf[id].Select(s => s.Id.Value).ToList();
            if (sources.Count == 0)
            {
                yield return new(RuleResult.NotApplicable, id, [],
                    $"No subscription names the channel '{channel.Name}' as its dead-letter channel.",
                    "The rule applies to a channel that a subscription dead-letters to.", Severity.Note);
                continue;
            }

            var readers = facts.ReachableSubscriptions(channel.Id);
            Guid[] read = [.. facts.Route(channel.Id), .. readers.Select(s => s.Id.Value)];
            if (facts.DoubtAmong([id, .. sources, .. read]) is { } doubt)
            {
                yield return new(RuleResult.Unknown, id, sources,
                    $"The rule does not read the dead-letter channel '{channel.Name}': {doubt}.",
                    "Confirm the fact, or correct it.", Severity.Note);
            }
            else if (readers.Count == 0)
            {
                yield return new(RuleResult.Concern, id, sources,
                    $"The channel '{channel.Name}' is the dead-letter channel of {Count(sources.Count, "subscription")}, and the model shows nothing that consumes it.",
                    "The result is verified when a subscription is on the dead-letter channel or behind its bindings.", Severity.Note);
            }
            else
            {
                yield return new(RuleResult.Verified, id, [.. sources, .. read],
                    $"The declared model satisfies the rule: the dead-letter channel '{channel.Name}' has {Count(readers.Count, "subscription")} that can consume it.",
                    "The result is a concern when nothing consumes the dead-letter channel.", Severity.Note);
            }
        }
    }

    private static IEnumerable<RuleOutcome> UnmatchedBindings(ReviewFacts facts)
    {
        foreach (var binding in facts.View.Bindings)
        {
            Guid id = binding.Id;
            var name = facts.Name(id);
            Guid[] cites = [binding.SourceChannelId, binding.TargetChannelId];
            if (binding.RoutingKey is not { } key)
            {
                yield return new(RuleResult.NotApplicable, id, cites,
                    $"The binding '{name}' has no routing key, so it passes every message.",
                    "The rule applies to a binding with a routing key.");
                continue;
            }
            // A message keeps its key along the bindings, so every publish that can reach the source counts.
            var arriving = facts.Publishes
                .Where(p => facts.Model.Channels.ContainsKey(p.Step.Channel) && ChannelRouting.Reachable(facts.Trace, p.Step.Channel).Contains(binding.SourceChannelId))
                .ToList();
            // What such a message passes before it is at the source of the binding.
            var before = arriving.Select(p => p.Step.Channel).Distinct()
                .SelectMany(from => facts.Between(from, binding.SourceChannelId)).Distinct().Order().ToList();
            cites = [.. cites.Union(before)];
            if (facts.DoubtAmong([id, .. cites]) is { } doubt)
            {
                yield return new(RuleResult.Unknown, id, cites, $"The rule does not read the binding '{name}': {doubt}.", "Confirm the fact, or correct it.");
                continue;
            }

            var stated = arriving.Where(p => p.Step.RoutingKey is not null).ToList();
            var scenarios = arriving.Select(p => p.Scenario.Id.Value).Distinct().Order();
            if (stated.Any(p => ChannelRouting.Matches(key, p.Step.RoutingKey)))
            {
                yield return new(RuleResult.Verified, id, [.. cites, .. scenarios],
                    $"The declared model satisfies the rule: a scenario publishes with a routing key that the key '{key}' of the binding '{name}' matches.",
                    "The result is a concern when scenarios state routing keys and none matches the binding.");
            }
            else if (stated.Count == 0)
            {
                yield return new(RuleResult.Unknown, id, [.. cites, .. scenarios],
                    arriving.Count == 0
                        ? $"No scenario publishes to the source of the binding '{name}', so no routing key is stated for the key '{key}' to match."
                        : $"The scenarios that publish to the source of the binding '{name}' state no routing key, so the model does not show whether the key '{key}' matches.",
                    "State the routing key on a publish step that reaches the source of the binding.");
            }
            else
            {
                yield return new(RuleResult.Concern, id, [.. cites, .. scenarios],
                    $"No publish step that reaches the binding '{name}' has a routing key that its key '{key}' matches (stated: {string.Join(", ", stated.Select(p => $"'{p.Step.RoutingKey}'").Distinct().Order(StringComparer.Ordinal))}), so the binding passes nothing in any scenario.",
                    "The result is verified when a publish step states a routing key that the binding matches.");
            }
        }
    }

    internal static string Names(ReviewFacts facts, IEnumerable<OperationState> operations) =>
        string.Join(", ", operations.Select(o => $"'{o.Name}'"));

    /// <summary>"1 scenario" or "3 scenarios".</summary>
    internal static string Count(int count, string noun) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{count} {noun}{(count == 1 ? "" : "s")}");
}
