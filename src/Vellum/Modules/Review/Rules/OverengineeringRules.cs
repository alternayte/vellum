using Vellum.Modules.Modelling;
using Vellum.Modules.Trace.Engine;
using static Vellum.Modules.Review.Rules.ResilienceRules;

namespace Vellum.Modules.Review.Rules;

/// <summary>
/// The rules that argue the other way: against a service, a queue or a part that the model
/// gives no reason for. A concern of these rules is a prompt for a decision, not a defect.
/// An accepted decision that names the rule and the subject settles it.
/// </summary>
internal static class OverengineeringRules
{
    /// <summary>The last sentence of every concern of this category.</summary>
    public const string Prompt = "This is a prompt for a decision: an accepted decision that names the rule and this subject settles it.";

    public const string QueueQuestion = "What does the queue buy here?";

    private const string DecisionSettles = "Change the model, or answer the question in a decision and accept it.";

    public static readonly Rule[] All =
    [
        new("OVR-001", 2, RuleCategory.Overengineering, "An app does more than pass a call on",
            "Each app: the scenario steps that call it, that it calls, publishes and writes, the operations, channels, messages and stores those steps name, and the subscriptions it consumes.",
            DecisionSettles, PassThrough),
        new("OVR-002", 2, RuleCategory.Overengineering, "An asynchronous hop inside one system has a stated benefit",
            "Each channel that a scenario publishes to: the publisher, the one consumer behind it and the bindings that lead to it, their parent system, the retry and dead-letter channel of the subscription, and synchronous calls between the two in the same scenario.",
            DecisionSettles, QueueWithoutBenefit),
        new("OVR-003", 2, RuleCategory.Overengineering, "A system has no more apps than its data and its actors explain",
            "Each system: the apps and the stores it holds, and the actors that call it or have a relationship with it, with those operations and relationships.",
            DecisionSettles, AppCount),
        new("OVR-004", 2, RuleCategory.Overengineering, "A channel has a subscription",
            "Each channel: the subscriptions on it and behind its bindings, with those bindings and the channels behind them.",
            DecisionSettles, ChannelWithoutSubscription),
        new("OVR-005", 2, RuleCategory.Overengineering, "An element is used",
            "Each element: its operations, subscriptions, messages, relationships, children and the scenario steps that name it. One of them that is confirmed is enough.",
            DecisionSettles, UnusedElement),
    ];

    private static IEnumerable<RuleOutcome> PassThrough(ReviewFacts facts)
    {
        var calls = facts.Calls.Where(c => facts.Model.Operations.ContainsKey(c.Step.Operation))
            .Select(c => (Caller: c.Step.From.Value, Operation: c.Step.Operation.Value, Callee: facts.Model.Operations[c.Step.Operation].OwnerId.Value)).ToList();
        foreach (var app in facts.View.Elements.Where(e => e.Kind == ElementKind.App))
        {
            Guid id = app.Id;
            var inbound = calls.Where(c => c.Callee == id && c.Caller != id).ToList();
            var outbound = calls.Where(c => c.Caller == id && c.Callee != id).ToList();
            var callers = inbound.Select(c => c.Caller).Distinct().Order().ToList();
            var callees = outbound.Select(c => c.Callee).Distinct().Order().ToList();
            var operations = inbound.Concat(outbound).Select(c => c.Operation).Distinct().Order().ToList();
            // What the app does besides passing a call on: it publishes, it writes or it consumes.
            var work = facts.Publishes.Where(p => p.Step.From.Value == id).SelectMany(p => new[] { p.Step.Channel.Value, p.Step.Message.Value })
                .Concat(facts.Writes.Where(w => w.Step.By.Value == id).Select(w => w.Step.Store.Value))
                .Concat(facts.View.Subscriptions.Where(s => s.ConsumerId.Value == id).Select(s => s.Id.Value))
                .Distinct().Order().ToList();
            var other = work.Count > 0;
            Guid[] read = [.. callers, .. callees, .. operations, .. facts.Facts(work)];
            if (callers.Count == 0 && callees.Count == 0 && !other)
            {
                yield return new(RuleResult.Unknown, id, [],
                    $"No scenario shows traffic of the app '{app.Name}', so the model does not show what it does.",
                    "Add a scenario with the calls, messages and writes of the app.");
            }
            else if (facts.DoubtAmong([id, .. read]) is { } doubt)
                yield return new(RuleResult.Unknown, id, read, $"The rule does not read the traffic of the app '{app.Name}': {doubt}.", "Confirm the fact, or correct it.");
            else if (callers.Count == 1 && callees.Count == 1 && !other)
            {
                yield return new(RuleResult.Concern, id, read,
                    $"In the scenarios the app '{app.Name}' has one synchronous caller ('{facts.Name(callers[0])}') and one callee ('{facts.Name(callees[0])}'), and it publishes, consumes and writes nothing: it passes the call on. {Prompt}",
                    "The result is verified when the app has a second caller or callee, or publishes, consumes or writes.",
                    Question: $"What does '{app.Name}' add between '{facts.Name(callers[0])}' and '{facts.Name(callees[0])}'?");
            }
            else
            {
                yield return new(RuleResult.Verified, id, read,
                    $"The declared model satisfies the rule: the app '{app.Name}' has {Count(callers.Count, "caller")} and {Count(callees.Count, "callee")} in the scenarios{(other ? ", and it publishes, consumes or writes" : "")}.",
                    "The result is a concern when the app has one caller and one callee and does nothing else.");
            }
        }
    }

