using System.Globalization;
using Deedbox;
using Microsoft.EntityFrameworkCore;
using Vellum.Modules.Modelling.Model;
using Vellum.Modules.Schemas;
using Vellum.Modules.Trace.Engine;
using Vellum.Modules.Trace.Scenarios;
using Vellum.Shared;

namespace Vellum.Modules.Promises.Checks;

/// <summary>What a check found, before it becomes a <see cref="VerificationRun"/>.</summary>
/// <param name="Inputs">Everything the check read, at the version it read it. The obligation hash is added when the run is recorded.</param>
public sealed record CheckOutcome(
    string Result, string Environment, string Scope, string Detail,
    IReadOnlyList<EvidenceItem> Evidence, IReadOnlyList<string> Limitations,
    ModelPin? ModelRevision = null, string? CommitSha = null, Guid? ScanId = null,
    RunInputs? Inputs = null, IReadOnlyList<AssertionOutcome>? Assertions = null, IReadOnlyList<Fault>? EffectiveFaults = null)
{
    /// <summary>Text inside the description limit. The cut never leaves half of a surrogate pair.</summary>
    public static string Bounded(string text)
    {
        if (text.Length <= TextRule.MaxDescription)
            return text;
        var cut = TextRule.MaxDescription - 1;
        if (char.IsHighSurrogate(text[cut - 1]))
            cut--;
        return text[..cut] + "…";
    }
}

/// <summary>
/// The <c>scenario-check</c>: plays the scenario on a pinned model through the trace engine,
/// with the obligation's faults added to the scenario's own, and evaluates the obligation's
/// assertions. The same scenario, model and schemas give the same result and the same
/// evidence hash. It shows what the declared design does and nothing about running code.
/// </summary>
public static class ScenarioCheck
{
    public const string Environment = "declared-design";

    public static readonly IReadOnlyList<string> Limitations =
    [
        "This result shows only that the declared design behaves so: the trace engine replays declared steps over declared contracts. It is not evidence about the source code or a running system.",
        "Time is virtual. Only declared timeouts and retry backoff advance it.",
    ];

    public static async Task<CheckOutcome> RunAsync(
        IEventStore store, SchemasDbContext schemasDb, Guid projectId, ScenarioCheckRecipe recipe, PinnedModel pinned, string obligationHash, CancellationToken ct)
    {
        var (state, scenarioVersion) = await PromiseWorld.LoadScenarioAsync(store, recipe.ScenarioId, ct);
        var model = pinned.State;
        var referenced = ContractDecider.ReferencedSchemaIds(model);
        var schemas = await schemasDb.Schemas.AsNoTracking()
            .Where(s => s.ProjectId == projectId && referenced.Contains(s.Id))
            .OrderBy(s => s.Id)
            .Select(s => new { s.Id, s.Content, s.Version })
            .ToListAsync(ct);

        return Run(
            recipe, pinned, state.DocumentIn(projectId) is { } document ? ScenarioSnapshot.Of(document, scenarioVersion) : null,
            schemas.ToDictionary(s => new SchemaId(s.Id), s => s.Content),
            [.. schemas.Select(s => new SchemaInput(s.Id, s.Version))], obligationHash);
    }

