using System.Globalization;
using Vellum.Modules.Modelling;
using Vellum.Modules.Promises;
using Vellum.Modules.Trace.Engine;

namespace Vellum.Modules.Diagrams;

public enum SequenceParticipantKind { Actor, Element, Channel, Scenario }

/// <summary>One lifeline: an element, a channel, or the scenario itself when nothing else is named.</summary>
public sealed record SequenceParticipant(Guid Id, string Name, SequenceParticipantKind Kind, ElementKind? ElementKind = null, string? Detail = null);

/// <summary>A request (<c>-&gt;&gt;</c>), its response (<c>--&gt;&gt;</c>) or a message that nobody waits for (<c>-)</c>).</summary>
public enum SequenceArrow { Call, Reply, Async }

public abstract record SequenceItem;

/// <summary>An arrow between two participants, by their index in <see cref="ScenarioSequence.Participants"/>.</summary>
public sealed record SequenceMessage(int From, int To, SequenceArrow Arrow, string Text) : SequenceItem;

/// <summary>A note over the participants from <paramref name="From"/> to <paramref name="To"/>.</summary>
public sealed record SequenceNote(int From, int To, string Text) : SequenceItem;

public enum SequenceBlockKind { Alt, Par }

public sealed record SequenceSection(string Label, IReadOnlyList<SequenceItem> Items);

/// <summary>An <c>alt</c> or a <c>par</c> block. Every section has at least one item.</summary>
public sealed record SequenceBlock(SequenceBlockKind Kind, IReadOnlyList<SequenceSection> Sections) : SequenceItem;

