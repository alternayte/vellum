using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Vellum.Modules.Modelling.Model;
using Vellum.Modules.Trace.Engine;
using Vellum.Modules.Trace.Scenarios;
using Vellum.Shared;

namespace Vellum.Modules.Ai.Design;

public sealed record BlockedStep(string StepId, IReadOnlyList<string> Needs);

/// <summary>What <see cref="FlowAssembler.Build"/> made of a model's flat answer.</summary>
/// <param name="Scenario">Only the steps whose references all resolve, with the real IDs.</param>
/// <param name="Blocked">Steps that wait for a missing thing to be created. They are in no scenario.</param>
/// <param name="ValidMissing">The missing entries that are well formed, by alias.</param>
internal sealed record FlowBuild(
    Scenario Scenario,
    IReadOnlyList<BlockedStep> Blocked,
    IReadOnlyDictionary<string, FlowMissingOutput> ValidMissing,
    IReadOnlyList<RejectedReference> Rejected,
    IReadOnlyList<ScenarioIssue> Issues);

/// <summary>
/// Turns the flat answer of a model into a scenario document. The answer is untrusted: it is
/// valid against the JSON Schema and nothing more. Every reference is mapped through the alias
/// table of the request. A reference that names nothing is an invented reference: the step,
/// fault or assertion that holds it is dropped and listed, and the rest of the proposal stays.
/// A step that references a declared missing thing stays out of the scenario until that thing
/// is created. Nothing here throws for bad input, and every list is bounded.
/// </summary>
internal static partial class FlowAssembler
{
    public const int MaxFaults = 100;
    public const int MaxAssertions = 100;
    public const int MaxMissing = 50;
    public const int MaxQuestions = 20;
    public const int MaxEvidence = 50;
    public const int MaxParallelBranches = 64;
    /// <summary>The most issues and the most rejected references one proposal lists. A last entry says that more exist.</summary>
    public const int MaxFindings = 200;

    private static readonly string[] FaultTargets =
        [AliasKinds.Operation, AliasKinds.Channel, AliasKinds.Subscription, AliasKinds.Element];

    [GeneratedRegex("^N[1-9][0-9]{0,3}$")]
    private static partial Regex MissingAlias();

    /// <summary>
    /// Bounds the answer and cleans its text, so that it can be stored and resolved again
    /// later. A step that cannot be named, a second step with a taken ID and a payload that is
    /// too large are removed here and reported.
    /// </summary>
    public static (FlowOutput Output, List<ScenarioIssue> Issues) Normalize(FlowOutput raw)
    {
        var issues = new List<ScenarioIssue>();
        var given = raw.Steps ?? [];
        if (given.Count > TraceLimits.MaxSteps)
        {
            issues.Add(new("", "too-many-steps",
                $"The answer holds {given.Count} steps. A scenario has at most {TraceLimits.MaxSteps}; only the first {TraceLimits.MaxSteps} were read."));
        }

        var steps = new List<FlowStepOutput>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var step in given.Take(TraceLimits.MaxSteps))
        {
            if (step is null)
                continue;

            var id = step.Id?.Trim() ?? "";
            if (id.Length == 0 || TextRule.Line("id", id, TextRule.MaxIdentifier) is not null)
            {
                issues.Add(new(DesignText.Line(id, TextRule.MaxIdentifier), "invalid-step-id",
                    $"A step has no usable ID (1 to {TextRule.MaxIdentifier} characters, no control character). It was dropped."));
                continue;
            }
            if (!ids.Add(id))
            {
                issues.Add(new(id, "duplicate-step-id", $"Step ID '{id}' is used more than once. The later step was dropped."));
                continue;
            }

            string? Payload(string? text, string what)
            {
                // The empty string is the model's word for "no payload".
                if (string.IsNullOrWhiteSpace(text))
                    return null;
                if (Encoding.UTF8.GetByteCount(text) <= TraceLimits.MaxPayloadBytes)
                    return Canonical(text);
                issues.Add(new(id, "payload-too-large",
                    $"The {what} is larger than {TraceLimits.MaxPayloadBytes / 1024} KB. It was left out; the step has no {what}."));
                return null;
            }

            steps.Add(new FlowStepOutput(
                id, Token(step.ParentStepId), Token(step.Slot) ?? "", Token(step.SlotKey), step.Order, Token(step.Type) ?? "",
                Token(step.From), Token(step.Operation), Token(step.Channel), Token(step.Message), Token(step.By), Token(step.Store),
                string.IsNullOrEmpty(step.Dataset) ? null : DesignText.Line(step.Dataset),
                Token(step.WhenStep), Token(step.Is),
                Payload(step.RequestJson, "request"), Payload(step.ResponseJson, "response"),
                Payload(step.PayloadJson, "payload"), Payload(step.ChangeJson, "change"),
                // The empty string is the model's word for "no routing key".
                string.IsNullOrEmpty(step.RoutingKey) ? null : DesignText.Line(step.RoutingKey) is { Length: > 0 } key ? key : null));
        }

