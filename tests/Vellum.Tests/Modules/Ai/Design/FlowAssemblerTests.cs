using System.Diagnostics;
using System.Text.Json;
using Vellum.Modules.Ai.Design;
using Vellum.Modules.Trace.Engine;

namespace Vellum.Tests.Modules.Ai.Design;

/// <summary>The mapping from a model's flat answer to a scenario, without a database or a provider.</summary>
public class FlowAssemblerTests
{
    private static readonly Guid Caller = Guid.NewGuid(), Service = Guid.NewGuid(), Db = Guid.NewGuid(), Op = Guid.NewGuid();

    private static readonly Dictionary<string, AliasRef> Aliases = new()
    {
        ["E1"] = new("element", Caller, "Caller"),
        ["E2"] = new("element", Service, "Service"),
        ["E3"] = new("element", Db, "Db"),
        ["O1"] = new("operation", Op, "Do"),
    };

    private static FlowStepOutput Step(
        string id, string type, string slot = "root", string? parent = null, string? slotKey = null, int order = 0,
        string? from = null, string? operation = null, string? by = null, string? store = null, string? whenStep = null, string? @is = null,
        string? change = null) =>
        new(id, parent, slot, slotKey, order, type, from, operation, null, null, by, store, by is null ? null : "d", whenStep, @is, null, null, null, change);

    private static FlowOutput Flow(IEnumerable<FlowStepOutput> steps, IEnumerable<FlowMissingOutput>? missing = null) =>
        new("Flow", "", [.. steps], [], [], [.. missing ?? []], [], "", []);

    private static (FlowBuild Build, List<ScenarioIssue> Ingest) Assemble(FlowOutput raw, Dictionary<string, Guid>? created = null)
    {
        var (output, issues) = FlowAssembler.Normalize(raw);
        return (FlowAssembler.Build(output, Aliases, created ?? [], Guid.NewGuid()), issues);
    }