/// <summary>
/// A scenario as a sequence of arrows between lifelines. The Mermaid and the SVG export are
/// both drawn from it, so they show the same thing. The mapping is fixed:
/// <list type="bullet">
/// <item>A participant for every element and channel that a step names, in the order of first
/// use. An actor is an actor.</item>
/// <item>A call is a request from the caller to the owner of the operation, the steps of
/// <c>then</c>, and the response. Its <c>onError</c> steps are an <c>alt</c> block, labelled
/// with the faults that the scenario injects on the operation or its owner.</item>
/// <item>A publish is a message to the channel, a message along each binding that passes it
/// (by ID, as the engine routes), and a delivery to each subscription it reaches, followed by
/// the handler of that subscription. Two or more deliveries are a <c>par</c> block.</item>
/// <item>A write is a request from the writer to the store, with the dataset as its text.</item>
/// <item>A parallel step is a <c>par</c> block; a branch step is an <c>alt</c> block.</item>
/// <item>Faults are notes at the start; assertions are notes at the end.</item>
/// </list>
/// A reference that the model does not hold is named as unknown, with the start of its ID.
/// The same scenario version and the same model give the same sequence.
/// </summary>
public sealed record ScenarioSequence(
    Guid ScenarioId,
    string ScenarioName,
    long ScenarioVersion,
    ModelPin Pin,
    IReadOnlyList<SequenceParticipant> Participants,
    IReadOnlyList<SequenceItem> Items)
{
    /// <summary>What the Mermaid and the SVG export of a scenario do not show.</summary>
    public const string Dropped =
        "payloads, headers, routing keys, schemas, transports, timeouts, retry policies, dead-letter routes, " +
        "the result of a playback (outcomes, attempts, timing and the routes that a fault closes), " +
        "truth labels and their provenance, promises, costs and comments";

    /// <summary>A sentence that names the scenario and the model a file was made from.</summary>
    public string SourceText() => string.Create(CultureInfo.InvariantCulture,
        $"steps of scenario {ScenarioId} at version {ScenarioVersion} (a scenario is not part of a revision), names from {ViewDiagram.PinText(Pin)}");

    public static ScenarioSequence Build(Scenario scenario, long scenarioVersion, PinnedModel model)
    {
        var builder = new Builder(model.State, scenario);
        var items = builder.Run();
        return new ScenarioSequence(scenario.Id.Value, scenario.Name ?? "", scenarioVersion, model.Pin, builder.Participants, items);
    }

    private sealed class Builder(ModelState state, Scenario scenario)
    {
        private readonly Dictionary<Guid, int> _index = [];
        private readonly List<Fault> _faults = [.. (scenario.Faults ?? []).Where(f => f is not null)];

        public List<SequenceParticipant> Participants { get; } = [];

        public List<SequenceItem> Run()
        {
            var body = new List<SequenceItem>();
            Steps(scenario.Steps, body);

            var assertions = (scenario.Assertions ?? []).Where(a => a is not null).ToList();
            if (Participants.Count == 0 && (_faults.Count > 0 || assertions.Count > 0))
                Add(scenario.Id.Value, scenario.Name ?? "", SequenceParticipantKind.Scenario, null, "scenario");

            var items = new List<SequenceItem>();
            foreach (var fault in _faults)
            {
                var over = FaultParticipant(fault.Target) ?? 0;
                items.Add(new SequenceNote(over, over, $"fault: {FaultText(fault)} at {TargetName(fault.Target)}"));
            }
            items.AddRange(body);
            foreach (var assertion in assertions)
                items.Add(new SequenceNote(0, Participants.Count - 1, AssertionText(assertion)));
            return items;
        }

        private void Steps(IReadOnlyList<Step>? steps, List<SequenceItem> into)
        {
            foreach (var step in steps ?? [])
            {
                switch (step)
                {
                    case CallStep call: Call(call, into); break;
                    case PublishStep publish: Publish(publish, into); break;
                    case WriteStep write:
                        var dataset = DiagramText.OneLine(write.Dataset);
                        into.Add(new SequenceMessage(Element(write.By), Element(write.Store), SequenceArrow.Call,
                            dataset.Length == 0 ? "write" : dataset));
                        break;
                    case ParallelStep parallel: Parallel(parallel, into); break;
                    case BranchStep branch: Branch(branch, into); break;
                }
            }
        }

        private void Call(CallStep call, List<SequenceItem> into)
        {
            var from = Element(call.From);
            state.Operations.TryGetValue(call.Operation, out var operation);
            if (operation is not null)
            {
                var to = Element(operation.OwnerId);
                into.Add(new SequenceMessage(from, to, SequenceArrow.Call, operation.Name));
                Steps(call.Then, into);
                into.Add(new SequenceMessage(to, from, SequenceArrow.Reply, "response"));
            }
            else
            {
                into.Add(new SequenceNote(from, from, $"calls {Unknown("operation", call.Operation)}"));
                Steps(call.Then, into);
            }

            var onError = new List<SequenceItem>();
            Steps(call.OnError, onError);
            if (onError.Count == 0)
                return;

            var faults = _faults
                .Where(f => f.Target == call.Operation.Value || (operation is not null && f.Target == operation.OwnerId.Value))
                .Select(FaultText)
                .ToList();
            var label = $"{(operation is null ? "the call" : operation.Name)} fails";
            if (faults.Count > 0)
                label += $" (fault: {string.Join(", ", faults)})";
            into.Add(new SequenceBlock(SequenceBlockKind.Alt, [new SequenceSection(label, onError)]));
        }

        private void Publish(PublishStep publish, List<SequenceItem> into)
        {
            var from = Element(publish.From);
            var message = state.Messages.TryGetValue(publish.Message, out var found) ? found.Name : Unknown("message", publish.Message);
            var handlers = publish.Handlers ?? new Dictionary<SubscriptionId, IReadOnlyList<Step>>();
            var delivered = new HashSet<SubscriptionId>();

            if (!state.Channels.ContainsKey(publish.Channel))
            {
                into.Add(new SequenceNote(from, from, $"publishes {message} to {Unknown("channel", publish.Channel)}"));
            }
            else
            {
                into.Add(new SequenceMessage(from, Channel(publish.Channel), SequenceArrow.Async, message));

                // The routes of the engine: the subscriptions on a channel by ID, then each
                // binding that passes the routing key by ID, to a channel not reached yet.
                var subscriptionsOn = state.Subscriptions.Values.OrderBy(s => s.Id.Value).ToLookup(s => s.ChannelId);
                var bindingsFrom = state.Bindings.Values.OrderBy(b => b.Id.Value).ToLookup(b => b.SourceChannelId);
                var reached = new HashSet<ChannelId> { publish.Channel };
                var deliveries = new List<SubscriptionState>();

                void Route(ChannelId channel)
                {
                    deliveries.AddRange(subscriptionsOn[channel]);
                    foreach (var binding in bindingsFrom[channel])
                    {
                        if (!ChannelRouting.Matches(binding.RoutingKey, publish.RoutingKey) || !reached.Add(binding.TargetChannelId))
                            continue;
                        into.Add(new SequenceMessage(Channel(channel), Channel(binding.TargetChannelId), SequenceArrow.Async, message));
                        Route(binding.TargetChannelId);
                    }
                }
                Route(publish.Channel);

                var sections = new List<SequenceSection>();
                foreach (var subscription in deliveries)
                {
                    delivered.Add(subscription.Id);
                    var items = new List<SequenceItem>
                    {
                        new SequenceMessage(Channel(subscription.ChannelId), Element(subscription.ConsumerId), SequenceArrow.Async, message),
                    };
                    Steps(handlers.GetValueOrDefault(subscription.Id), items);
                    sections.Add(new SequenceSection(ContractDecider.SubscriptionLabel(state, subscription), items));
                }

                if (sections.Count == 1)
                    into.AddRange(sections[0].Items);
                else if (sections.Count > 1)
                    into.Add(new SequenceBlock(SequenceBlockKind.Par, sections));
            }

            foreach (var subscription in handlers.Keys.Where(k => !delivered.Contains(k)).OrderBy(k => k.Value))
            {
                var name = state.Subscriptions.TryGetValue(subscription, out var s)
                    ? $"the handler of {ContractDecider.SubscriptionLabel(state, s)}"
                    : $"the handler of {Unknown("subscription", subscription)}";
                into.Add(new SequenceNote(from, from, $"{name} does not run: the message does not reach it"));
            }
        }

        private void Parallel(ParallelStep parallel, List<SequenceItem> into)
        {
            var sections = new List<SequenceSection>();
            var branches = parallel.Branches ?? [];
            for (var i = 0; i < branches.Count; i++)
            {
                var items = new List<SequenceItem>();
                Steps(branches[i], items);
                if (items.Count > 0)
                    sections.Add(new SequenceSection(string.Create(CultureInfo.InvariantCulture, $"branch {i + 1}"), items));
            }
            if (sections.Count > 0)
                into.Add(new SequenceBlock(SequenceBlockKind.Par, sections));
        }

        private void Branch(BranchStep branch, List<SequenceItem> into)
        {
            var then = new List<SequenceItem>();
            var otherwise = new List<SequenceItem>();
            Steps(branch.Then, then);
            Steps(branch.Else, otherwise);

            var step = DiagramText.OneLine(branch.WhenStep);
            var sections = new List<SequenceSection>();
            if (then.Count > 0)
            {
                sections.Add(new SequenceSection($"if {step} {Outcome(branch.Is)}", then));
                if (otherwise.Count > 0)
                    sections.Add(new SequenceSection("otherwise", otherwise));
            }
            else if (otherwise.Count > 0)
            {
                sections.Add(new SequenceSection($"if {step} {NotOutcome(branch.Is)}", otherwise));
            }
            if (sections.Count > 0)
                into.Add(new SequenceBlock(SequenceBlockKind.Alt, sections));
        }

        private int Element(ElementId id) =>
            state.Elements.TryGetValue(id, out var element)
                ? Add(id.Value, element.Name, element.Kind == ElementKind.Actor ? SequenceParticipantKind.Actor : SequenceParticipantKind.Element,
                    element.Kind, ViewDiagram.KindWord(element.Kind))
                : Add(id.Value, Unknown("element", id.Value), SequenceParticipantKind.Element, null, "unknown");

        private int Channel(ChannelId id) =>
            state.Channels.TryGetValue(id, out var channel)
                ? Add(id.Value, channel.Name, SequenceParticipantKind.Channel, null, channel.Kind.ToString().ToLowerInvariant())
                : Add(id.Value, Unknown("channel", id.Value), SequenceParticipantKind.Channel, null, "unknown");

        private int Add(Guid id, string name, SequenceParticipantKind kind, ElementKind? elementKind, string detail)
        {
            if (_index.TryGetValue(id, out var index))
                return index;
            index = Participants.Count;
            Participants.Add(new SequenceParticipant(id, DiagramText.OneLine(name), kind, elementKind, detail));
            _index[id] = index;
            return index;
        }

        /// <summary>The participant a fault note stands over: the target, or what serves it.</summary>
        private int? FaultParticipant(Guid target)
        {
            if (_index.TryGetValue(target, out var index))
                return index;
            if (state.Operations.TryGetValue(target, out var operation) && _index.TryGetValue(operation.OwnerId, out index))
                return index;
            if (state.Subscriptions.TryGetValue(target, out var subscription) && _index.TryGetValue(subscription.ConsumerId, out index))
                return index;
            return null;
        }

        private string TargetName(Guid? target)
        {
            if (target is not { } id) return "everything";
            if (state.Operations.TryGetValue(id, out var operation)) return $"operation {operation.Name}";
            if (state.Channels.TryGetValue(id, out var channel)) return $"channel {channel.Name}";
            if (state.Subscriptions.TryGetValue(id, out var subscription)) return $"subscription {ContractDecider.SubscriptionLabel(state, subscription)}";
            if (state.Bindings.TryGetValue(id, out var binding)) return $"binding {ContractDecider.BindingLabel(state, binding)}";
            if (state.Elements.TryGetValue(id, out var element)) return $"element {element.Name}";
            return Unknown("target", id);
        }

        private static string FaultText(Fault fault)
        {
            var kind = fault.Kind.ToString().ToLowerInvariant();
            return fault.FailFirst switch
            {
                null => kind,
                1 => $"{kind} on the first attempt",
                { } n => string.Create(CultureInfo.InvariantCulture, $"{kind} on the first {n} attempts"),
            };
        }

        private string AssertionText(Assertion assertion) => assertion switch
        {
            OutcomeAssertion outcome =>
                $"assert {DiagramText.OneLine(outcome.Id)}: {DiagramText.OneLine(outcome.Step)} {Outcome(outcome.Expected)}",
            EventCountAssertion count => string.Create(CultureInfo.InvariantCulture,
                $"assert {DiagramText.OneLine(count.Id)}: {count.Expected} {count.Kind} {(count.Expected == 1 ? "event" : "events")}") +
                (count.Target is { } target ? $" for {TargetName(target)}" : ""),
            _ => $"assert {DiagramText.OneLine(assertion.Id)}",
        };

        private static string Outcome(StepOutcome outcome) => outcome switch
        {
            StepOutcome.Succeeded => "succeeded",
            StepOutcome.Failed => "failed",
            _ => "was skipped",
        };

        private static string NotOutcome(StepOutcome outcome) => outcome switch
        {
            StepOutcome.Succeeded => "did not succeed",
            StepOutcome.Failed => "did not fail",
            _ => "was not skipped",
        };

        private static string Unknown(string what, Guid id) =>
            id == Guid.Empty ? $"no {what}" : $"unknown {what} {id.ToString("N", CultureInfo.InvariantCulture)[..8]}";
    }
}
