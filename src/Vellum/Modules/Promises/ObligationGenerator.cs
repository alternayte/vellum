using System.Globalization;
using Vellum.Modules.Modelling;
using Vellum.Modules.Trace.Engine;

namespace Vellum.Modules.Promises;

/// <summary>
/// The default obligations of a draft, made from its links with no AI. The same draft, model
/// and scenarios give the same candidates in the same order:
/// <list type="number">
/// <item>Each linked scenario, in ID order: one <c>scenario-check</c> that plays the scenario
/// with its own faults and expects the scenario's own assertions, then one
/// <c>event-count</c> assertion with <c>expected = 0</c> for each structured forbidden event
/// of the promise (kind and target copied as given). The forbidden outcome text has no exact
/// engine form and maps to nothing. A scenario with no assertion at all gives no check.</item>
/// <item>Each linked subscription: one <c>failure-test</c> plan for redelivery, duplicate
/// delivery and the dead-letter path.</item>
/// <item>Each linked operation: one <c>contract-test</c> plan.</item>
/// <item><c>requiresPattern</c>: one <c>source-pattern-check</c>.</item>
/// </list>
/// A candidate is a proposal inside the draft. A person edits or deletes it before approval.
/// </summary>
public static class ObligationGenerator
{
    /// <param name="Key">Names what the candidate was made for; one obligation per key and draft.</param>
    public sealed record Candidate(string Key, PromiseRules.ObligationInput Input);

    /// <summary>A link that gave no candidate, with the reason.</summary>
    public sealed record Skipped(string Key, string Reason);