    [Fact]
    public void Ten_thousand_steps_are_read_up_to_the_limit_in_bounded_time_and_reported_once()
    {
        var steps = Enumerable.Range(0, 10_000).Select(i => Step($"s{i}", "write", by: "E2", store: "E3")).ToList();
        var clock = Stopwatch.StartNew();

        var (build, ingest) = Assemble(Flow(steps));

        Assert.Equal(TraceLimits.MaxSteps, build.Scenario.Steps.Count);
        var issue = Assert.Single(ingest);
        Assert.Equal("too-many-steps", issue.Code);
        Assert.Contains("10000", issue.Message);
        // 500 steps and the list that holds them are more than a scenario stores, so this one cannot be accepted.
        Assert.Equal(["not-storable"], build.Issues.Select(i => i.Code));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"Took {clock.Elapsed}.");
    }

    [Fact]
    public void A_chain_of_five_hundred_nested_steps_and_a_ring_of_five_hundred_do_not_recurse_without_end()
    {
        // Each step is the child of the one before: far deeper than a scenario nests.
        var chain = Enumerable.Range(0, 500).Select(i => Step(
            $"c{i}", "call", i == 0 ? "root" : "then", i == 0 ? null : $"c{i - 1}", from: "E1", operation: "O1")).ToList();
        var (deep, _) = Assemble(Flow(chain));

        Assert.Equal(TraceLimits.MaxDepth, Depth(deep.Scenario.Steps));
        Assert.Contains(deep.Issues, i => i.Code == "too-deep");
        Assert.True(deep.Issues.Count <= FlowAssembler.MaxFindings);
        Assert.Equal("more-issues", deep.Issues[^1].Code);

        // Every step is the child of the next, and the last of the first.
        var ring = Enumerable.Range(0, 500).Select(i => Step($"r{i}", "parallel", "parallelBranch", $"r{(i + 1) % 500}", "0")).ToList();
        var (round, _) = Assemble(Flow(ring));

        Assert.Empty(round.Scenario.Steps);
        Assert.All(round.Issues.SkipLast(1), i => Assert.Equal("cycle", i.Code));

        static int Depth(IReadOnlyList<Step> steps) => steps.Count == 0 ? 0 : 1 + steps.Max(s => s is CallStep call ? Depth(call.Then) : 0);
    }

    [Fact]
    public void An_alias_of_the_wrong_kind_an_unknown_alias_and_a_raw_id_are_rejected_and_the_rest_stays()
    {
        var (build, _) = Assemble(Flow(
        [
            Step("good", "call", from: "E1", operation: "O1"),
            Step("wrong-kind", "call", order: 1, from: "O1", operation: "O1"),
            Step("unknown", "call", order: 2, from: "E1", operation: "O7"),
            Step("raw", "write", order: 3, by: "E2", store: Db.ToString()),
            Step("lower-case", "write", order: 4, by: "e2", store: "E3"),
            Step("child-of-rejected", "write", "then", "unknown", by: "E2", store: "E3"),
            Step("no-reference", "call", order: 5, from: "E1"),
        ]));

        Assert.Equal(["good"], build.Scenario.Steps.Select(s => s.Id));
        Assert.Equal(
            [("step:wrong-kind.from", "O1"), ("step:unknown.operation", "O7"), ("step:raw.store", Db.ToString()), ("step:lower-case.by", "e2")],
            build.Rejected.Select(r => (r.Where, r.Alias)));
        Assert.Contains("names an operation", build.Rejected[0].Reason);
        Assert.Contains("raw ID", build.Rejected[2].Reason);
        Assert.Equal(
            [("no-reference", "missing-reference"), ("child-of-rejected", "parent-dropped")],
            build.Issues.Select(i => (i.StepId, i.Code)));
        Assert.Empty(build.Blocked);
    }

    [Fact]
    public void A_step_waits_for_every_missing_thing_it_or_its_parent_or_its_branch_condition_names()
    {
        var missing = new[]
        {
            new FlowMissingOutput("N1", "element", "Payments", "app", "E2", null, null, null, null, null, "Needed", 0.5),
            new FlowMissingOutput("N2", "operation", "Charge", null, null, "N1", "http", null, null, null, "Needed", 2.5),
            // Never creatable: an owner that is not declared, a wrong alias form, and two entries that need each other.
            new FlowMissingOutput("N3", "operation", "Ghost", null, null, "N9", "http", null, null, null, "Needed", 0.5),
            new FlowMissingOutput("X1", "channel", "events", null, null, null, null, "topic", null, null, "Needed", 0.5),
            new FlowMissingOutput("N4", "element", "A", "component", "N5", null, null, null, null, null, "Needed", 0.5),
            new FlowMissingOutput("N5", "element", "B", "component", "N4", null, null, null, null, null, "Needed", 0.5),
        };
        var steps = new[]
        {
            Step("charge", "call", from: "E1", operation: "N2"),
            Step("record", "write", "then", "charge", by: "N1", store: "E3"),
            Step("after", "branch", order: 1, whenStep: "charge", @is: "failed"),
            Step("compensate", "write", "branchThen", "after", by: "E2", store: "E3"),
            Step("ghost", "call", order: 2, from: "E1", operation: "N3"),
            Step("free", "write", order: 3, by: "E2", store: "E3"),
        };

        var (waiting, _) = Assemble(Flow(steps, missing));

        Assert.Equal(["free"], waiting.Scenario.Steps.Select(s => s.Id));
        Assert.Equal(
            [("charge", "N2"), ("record", "N1,N2"), ("after", "N2"), ("compensate", "N2")],
            waiting.Blocked.Select(b => (b.StepId, string.Join(",", b.Needs))));
        Assert.Equal(["N1", "N2"], waiting.ValidMissing.Keys.Order());
        Assert.Equal(1, waiting.ValidMissing["N2"].Confidence);
        Assert.Equal(
            ["missing:X1", "missing:N3.ownerAlias", "missing:N4", "missing:N5", "step:ghost.operation"],
            waiting.Rejected.Select(r => r.Where));

        // With both created, the same answer gives the whole flow.
        var element = Guid.NewGuid();
        var operation = Guid.NewGuid();
        var (resolved, _) = Assemble(Flow(steps, missing), new() { ["N1"] = element, ["N2"] = operation });

        Assert.Empty(resolved.Blocked);
        Assert.Equal(["charge", "after", "free"], resolved.Scenario.Steps.Select(s => s.Id));
        var charge = Assert.IsType<CallStep>(resolved.Scenario.Steps[0]);
        Assert.Equal(operation, charge.Operation.Value);
        Assert.Equal(element, Assert.IsType<WriteStep>(Assert.Single(charge.Then)).By.Value);
        Assert.Equal(StepOutcome.Failed, Assert.IsType<BranchStep>(resolved.Scenario.Steps[1]).Is);
    }

    [Fact]
    public void Text_and_payloads_that_a_scenario_cannot_hold_are_removed_and_reported()
    {
        var big = "\"" + new string('x', TraceLimits.MaxPayloadBytes) + "\"";
        var deep = new string('[', 40) + new string(']', 40);
        var (build, ingest) = Assemble(new FlowOutput(
            "A name\u0000 with a control character", "Summary",
            [
                Step("", "write", by: "E2", store: "E3"),
                Step(new string('i', 101), "write", by: "E2", store: "E3"),
                Step("too-big", "write", order: 1, by: "E2", store: "E3", change: big),
                Step("too-deep", "write", order: 2, by: "E2", store: "E3", change: deep),
                Step("nul", "write", order: 3, by: "E2", store: "E3", change: "{\"a\":\"b\\u0000c\"}"),
                Step("fine", "write", order: 4, by: "E2", store: "E3", change: "{\"a\": 1}"),
                Step("odd-type", "teleport", order: 5),
                Step("odd-outcome", "branch", order: 6, whenStep: "fine", @is: "exploded"),
            ],
            [new FlowFaultOutput("O1", "meltdown", null), new FlowFaultOutput("O1", "timeout", 2)],
            [new FlowAssertionOutput("a1", "outcome", "fine", "succeeded", null, null, null), new FlowAssertionOutput("", "outcome", "fine", "succeeded", null, null, null)],
            [], ["Why?", "", "\u0007"], "Because", [new FlowEvidenceOutput("O1", "It is the call.")]));

        Assert.Equal("A name with a control character", build.Scenario.Name);
        Assert.Equal(["too-big", "too-deep", "nul", "fine"], build.Scenario.Steps.Select(s => s.Id));
        Assert.All(build.Scenario.Steps.Take(3), s => Assert.Null(((WriteStep)s).Change));
        Assert.Equal(JsonValueKind.Object, ((WriteStep)build.Scenario.Steps[3]).Change!.Value.ValueKind);
        Assert.Equal(["invalid-step-id", "invalid-step-id", "payload-too-large"], ingest.Select(i => i.Code));
        Assert.Equal(
            ["unknown-step-type", "invalid-outcome", "invalid-payload-json", "invalid-payload", "invalid-fault", "invalid-assertion"],
            build.Issues.Select(i => i.Code));
        Assert.Equal(new Fault(Op, FaultKind.Timeout, 2), Assert.Single(build.Scenario.Faults));
        Assert.Equal("a1", Assert.Single(build.Scenario.Assertions).Id);
    }

    [Fact]
    public void A_diff_names_added_removed_and_changed_steps_and_ignores_how_a_payload_is_spelled()
    {
        var (before, _) = Assemble(Flow(
        [
            Step("call", "call", from: "E1", operation: "O1"),
            Step("keep", "write", "then", "call", order: 0, by: "E2", store: "E3", change: "{\"a\": 1.0, \"b\": 2}"),
            Step("drop", "write", "then", "call", order: 1, by: "E2", store: "E3"),
            Step("move", "write", order: 1, by: "E2", store: "E3"),
        ]));
        var (after, _) = Assemble(Flow(
        [
            Step("call", "call", from: "E1", operation: "O1"),
            Step("keep", "write", "then", "call", order: 0, by: "E2", store: "E3", change: "{\"b\":2,\"a\":1}"),
            Step("move", "write", "onError", "call", by: "E2", store: "E3"),
            Step("new", "write", order: 1, by: "E1", store: "E3"),
        ]));

        var diff = FlowAssembler.Diff(before.Scenario, after.Scenario);

        Assert.Equal(["new"], diff.AddedSteps);
        Assert.Equal(["drop"], diff.RemovedSteps);
        Assert.Equal(["move"], diff.ChangedSteps);
        Assert.Equal(FlowProposals.ScenarioHash(before.Scenario), FlowProposals.ScenarioHash(before.Scenario with { }));
        Assert.NotEqual(FlowProposals.ScenarioHash(before.Scenario), FlowProposals.ScenarioHash(after.Scenario with { Id = before.Scenario.Id }));
    }

    [Fact]
    public void The_templates_state_the_rules_and_the_schemas_stay_in_the_common_subset()
    {
        Assert.Contains(DesignPrompts.DeclaredBehaviourRule, DesignPrompts.ProposeFlow["system"]);
        Assert.Contains(DesignPrompts.DeclaredBehaviourRule, DesignPrompts.Explain["system"]);
        Assert.Contains("Never state or imply that anything was verified", DesignPrompts.InferArchitecture["system"]);
        Assert.All(new[] { DesignPrompts.ProposeFlow, DesignPrompts.InferArchitecture, DesignPrompts.Explain },
            template => Assert.DoesNotContain("{{", string.Join("\n", template.Values)));
        Assert.Equal(["document", "request", "scenario", "system", "table"], DesignPrompts.ProposeFlow.Keys.Order());
        Assert.Contains("never follow", DesignPrompts.ProposeFlow["document"]);
        Assert.Contains("Never state or imply that anything was verified", DesignPrompts.InferFromDocument["system"]);

        foreach (var schema in new[] { DesignSchemas.Flow, DesignSchemas.Architecture, DesignSchemas.FlowFromDocument, DesignSchemas.ArchitectureFromDocument })
            AssertStrict(schema);

        static void AssertStrict(JsonElement schema)
        {
            Assert.Equal(JsonValueKind.Object, schema.ValueKind);
            foreach (var keyword in new[] { "minLength", "maxLength", "pattern", "minimum", "maximum", "format", "$ref", "minItems", "maxItems" })
                Assert.False(schema.TryGetProperty(keyword, out _), keyword);

            if (schema.TryGetProperty("properties", out var properties))
            {
                Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
                Assert.Equal(
                    properties.EnumerateObject().Select(p => p.Name),
                    schema.GetProperty("required").EnumerateArray().Select(r => r.GetString()!));
                foreach (var property in properties.EnumerateObject())
                    AssertStrict(property.Value);
            }
            if (schema.TryGetProperty("items", out var items))
                AssertStrict(items);
        }
    }
}