    /// <summary>The check as a function of its inputs. It reads nothing else.</summary>
    internal static CheckOutcome Run(
        ScenarioCheckRecipe recipe, PinnedModel pinned, ScenarioSnapshot? scenario,
        IReadOnlyDictionary<SchemaId, string> schemaContents, IReadOnlyList<SchemaInput> schemas, string obligationHash)
    {
        var inputs = new RunInputs(
            obligationHash, pinned.Pin.StreamId, pinned.Pin.Version, recipe.ScenarioId, scenario?.Version, scenario?.Hash, schemas);
        CheckOutcome NotRun(string scope, string detail, IReadOnlyList<Fault>? faults = null) => new(
            RunResult.NotRun, Environment, scope, CheckOutcome.Bounded(detail), [], Limitations, pinned.Pin, Inputs: inputs, EffectiveFaults: faults);

        if (scenario is null)
            return NotRun($"Scenario {recipe.ScenarioId} on {PromiseViews.Describe(pinned.Pin)}.", "The scenario no longer exists in the project.");

        var document = scenario.Document;
        var own = (document.Faults ?? []).Where(f => f is not null).ToList();
        var collisions = recipe.Faults.Where(f => own.Any(o => o.Target == f.Target)).Select(f => f.Target).ToList();
        var faults = (IReadOnlyList<Fault>)[.. own, .. recipe.Faults.Where(f => !collisions.Contains(f.Target))];
        var scope = string.Create(CultureInfo.InvariantCulture,
            $"Scenario '{document.Name}' ({recipe.ScenarioId}) at scenario version {scenario.Version}, played on {PromiseViews.Describe(pinned.Pin)} with {faults.Count} fault(s) ({own.Count} of the scenario, {recipe.Faults.Count} of the obligation) by trace engine {TraceEngine.Version}. {recipe.Expect.Count} assertion(s) evaluated.");

        // The scenario was changed after the obligation was written, and now faults a target
        // the obligation also faults. Neither fault replaces the other, so nothing is played.
        if (collisions.Count > 0)
        {
            return NotRun(scope,
                $"The scenario now declares a fault on {string.Join(", ", collisions)}, which this obligation also faults. An obligation adds faults; it does not replace one of the scenario. Change the obligation or the scenario.",
                faults);
        }

        var traceModel = TraceModelBuilder.Build(pinned.State, schemaContents);
        var played = document with { Faults = faults, Assertions = recipe.Expect };
        var result = TraceEngine.Run(traceModel, played);
        if (!result.IsValid)
        {
            return NotRun(scope,
                "The scenario cannot be played on this model: "
                + string.Join(" ", result.Issues.Select(i => $"[{i.Code}{(i.StepId.Length > 0 ? " " + i.StepId : "")}] {i.Message}")),
                faults);
        }

        // The same scenario with no fault at all, to see which steps the faults reach.
        var plain = faults.Count == 0 ? null : TraceEngine.Run(traceModel, played with { Faults = [] });
        var byId = recipe.Expect.ToDictionary(a => a.Id, StringComparer.Ordinal);
        var assertions = result.Assertions.Select(a =>
        {
            var reason = a.Passed ? Vacuity.Reason(byId[a.Id], traceModel, played, result, plain is { IsValid: true } ? plain : null) : null;
            return new AssertionOutcome(a.Id, a.Passed, a.Detail, reason is not null, reason);
        }).ToList();

        var failed = assertions.Where(a => !a.Passed).ToList();
        var vacuous = assertions.Where(a => a.Vacuous).ToList();
        var hash = PromiseJson.Hash(new
        {
            engine = result.EngineVersion,
            trace = result.Hash,
            assertions = recipe.Expect,
            results = assertions,
            inputs,
        });
        IReadOnlyList<EvidenceItem> evidence = [new EvidenceItem(EvidenceKind.Trace, Hash: hash, ScenarioId: recipe.ScenarioId)];
        CheckOutcome Outcome(string outcome, string detail) => new(
            outcome, Environment, scope, CheckOutcome.Bounded(detail), evidence, Limitations, pinned.Pin,
            Inputs: inputs, Assertions: assertions, EffectiveFaults: faults);

        if (failed.Count > 0)
        {
            return Outcome(RunResult.Failed,
                string.Create(CultureInfo.InvariantCulture, $"{failed.Count} of {assertions.Count} assertion(s) fail. ")
                + string.Join(" ", failed.Select(a => $"[{a.Id}] {a.Detail}")));
        }

        var vacuousText = vacuous.Count == 0
            ? ""
            : string.Create(CultureInfo.InvariantCulture, $" {vacuous.Count} of them are vacuous and show nothing: ")
              + string.Join(" ", vacuous.Select(a => $"[{a.Id}] {a.VacuousReason}"));
        // A check whose every assertion holds without being exercised establishes nothing. It is not a pass.
        return vacuous.Count == assertions.Count
            ? Outcome(RunResult.Unsupported,
                string.Create(CultureInfo.InvariantCulture, $"All {assertions.Count} assertion(s) hold, and every one is vacuous: the scenario does not exercise what they assert. ")
                + string.Join(" ", vacuous.Select(a => $"[{a.Id}] {a.VacuousReason}")))
            : Outcome(RunResult.Passed, string.Create(CultureInfo.InvariantCulture, $"All {assertions.Count} assertion(s) hold.") + vacuousText);
    }
}