    private static IEnumerable<RuleOutcome> QueueWithoutBenefit(ReviewFacts facts)
    {
        var published = facts.Publishes.ToLookup(p => p.Step.Channel.Value);
        foreach (var channel in facts.View.Channels)
        {
            Guid id = channel.Id;
            var consumers = facts.ReachableSubscriptions(channel.Id);
            var publishers = published[id].Select(p => p.Step.From.Value).Distinct().Order().ToList();
            if (consumers.Count != 1)
            {
                yield return new(RuleResult.NotApplicable, id, [],
                    $"The channel '{channel.Name}' has {Count(consumers.Count, "subscription")} behind it. The rule reads a channel with exactly one.",
                    "The rule applies to a channel with exactly one subscription behind it.");
                continue;
            }

            var subscription = consumers[0];
            if (publishers.Count == 0)
            {
                yield return new(RuleResult.Unknown, id, [subscription.Id],
                    $"No scenario publishes to the channel '{channel.Name}', so the model does not show who is on the other side of its one subscription.",
                    "Add a scenario with the publish step.");
                continue;
            }

            var consumer = subscription.ConsumerId.Value;
            Guid[] hop =
            [
                .. publishers, subscription.Id, consumer, .. facts.Route(channel.Id),
                .. new Guid?[] { subscription.DeadLetterChannelId?.Value }.OfType<Guid>(),
            ];
            if (facts.DoubtAmong([id, .. hop]) is { } doubt)
            {
                yield return new(RuleResult.Unknown, id, hop, $"The rule does not read the hop over the channel '{channel.Name}': {doubt}.", "Confirm the fact, or correct it.");
                continue;
            }

            var inside = publishers.Where(p => SameSystemApps(facts.Model, p, consumer)).ToList();
            if (inside.Count == 0)
            {
                yield return new(RuleResult.NotApplicable, id, hop,
                    $"The publisher and the consumer of the channel '{channel.Name}' are not two apps of one system.",
                    "The rule applies when one app publishes and another app of the same system is the only consumer.");
                continue;
            }

            // The publisher also needs the answer now: it calls the consumer in the same
            // scenario, or the handler of the consumer calls the publisher back.
            var answers = published[id].Where(p => inside.Contains(p.Step.From.Value)).SelectMany(p =>
                    facts.Calls.Where(c => c.Scenario.Id == p.Scenario.Id && facts.Model.Operations.TryGetValue(c.Step.Operation, out var operation)
                        && ((c.Step.From.Value == p.Step.From.Value && operation.OwnerId.Value == consumer)
                            || (c.Step.From.Value == consumer && operation.OwnerId.Value == p.Step.From.Value))))
                .Select(c => c.Step.Operation.Value).Distinct().Order().ToList();
            Guid system = facts.Model.Elements[consumer].ParentId!.Value;
            Guid[] cites = [.. hop, system, .. answers];
            if (facts.DoubtAmong([system, .. answers]) is { } other)
            {
                yield return new(RuleResult.Unknown, id, cites, $"The rule does not read the hop over the channel '{channel.Name}': {other}.", "Confirm the fact, or correct it.");
                continue;
            }

            var synchronous = answers.Count > 0;
            var unhandled = subscription.Retry.MaxAttempts <= 1 && subscription.DeadLetterChannelId is null;
            var between = $"'{facts.Name(inside[0])}' and '{facts.Name(consumer)}'";
            if (synchronous || unhandled)
            {
                var why = synchronous
                    ? "the same scenario also has a synchronous call between the two, so the publisher needs the answer at once"
                    : "the subscription declares no retry and no dead-letter channel, so the hop gives no failure handling";
                yield return new(RuleResult.Concern, id, cites,
                    $"The channel '{channel.Name}' is an asynchronous hop between {between}, two apps of one system, with one consumer, and {why}. {Prompt}",
                    "The result is verified when the subscription declares a retry or a dead-letter channel and no scenario also calls between the two synchronously.",
                    Question: QueueQuestion);
            }
            else
            {
                yield return new(RuleResult.Verified, id, cites,
                    $"The declared model satisfies the rule: the hop over the channel '{channel.Name}' between {between} declares failure handling, and no scenario also calls between the two synchronously.",
                    "The result is a concern when the hop has no failure handling, or a scenario also needs the answer synchronously.");
            }
        }
    }