    public static (IReadOnlyList<Candidate> Candidates, IReadOnlyList<Skipped> Skipped) Generate(
        PromiseRevisionState draft, ModelState model, IReadOnlyDictionary<Guid, Scenario> scenarios)
    {
        var content = draft.Content;
        var candidates = new List<Candidate>();
        var skipped = new List<Skipped>();

        foreach (var scenarioId in content.Links.ScenarioIds)
        {
            var key = $"{ObligationKind.ScenarioCheck}:{scenarioId}";
            if (!scenarios.TryGetValue(scenarioId, out var scenario))
            {
                skipped.Add(new(key, "The linked scenario no longer exists."));
                continue;
            }

            var expect = new List<Assertion>(scenario.Assertions ?? []);
            var taken = expect.Select(a => a.Id).ToHashSet(StringComparer.Ordinal);
            for (var i = 0; i < content.ForbiddenEvents.Count; i++)
            {
                var forbidden = content.ForbiddenEvents[i];
                var id = string.Create(CultureInfo.InvariantCulture, $"forbidden-{i + 1}-{forbidden.Kind}");
                while (!taken.Add(id))
                    id += "-promise";
                expect.Add(new EventCountAssertion(id, forbidden.Kind, forbidden.Target, 0));
            }

            if (expect.Count == 0)
            {
                skipped.Add(new(key, "The scenario has no assertion and the promise has no structured forbidden event, so there is nothing to check."));
                continue;
            }

            var unmapped = string.IsNullOrWhiteSpace(content.ForbiddenOutcome)
                ? ""
                : " The forbidden outcome text of the promise is not checked by itself: only its structured forbidden events are.";
            candidates.Add(new(key, new(
                ObligationKind.ScenarioCheck,
                Bounded($"Scenario '{scenario.Name}' holds on the declared design"),
                new ObligationRecipe(
                    $"Play scenario '{scenario.Name}' ({scenarioId}) on a pinned model revision with its declared faults. "
                    + $"All {expect.Count} assertion(s) must hold.{unmapped}",
                    ScenarioCheck: new ScenarioCheckRecipe(scenarioId, [], expect)))));
        }

        foreach (var subscriptionId in content.Links.SubscriptionIds)
        {
            var key = $"{ObligationKind.FailureTest}:{subscriptionId}";
            if (!model.Subscriptions.TryGetValue(subscriptionId, out var subscription))
            {
                skipped.Add(new(key, "The linked subscription no longer exists."));
                continue;
            }

            var label = ContractDecider.SubscriptionLabel(model, subscription);
            var attempts = subscription.Retry.MaxAttempts;
            var deadLetter = subscription.DeadLetterChannelId is { } id && model.Channels.TryGetValue(id, out var channel)
                ? $"channel '{channel.Name}' ({id})"
                : null;
            candidates.Add(new(key, new(
                ObligationKind.FailureTest,
                Bounded($"{label}: redelivery, duplicate and dead-letter"),
                new ObligationRecipe(
                    $"Failure test for subscription {subscriptionId} ({label}). Run it against the real consumer and broker.",
                    Plan: new TestPlanRecipe(
                        Steps:
                        [
                            "Publish one valid message to the channel while the consumer is running.",
                            string.Create(CultureInfo.InvariantCulture,
                                $"Make the consumer fail the first delivery. Observe each redelivery up to the declared {attempts} attempt(s)."),
                            "Deliver the same message twice (same message ID) after a successful handling.",
                            deadLetter is null
                                ? "Make every attempt fail. Observe what happens to the message: the contract declares no dead-letter channel."
                                : $"Make every attempt fail. Observe the message on the dead-letter {deadLetter}.",
                        ],
                        Inputs:
                        [
                            "One message that is valid against the payload schema of the channel's message.",
                            "A way to make the consumer fail on demand (fault injection or a failing dependency).",
                        ],
                        ExpectedObservations:
                        [
                            string.Create(CultureInfo.InvariantCulture, $"The consumer is tried at most {attempts} time(s) for one message."),
                            "A redelivered message that is handled later has the same effect as one handled the first time.",
                            "The duplicate delivery changes no state a second time.",
                            deadLetter is null
                                ? "The outcome of a message that fails every attempt is known and recorded."
                                : $"A message that fails every attempt is on the dead-letter {deadLetter} with its payload unchanged.",
                        ],
                        ForbiddenObservations:
                        [
                            "A message is lost with no record of it.",
                            "A duplicate delivery repeats a side effect.",
                        ],
                        ContractIds: subscription.DeadLetterChannelId is { } dlq
                            ? [subscriptionId, subscription.ChannelId, dlq]
                            : [subscriptionId, subscription.ChannelId])))));
        }

        foreach (var operationId in content.Links.OperationIds)
        {
            var key = $"{ObligationKind.ContractTest}:{operationId}";
            if (!model.Operations.TryGetValue(operationId, out var operation))
            {
                skipped.Add(new(key, "The linked operation no longer exists."));
                continue;
            }

            var owner = model.Elements.TryGetValue(operation.OwnerId, out var element) ? element.Name : operation.OwnerId.ToString();
            candidates.Add(new(key, new(
                ObligationKind.ContractTest,
                Bounded($"{owner}: {operation.Name} keeps its contract"),
                new ObligationRecipe(
                    $"Contract test for operation {operationId} ('{operation.Name}' of {owner}, {operation.Transport.ToString().ToLowerInvariant()}). Run it against the real service.",
                    Plan: new TestPlanRecipe(
                        Steps:
                        [
                            operation.RequestSchemaId is { } request
                                ? $"Send a request that is valid against schema {request.Value}."
                                : "Send a valid request. The contract declares no request schema.",
                            operation.RequestSchemaId is null
                                ? "Send a malformed request."
                                : "Send a request that breaks the request schema.",
                            "Compare the response with the declared response contract.",
                        ],
                        Inputs:
                        [
                            "One valid request fixture and one invalid request fixture.",
                        ],
                        ExpectedObservations:
                        [
                            operation.ResponseSchemaId is { } response
                                ? $"The response to the valid request is valid against schema {response.Value}."
                                : "The response to the valid request has the declared shape. The contract declares no response schema.",
                            "The invalid request is refused and changes no state.",
                        ],
                        ForbiddenObservations:
                        [
                            "The service accepts a request that the contract forbids.",
                        ],
                        ContractIds: [operationId])))));
        }

        if (content.RequiresPattern is { } pattern)
        {
            var service = pattern.Service is { } serviceId
                ? $" for service {(model.Elements.TryGetValue(serviceId, out var element) ? $"'{element.Name}' " : "")}({serviceId})"
                : "";
            candidates.Add(new(
                $"{ObligationKind.SourcePatternCheck}:{pattern.Repository}:{pattern.Pattern}:{pattern.Service?.ToString() ?? "-"}",
                new(
                    ObligationKind.SourcePatternCheck,
                    Bounded($"{pattern.Repository} declares the {pattern.Pattern} pattern"),
                    new ObligationRecipe(
                        $"Look at one import snapshot of {pattern.Repository}. A fact with pattern '{pattern.Pattern}'{service} must exist at that commit.",
                        SourcePattern: new SourcePatternRecipe(pattern.Repository, pattern.Pattern, pattern.Service)))));
        }

        return (candidates, skipped);
    }

    /// <summary>A title inside the name limit. The cut never leaves half of a surrogate pair.</summary>
    private static string Bounded(string title)
    {
        const int max = Vellum.Shared.TextRule.MaxName;
        if (title.Length <= max)
            return title;
        return char.IsHighSurrogate(title[max - 1]) ? title[..(max - 1)] : title[..max];
    }
}