        var name = DesignText.Line(raw.Name);
        return (new FlowOutput(
            name.Length > 0 ? name : "Proposed flow",
            DesignText.Lines(raw.Summary),
            steps,
            [.. (raw.Faults ?? []).Where(f => f is not null).Take(MaxFaults)
                .Select(f => new FlowFaultOutput(
                    Token(f.Target) ?? "", Token(f.Kind) ?? "", f.FailFirst == DesignSchemas.NoFailFirst ? null : f.FailFirst))],
            [.. (raw.Assertions ?? []).Where(a => a is not null).Take(MaxAssertions)
                .Select(a => new FlowAssertionOutput(
                    Token(a.Id) ?? "", Token(a.Type) ?? "", Token(a.Step), Token(a.Expected), Token(a.EventKind), Token(a.Target),
                    a.ExpectedCount == DesignSchemas.NoExpectedCount ? null : a.ExpectedCount))],
            [.. (raw.Missing ?? []).Where(m => m is not null).Take(MaxMissing)
                .Select(m => new FlowMissingOutput(
                    Token(m.Alias) ?? "", Token(m.Kind) ?? "", ProposedNames.Normalize(DesignText.Line(m.Name, TextRule.MaxName + 1)),
                    Token(m.ElementKind), Token(m.ParentAlias), Token(m.OwnerAlias), Token(m.Transport), Token(m.ChannelKind),
                    Token(m.ChannelAlias), Token(m.ConsumerAlias), DesignText.Lines(m.Reason), Confidence(m.Confidence)))],
            DesignText.List(raw.OpenQuestions, MaxQuestions),
            DesignText.Lines(raw.ReasoningSummary, TextRule.MaxDescription),
            [.. (raw.Evidence ?? []).Where(e => e is not null).Take(MaxEvidence)
                .Select(e => new FlowEvidenceOutput(Token(e.Alias) ?? "", DesignText.Lines(e.Why)))]), issues);
    }

    /// <summary>
    /// The payload text in its canonical form (<see cref="CanonicalJson"/>): sorted keys, the
    /// last of two equal keys, one spelling per number. It is made once, when the answer is
    /// read, so the previewed document, the stored proposal and the saved scenario hold the
    /// same bytes. Text that is not a usable payload is returned as it is;
    /// <see cref="Build"/> reports it and leaves it out.
    /// </summary>
    private static string Canonical(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = ScenarioShape.MaxPayloadDepth + 1 });
            if (ScenarioShape.Payload(document.RootElement, "payload") is not null)
                return text;

            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { MaxDepth = ScenarioShape.MaxPayloadDepth + 2 }))
                CanonicalJson.Write(writer, document.RootElement);
            return Encoding.UTF8.GetString(buffer.ToArray());
        }
        // A string with half of a surrogate pair cannot be read as JSON text.
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            return text;
        }
    }

    /// <summary>A confidence is a number from 0 to 1. Anything else that a model writes is moved into that range.</summary>
    public static double Confidence(double value) => double.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0;

    /// <summary>An identifier or an alias: one bounded line. Null when the model gave none.</summary>
    private static string? Token(string? text)
    {
        var clean = DesignText.Line(text, TextRule.MaxIdentifier);
        return clean.Length == 0 ? null : clean;
    }

    private sealed class Node(FlowStepOutput step, int index)
    {
        public FlowStepOutput Step { get; } = step;
        public int Index { get; } = index;
        public bool Dropped { get; set; }
        public SortedSet<string> Needs { get; } = new(StringComparer.Ordinal);
        public Node? Parent { get; set; }
        public int Depth { get; set; } = 1;
        public List<Node> Children { get; } = [];
        public Dictionary<string, Guid> Ids { get; } = new(StringComparer.Ordinal);
        public StepOutcome Outcome { get; set; }
        public bool Included => !Dropped && Needs.Count == 0;
    }

    private enum RefState { Resolved, Blocked, Rejected, Absent }

    /// <param name="output">An answer that went through <see cref="Normalize"/>.</param>
    /// <param name="aliases">The alias table of the request that produced the answer.</param>
    /// <param name="created">The missing aliases that a person has created, with the real ID of each.</param>
    public static FlowBuild Build(
        FlowOutput output, IReadOnlyDictionary<string, AliasRef> aliases, IReadOnlyDictionary<string, Guid> created, ScenarioId scenarioId)
    {
        var rejected = new List<RejectedReference>();
        var issues = new List<ScenarioIssue>();

        // The missing entries come first: a step may only use an N alias that one of them declares.
        var missing = new Dictionary<string, FlowMissingOutput>(StringComparer.Ordinal);
        foreach (var entry in output.Missing)
        {
            if (!MissingAlias().IsMatch(entry.Alias))
                rejected.Add(new($"missing:{entry.Alias}", entry.Alias, "A missing thing takes an alias N1, N2, ... The entry was dropped."));
            else if (!AliasKinds.Contracts.Contains(entry.Kind))
                rejected.Add(new($"missing:{entry.Alias}.kind", entry.Alias, "The kind is not element, operation, channel, subscription or message. The entry was dropped."));
            else if (entry.Kind != AliasKinds.Subscription && (string.IsNullOrWhiteSpace(entry.Name) || entry.Name.Length > TextRule.MaxName))
                rejected.Add(new($"missing:{entry.Alias}.name", entry.Alias, $"The name must hold 1 to {TextRule.MaxName} characters. The entry was dropped."));
            else if (!missing.TryAdd(entry.Alias, entry))
                rejected.Add(new($"missing:{entry.Alias}", entry.Alias, "The alias is declared more than once. The later entry was dropped."));
        }

        PruneMissing(missing, aliases, rejected);

        (RefState State, Guid Id, string? Reason) Resolve(string? alias, params string[] kinds)
        {
            if (alias is null)
                return (RefState.Absent, default, null);

            string Wanted() => string.Join(" or ", kinds.Select(AliasKinds.WithArticle));
            if (aliases.TryGetValue(alias, out var named))
            {
                return kinds.Contains(named.Kind)
                    ? (RefState.Resolved, named.Id, null)
                    : (RefState.Rejected, default, $"{alias} names {AliasKinds.WithArticle(named.Kind)}. {Wanted()} is needed here.");
            }
            if (missing.TryGetValue(alias, out var declared))
            {
                if (!kinds.Contains(declared.Kind))
                    return (RefState.Rejected, default, $"{alias} is declared as {AliasKinds.WithArticle(declared.Kind)}. {Wanted()} is needed here.");
                return created.TryGetValue(alias, out var id) ? (RefState.Resolved, id, null) : (RefState.Blocked, default, null);
            }

            return (RefState.Rejected, default, Guid.TryParse(alias, out _)
                ? "A raw ID is not a reference. A thing is referenced by its alias from the table."
                : "The alias is not in the alias table and is not declared in 'missing'.");
        }

        var nodes = output.Steps.Select((step, index) => new Node(step, index)).ToList();
        var byId = nodes.ToDictionary(n => n.Step.Id, StringComparer.Ordinal);

        void Drop(Node node, string code, string message)
        {
            if (node.Dropped)
                return;
            node.Dropped = true;
            issues.Add(new(node.Step.Id, code, message));
        }

        // A reference of a step. Returns false when the step cannot stay.
        bool Reference(Node node, string field, string? alias, bool required, params string[] kinds)
        {
            var (state, id, reason) = Resolve(alias, kinds);
            switch (state)
            {
                case RefState.Resolved:
                    node.Ids[field] = id;
                    return true;
                case RefState.Blocked:
                    node.Needs.Add(alias!);
                    return true;
                case RefState.Rejected:
                    node.Dropped = true;
                    rejected.Add(new($"step:{node.Step.Id}.{field}", alias!, $"{reason} The step was dropped."));
                    return false;
                default:
                    if (required)
                        Drop(node, "missing-reference", $"A {node.Step.Type} step needs '{field}'. The step was dropped.");
                    return !required;
            }
        }

        foreach (var node in nodes)
        {
            var step = node.Step;
            switch (step.Type)
            {
                case "call":
                    _ = Reference(node, "from", step.From, true, AliasKinds.Element)
                        && Reference(node, "operation", step.Operation, true, AliasKinds.Operation);
                    break;
                case "publish":
                    _ = Reference(node, "from", step.From, true, AliasKinds.Element)
                        && Reference(node, "channel", step.Channel, true, AliasKinds.Channel)
                        && Reference(node, "message", step.Message, true, AliasKinds.Message);
                    break;
                case "write":
                    _ = Reference(node, "by", step.By, true, AliasKinds.Element)
                        && Reference(node, "store", step.Store, true, AliasKinds.Element);
                    break;
                case "parallel":
                    break;
                case "branch":
                    if (step.WhenStep is null)
                        Drop(node, "missing-reference", "A branch step needs 'whenStep'. The step was dropped.");
                    else if (!ModelEnum.TryParseDefined(step.Is, out StepOutcome outcome))
                        Drop(node, "invalid-outcome", "A branch compares against succeeded, failed or skipped. The step was dropped.");
                    else
                        node.Outcome = outcome;
                    break;
                default:
                    Drop(node, "unknown-step-type", "The step type is not call, publish, write, parallel or branch. The step was dropped.");
                    break;
            }
        }

        // Parents and slots.
        foreach (var node in nodes.Where(n => !n.Dropped))
        {
            var step = node.Step;
            if (step.Slot == "root")
            {
                if (step.ParentStepId is not null)
                    Drop(node, "bad-slot", "A step with slot 'root' has no parent. The step was dropped.");
                continue;
            }

            if (step.ParentStepId is null || !byId.TryGetValue(step.ParentStepId, out var parent) || parent == node)
            {
                Drop(node, "unknown-parent", $"The parent step '{step.ParentStepId}' is not in the answer. The step was dropped.");
                continue;
            }

            node.Parent = parent;
            var fits = step.Slot switch
            {
                "then" or "onError" => parent.Step.Type == "call",
                "handler" => parent.Step.Type == "publish",
                "branchThen" or "branchElse" => parent.Step.Type == "branch",
                "parallelBranch" => parent.Step.Type == "parallel"
                    && int.TryParse(step.SlotKey, NumberStyles.None, CultureInfo.InvariantCulture, out var branch) && branch < MaxParallelBranches,
                _ => false,
            };
            if (!fits)
            {
                Drop(node, "bad-slot", $"Slot '{step.Slot}' does not fit a parent of type '{parent.Step.Type}'. The step was dropped.");
                continue;
            }

            if (step.Slot == "handler" && !Reference(node, "subscription", step.SlotKey, true, AliasKinds.Subscription))
                continue;
        }

        // A step whose chain of parents does not end at a root step is on a cycle, or hangs
        // from one. The walk is bounded by the number of steps.
        foreach (var node in nodes.Where(n => !n.Dropped))
        {
            var depth = 1;
            var at = node.Parent;
            while (at is not null && depth <= nodes.Count)
            {
                depth++;
                at = at.Parent;
            }

            if (at is not null)
                Drop(node, "cycle", "The step is its own ancestor. The step was dropped.");
            else if (depth > TraceLimits.MaxDepth)
                Drop(node, "too-deep", $"Steps nest at most {TraceLimits.MaxDepth} levels. The step was dropped.");
            else
                node.Depth = depth;
        }

        // What a parent lacks, its children lack: a step that hangs from a dropped step is
        // dropped, and one that hangs from a blocked step, or branches on one, waits with it.
        foreach (var node in nodes.OrderBy(n => n.Depth).ThenBy(n => n.Index))
        {
            if (node.Dropped)
                continue;
            if (node.Parent is { Dropped: true })
                Drop(node, "parent-dropped", $"The parent step '{node.Parent.Step.Id}' was dropped, so this step was dropped.");
            else if (node.Parent is { } parent)
                node.Needs.UnionWith(parent.Needs);
        }
        for (var changed = true; changed;)
        {
            changed = false;
            foreach (var node in nodes.Where(n => !n.Dropped).OrderBy(n => n.Depth).ThenBy(n => n.Index))
            {
                var before = node.Needs.Count;
                if (node.Parent is { } parent)
                    node.Needs.UnionWith(parent.Needs);
                if (node.Step.Type == "branch" && byId.TryGetValue(node.Step.WhenStep!, out var condition) && !condition.Dropped)
                    node.Needs.UnionWith(condition.Needs);
                changed |= node.Needs.Count != before;
            }
        }

        foreach (var node in nodes.Where(n => n.Included && n.Parent is not null))
            node.Parent!.Children.Add(node);

        JsonElement? Payload(Node node, string what, string? text)
        {
            if (text is null)
                return null;
            try
            {
                using var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = ScenarioShape.MaxPayloadDepth + 1 });
                var value = document.RootElement.Clone();
                if (ScenarioShape.Payload(value, what) is { } error)
                {
                    issues.Add(new(node.Step.Id, "invalid-payload", $"The {what} was left out: {error.Message}."));
                    return null;
                }

                return value;
            }
            catch (Exception ex) when (ex is JsonException or ArgumentException)
            {
                issues.Add(new(node.Step.Id, "invalid-payload-json", $"The {what} is not JSON text. It was left out; the step has no {what}."));
                return null;
            }
        }

        List<Step> Steps(IEnumerable<Node> list) => [.. list.OrderBy(n => n.Step.Order).ThenBy(n => n.Index).Select(Make)];

        Step Make(Node node)
        {
            var step = node.Step;
            List<Step> Slot(string slot) => Steps(node.Children.Where(c => c.Step.Slot == slot));
            switch (step.Type)
            {
                case "call":
                    return new CallStep(step.Id, node.Ids["from"], node.Ids["operation"],
                        Payload(node, "request", step.RequestJson), Payload(node, "response", step.ResponseJson), Slot("then"), Slot("onError"));
                case "publish":
                    var handlers = new Dictionary<SubscriptionId, IReadOnlyList<Step>>();
                    foreach (var group in node.Children.Where(c => c.Step.Slot == "handler").GroupBy(c => c.Ids["subscription"]).OrderBy(g => g.Min(c => c.Index)))
                        handlers[group.Key] = Steps(group);
                    return new PublishStep(step.Id, node.Ids["from"], node.Ids["channel"], node.Ids["message"],
                        Payload(node, "payload", step.PayloadJson), new Dictionary<string, string>(), handlers, step.RoutingKey);
                case "write":
                    return new WriteStep(step.Id, node.Ids["by"], node.Ids["store"], step.Dataset ?? "", Payload(node, "change", step.ChangeJson));
                case "parallel":
                    return new ParallelStep(step.Id, [.. node.Children
                        .GroupBy(c => int.Parse(c.Step.SlotKey!, NumberStyles.None, CultureInfo.InvariantCulture))
                        .OrderBy(g => g.Key)
                        .Select(g => (IReadOnlyList<Step>)Steps(g))]);
                default:
                    return new BranchStep(step.Id, step.WhenStep!, node.Outcome, Slot("branchThen"), Slot("branchElse"));
            }
        }

        var roots = Steps(nodes.Where(n => n.Included && n.Parent is null));

        var faults = new List<Fault>();
        for (var i = 0; i < output.Faults.Count; i++)
        {
            var fault = output.Faults[i];
            var (state, id, reason) = Resolve(fault.Target, FaultTargets);
            if (state == RefState.Rejected)
                rejected.Add(new($"fault:{i}.target", fault.Target, $"{reason} The fault was dropped."));
            else if (state == RefState.Resolved && ModelEnum.TryParseDefined(fault.Kind, out FaultKind kind))
                faults.Add(new Fault(id, kind, fault.FailFirst));
            else if (state == RefState.Resolved)
                issues.Add(new("", "invalid-fault", $"Fault {i} has a kind that is not error, timeout or outage. It was dropped."));
            else if (state == RefState.Absent)
                issues.Add(new("", "invalid-fault", $"Fault {i} names no target. It was dropped."));
        }

        var assertions = new List<Assertion>();
        foreach (var assertion in output.Assertions)
        {
            void Bad(string why) => issues.Add(new("", "invalid-assertion", $"Assertion '{assertion.Id}' {why}. It was dropped."));
            if (assertion.Id.Length == 0)
            {
                Bad("has no ID");
            }
            else if (assertion.Type == "outcome")
            {
                if (assertion.Step is null || !ModelEnum.TryParseDefined(assertion.Expected, out StepOutcome expected))
                    Bad("needs 'step' and an 'expected' of succeeded, failed or skipped");
                else if (byId.TryGetValue(assertion.Step, out var about) && about.Dropped)
                    Bad($"names step '{assertion.Step}', which was dropped");
                // An assertion about a step that waits for a missing thing waits with it.
                else if (!byId.TryGetValue(assertion.Step, out about) || about.Included)
                    assertions.Add(new OutcomeAssertion(assertion.Id, assertion.Step, expected));
            }
            else if (assertion.Type == "event-count")
            {
                var (state, id, reason) = Resolve(assertion.Target, FaultTargets);
                if (!ModelEnum.TryParseDefined(assertion.EventKind, out TraceEventKind kind) || assertion.ExpectedCount is not { } count)
                    Bad("needs a known 'eventKind' and an 'expectedCount'");
                else if (state == RefState.Rejected)
                    rejected.Add(new($"assertion:{assertion.Id}.target", assertion.Target!, $"{reason} The assertion was dropped."));
                else if (state != RefState.Blocked)
                    assertions.Add(new EventCountAssertion(assertion.Id, kind, state == RefState.Resolved ? id : null, count));
            }
            else
            {
                Bad("has a type that is not outcome or event-count");
            }
        }

        var scenario = new Scenario(scenarioId, output.Name, roots, faults, assertions);

        // The same limits a save applies. A document outside them cannot be accepted.
        // The issue goes first: it decides whether the proposal can be accepted, so the bound on the list must not cut it.
        if (ScenarioShape.Check(scenario) is { } shape)
            issues.Insert(0, new("", "not-storable", $"{shape.Field}: {shape.Message}."));
        else if (JsonSerializer.SerializeToUtf8Bytes(scenario, DesignJson.Options).Length is var bytes and > ScenarioShape.MaxDocumentBytes)
            issues.Insert(0, new("", "not-storable", $"A scenario document holds at most {ScenarioShape.MaxDocumentBytes} bytes. This one has {bytes}."));

        var blocked = nodes.Where(n => !n.Dropped && n.Needs.Count > 0)
            .Select(n => new BlockedStep(n.Step.Id, [.. n.Needs])).ToList();
        return new FlowBuild(scenario, blocked, missing, Bounded(rejected, MoreRejected), Bounded(issues, MoreIssues));
    }

    /// <summary>The references of a missing entry, with the kind each must name. A null alias of a required reference is listed too.</summary>
    public static IEnumerable<(string Field, string? Alias, string Kind, bool Required)> MissingReferences(FlowMissingOutput entry) => entry.Kind switch
    {
        AliasKinds.Element => [("parentAlias", entry.ParentAlias, AliasKinds.Element, false)],
        AliasKinds.Operation or AliasKinds.Message => [("ownerAlias", entry.OwnerAlias, AliasKinds.Element, true)],
        AliasKinds.Subscription =>
        [
            ("channelAlias", entry.ChannelAlias, AliasKinds.Channel, true),
            ("consumerAlias", entry.ConsumerAlias, AliasKinds.Element, true),
        ],
        _ => [],
    };

    /// <summary>
    /// Removes every missing entry that can never be created: one that references an alias
    /// that names nothing or a thing of the wrong kind, one that lacks a required reference,
    /// and one that waits for itself through other entries.
    /// </summary>
    private static void PruneMissing(
        Dictionary<string, FlowMissingOutput> missing, IReadOnlyDictionary<string, AliasRef> aliases, List<RejectedReference> rejected)
    {
        for (var changed = true; changed;)
        {
            changed = false;
            foreach (var entry in missing.Values.ToList())
            {
                foreach (var (field, alias, kind, required) in MissingReferences(entry))
                {
                    var fits = alias is null
                        ? !required
                        : aliases.TryGetValue(alias, out var named) ? named.Kind == kind
                        : missing.TryGetValue(alias, out var declared) && declared.Kind == kind;
                    if (fits)
                        continue;

                    rejected.Add(new($"missing:{entry.Alias}.{field}", alias ?? "",
                        alias is null
                            ? $"{AliasKinds.WithArticle(entry.Kind)} needs '{field}'. The entry was dropped."
                            : $"The alias does not name {AliasKinds.WithArticle(kind)} of the table or of 'missing'. The entry was dropped."));
                    missing.Remove(entry.Alias);
                    changed = true;
                    break;
                }
            }
        }

        // What is left after every entry whose references are created first has been taken
        // waits for itself.
        var ordered = new HashSet<string>(StringComparer.Ordinal);
        for (var changed = true; changed;)
        {
            changed = false;
            foreach (var entry in missing.Values.Where(e => !ordered.Contains(e.Alias)))
            {
                if (MissingReferences(entry).All(r => r.Alias is null || aliases.ContainsKey(r.Alias) || ordered.Contains(r.Alias)))
                    changed |= ordered.Add(entry.Alias);
            }
        }
        foreach (var alias in missing.Keys.Where(a => !ordered.Contains(a)).ToList())
        {
            rejected.Add(new($"missing:{alias}", alias, "The entry needs itself to exist first. The entry was dropped."));
            missing.Remove(alias);
        }
    }

    private static readonly RejectedReference MoreRejected = new("", "", "More references were rejected; they are not listed.");
    private static readonly ScenarioIssue MoreIssues = new("", "more-issues", "More issues exist; they are not listed.");

    private static List<T> Bounded<T>(List<T> all, T more) =>
        all.Count <= MaxFindings ? all : [.. all.Take(MaxFindings - 1), more];

    /// <summary>
    /// A scenario as the flat step list that a model reads and writes. <paramref name="name"/>
    /// turns an ID into the text of a reference: an alias for a prompt, the ID itself for a diff.
    /// </summary>
    /// <param name="canonicalPayloads">Writes each payload in canonical form, so that two spellings of one value compare equal.</param>
    public static List<FlowStepOutput> Flatten(Scenario scenario, Func<Guid, string> name, bool canonicalPayloads = false)
    {
        var flat = new List<FlowStepOutput>();
        string? Text(JsonElement? payload)
        {
            if (payload is not { } value)
                return null;
            if (!canonicalPayloads)
                return value.GetRawText();

            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer))
                CanonicalJson.Write(writer, value);
            return Encoding.UTF8.GetString(buffer.ToArray());
        }

        void Visit(IReadOnlyList<Step>? steps, string? parent, string slot, string? slotKey, int depth)
        {
            if (steps is null || depth > TraceLimits.MaxDepth)
                return;

            var order = 0;
            foreach (var step in steps)
            {
                if (step is null || flat.Count >= TraceLimits.MaxSteps)
                    continue;

                order++;
                FlowStepOutput Row(
                    string type, string? from = null, string? operation = null, string? channel = null, string? message = null,
                    string? by = null, string? store = null, string? dataset = null, string? whenStep = null, string? @is = null,
                    string? request = null, string? response = null, string? payload = null, string? change = null,
                    string? routingKey = null) =>
                    new(step.Id, parent, slot, slotKey, order, type, from, operation, channel, message, by, store, dataset, whenStep, @is,
                        request, response, payload, change, routingKey);

                switch (step)
                {
                    case CallStep call:
                        flat.Add(Row("call", from: name(call.From), operation: name(call.Operation), request: Text(call.Request), response: Text(call.Response)));
                        Visit(call.Then, step.Id, "then", null, depth + 1);
                        Visit(call.OnError, step.Id, "onError", null, depth + 1);
                        break;
                    case PublishStep publish:
                        flat.Add(Row("publish", from: name(publish.From), channel: name(publish.Channel), message: name(publish.Message), payload: Text(publish.Payload),
                            routingKey: publish.RoutingKey));
                        foreach (var (subscription, handler) in publish.Handlers ?? new Dictionary<SubscriptionId, IReadOnlyList<Step>>())
                            Visit(handler, step.Id, "handler", name(subscription), depth + 1);
                        break;
                    case WriteStep write:
                        flat.Add(Row("write", by: name(write.By), store: name(write.Store), dataset: write.Dataset, change: Text(write.Change)));
                        break;
                    case ParallelStep parallel:
                        flat.Add(Row("parallel"));
                        var index = 0;
                        foreach (var branch in parallel.Branches ?? [])
                            Visit(branch, step.Id, "parallelBranch", (index++).ToString(CultureInfo.InvariantCulture), depth + 1);
                        break;
                    case BranchStep branch:
                        flat.Add(Row("branch", whenStep: branch.WhenStep, @is: branch.Is.ToString().ToLowerInvariant()));
                        Visit(branch.Then, step.Id, "branchThen", null, depth + 1);
                        Visit(branch.Else, step.Id, "branchElse", null, depth + 1);
                        break;
                }
            }
        }

        Visit(scenario.Steps, null, "root", null, 1);
        return flat;
    }

    /// <summary>The faults and the assertions of a scenario in the flat form, with <paramref name="name"/> for each ID.</summary>
    public static (List<FlowFaultOutput> Faults, List<FlowAssertionOutput> Assertions) FlattenChecks(Scenario scenario, Func<Guid, string> name) =>
    (
        [.. (scenario.Faults ?? []).Where(f => f is not null).Take(MaxFaults)
            .Select(f => new FlowFaultOutput(name(f.Target), f.Kind.ToString().ToLowerInvariant(), f.FailFirst))],
        [.. (scenario.Assertions ?? []).Where(a => a is not null).Take(MaxAssertions).Select(a => a switch
        {
            OutcomeAssertion outcome => new FlowAssertionOutput(a.Id, "outcome", outcome.Step, outcome.Expected.ToString().ToLowerInvariant(), null, null, null),
            EventCountAssertion count => new FlowAssertionOutput(
                a.Id, "event-count", null, null, count.Kind.ToString(), count.Target is { } target ? name(target) : null, count.Expected),
            _ => new FlowAssertionOutput(a.Id, "", null, null, null, null, null),
        })]
    );

    /// <summary>
    /// What a revision changes, by step ID. A step is changed when its own values, its parent,
    /// its slot or its place in its list differ. A change inside a nested step is reported for
    /// that step, not for its parent.
    /// </summary>
    public static FlowDiff Diff(Scenario before, Scenario after)
    {
        static Dictionary<string, string> Signatures(Scenario scenario)
        {
            var rows = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var row in Flatten(scenario, id => id.ToString(), canonicalPayloads: true))
                rows.TryAdd(row.Id ?? "", JsonSerializer.Serialize(row, DesignJson.Options));
            return rows;
        }

        var old = Signatures(before);
        var proposed = Signatures(after);
        return new FlowDiff(
            [.. proposed.Keys.Where(id => !old.ContainsKey(id))],
            [.. old.Keys.Where(id => !proposed.ContainsKey(id))],
            [.. proposed.Where(p => old.TryGetValue(p.Key, out var was) && was != p.Value).Select(p => p.Key)]);
    }
}

/// <summary>Step IDs. <paramref name="RemovedSteps"/> also holds a step that the revision keeps but that waits for a missing thing.</summary>
public sealed record FlowDiff(
    IReadOnlyList<string> AddedSteps, IReadOnlyList<string> RemovedSteps, IReadOnlyList<string> ChangedSteps);