    private static bool SameSystemApps(ModelState model, Guid first, Guid second) =>
        first != second
        && model.Elements.TryGetValue(first, out var a) && model.Elements.TryGetValue(second, out var b)
        && a.Kind == ElementKind.App && b.Kind == ElementKind.App
        && a.ParentId is { } parent && b.ParentId == parent;

    private static IEnumerable<RuleOutcome> AppCount(ReviewFacts facts)
    {
        var actors = facts.View.Elements.Where(e => e.Kind == ElementKind.Actor).Select(e => e.Id.Value).ToHashSet();
        foreach (var system in facts.View.Elements.Where(e => e.Kind == ElementKind.System))
        {
            Guid id = system.Id;
            var children = facts.View.Elements.Where(e => e.ParentId == system.Id).Select(e => (e.Kind, Id: e.Id.Value)).ToList();
            var apps = children.Where(e => e.Kind == ElementKind.App).Select(e => e.Id).ToList();
            var stores = children.Where(e => e.Kind == ElementKind.Store).Select(e => e.Id).ToList();
            var inside = children.Select(e => e.Id).Append(id).ToHashSet();
            // An actor is a user of the system through a call of a scenario or through a relationship.
            var calls = facts.Calls
                .Where(c => actors.Contains(c.Step.From) && facts.Model.Operations.TryGetValue(c.Step.Operation, out var operation) && inside.Contains(operation.OwnerId))
                .ToList();
            var relationships = facts.View.Relationships
                .Where(r => (actors.Contains(r.FromId) && inside.Contains(r.ToId)) || (actors.Contains(r.ToId) && inside.Contains(r.FromId))).ToList();
            var users = calls.Select(c => c.Step.From.Value)
                .Concat(relationships.Select(r => actors.Contains(r.FromId) ? r.FromId.Value : r.ToId.Value))
                .Distinct().Order().ToList();
            var through = calls.Select(c => c.Step.Operation.Value).Concat(relationships.Select(r => r.Id.Value)).Distinct().Order().ToList();
            // Every child is read: a child that is no app and no store is counted as neither.
            Guid[] read = [.. apps, .. stores, .. users, .. through, .. children.Where(e => e.Kind is not (ElementKind.App or ElementKind.Store)).Select(e => e.Id)];
            var numbers = $"{Count(apps.Count, "app")}, {Count(stores.Count, "store")} and {Count(users.Count, "actor")}";
            if (apps.Count == 0)
            {
                yield return new(RuleResult.NotApplicable, id, [], $"The system '{system.Name}' holds no app.", "The rule applies to a system that holds an app.", Severity.Note);
            }
            else if (facts.DoubtAmong([id, .. read]) is { } doubt)
            {
                yield return new(RuleResult.Unknown, id, read, $"The rule does not count the parts of the system '{system.Name}': {doubt}.", "Confirm the fact, or correct it.", Severity.Note);
            }
            else if (apps.Count > stores.Count + users.Count)
            {
                yield return new(RuleResult.Concern, id, read,
                    $"The system '{system.Name}' holds {numbers}: more apps than its stores and its actors together. {Prompt}",
                    "The result is verified when the system has at most as many apps as stores and actors together.",
                    Severity.Note, $"What does each of the {apps.Count} apps of '{system.Name}' own that another app cannot?");
            }
            else
            {
                yield return new(RuleResult.Verified, id, read,
                    $"The declared model satisfies the rule: the system '{system.Name}' holds {numbers}.",
                    "The result is a concern when the system has more apps than stores and actors together.", Severity.Note);
            }
        }
    }

    private static IEnumerable<RuleOutcome> ChannelWithoutSubscription(ReviewFacts facts)
    {
        foreach (var channel in facts.View.Channels)
        {
            Guid id = channel.Id;
            var route = facts.Route(channel.Id);
            var reached = facts.ReachableSubscriptions(channel.Id);
            Guid[] read = [.. route, .. reached.Select(s => s.Id.Value)];
            if (facts.DoubtAmong([id, .. read]) is { } doubt)
                yield return new(RuleResult.Unknown, id, read, $"The rule does not read the channel '{channel.Name}': {doubt}.", "Confirm the fact, or correct it.");
            else if (reached.Count == 0)
            {
                yield return new(RuleResult.Concern, id, route,
                    $"The channel '{channel.Name}' has no subscription on it or behind its bindings, so nothing in the model consumes it. {Prompt}",
                    "The result is verified when a subscription is on the channel or behind its bindings.",
                    Question: $"Who reads '{channel.Name}'?");
            }
            else
            {
                yield return new(RuleResult.Verified, id, read,
                    $"The declared model satisfies the rule: the channel '{channel.Name}' has {Count(reached.Count, "subscription")} on it or behind its bindings.",
                    "The result is a concern when no subscription can be reached from the channel.");
            }
        }
    }

    private static IEnumerable<RuleOutcome> UnusedElement(ReviewFacts facts)
    {
        var model = facts.Model;
        // What names each element: a fact of the model, or a scenario with a step that names it.
        var names = model.Operations.Values.Select(o => (Element: o.OwnerId.Value, By: o.Id.Value))
            .Concat(model.Subscriptions.Values.Select(s => (Element: s.ConsumerId.Value, By: s.Id.Value)))
            .Concat(model.Messages.Values.SelectMany(m => m.ConsumerIds.Select(c => c.Value).Append(m.ProducerId.Value).Select(e => (Element: e, By: m.Id.Value))))
            .Concat(model.Relationships.Values.SelectMany(r => new[] { (Element: r.FromId.Value, By: r.Id.Value), (Element: r.ToId.Value, By: r.Id.Value) }))
            .Concat(model.Elements.Values.Where(e => e.ParentId is not null).Select(e => (Element: e.ParentId!.Value.Value, By: e.Id.Value)))
            .Concat(facts.Calls.Select(c => (Element: c.Step.From.Value, By: c.Scenario.Id.Value)))
            .Concat(facts.Publishes.Select(p => (Element: p.Step.From.Value, By: p.Scenario.Id.Value)))
            .Concat(facts.Writes.SelectMany(w => new[] { (Element: w.Step.By.Value, By: w.Scenario.Id.Value), (Element: w.Step.Store.Value, By: w.Scenario.Id.Value) }))
            .ToLookup(n => n.Element, n => n.By);
        foreach (var element in facts.View.Elements)
        {
            Guid id = element.Id;
            var kind = element.Kind.ToString().ToLowerInvariant();
            var by = names[id].Distinct().Order().ToList();
            // A scenario has no truth label: a person wrote it. One confirmed name is enough.
            var confirmed = by.Where(n => facts.Fact(n) is null || facts.Doubt(n) is null).ToList();
            if (facts.Doubt(id) is { } doubt)
                yield return new(RuleResult.Unknown, id, [], $"The rule does not read the {kind} '{element.Name}': {doubt}.", "Confirm the fact, or correct it.");
            else if (confirmed.Count > 0)
            {
                yield return new(RuleResult.Verified, id, [confirmed[0]],
                    $"The declared model satisfies the rule: the {kind} '{element.Name}' is named by a contract, a relationship, a child or a scenario step.",
                    "The result is a concern when nothing in the model names the element.");
            }
            else if (by.Count > 0)
            {
                yield return new(RuleResult.Unknown, id, by,
                    $"The rule does not read what names the {kind} '{element.Name}': {facts.Doubt(by[0])}.",
                    "Confirm one of the facts that name the element, or correct it.");
            }
            else
            {
                yield return new(RuleResult.Concern, id, [],
                    $"The {kind} '{element.Name}' has no operation, subscription, message, relationship or child, and no scenario step names it. {Prompt}",
                    "The result is verified when the element serves an operation, consumes or produces a message, has a relationship or a child, or a scenario step names it.",
                    Question: $"What is '{element.Name}' for?");
            }
        }
    }
}
