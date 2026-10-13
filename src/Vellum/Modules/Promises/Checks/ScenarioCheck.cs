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
public sealed record CheckOutcome(
    string Result, string Environment, string Scope, string Detail,
    IReadOnlyList<EvidenceItem> Evidence, IReadOnlyList<string> Limitations,
    ModelPin? ModelRevision = null, string? CommitSha = null, Guid? ScanId = null)
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
/// with the obligation's faults over the scenario's own, and evaluates the obligation's
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
        IEventStore store, SchemasDbContext schemasDb, Guid projectId, ScenarioCheckRecipe recipe, PinnedModel pinned, CancellationToken ct)
    {
        CheckOutcome NotRun(string detail) => new(
            RunResult.NotRun, Environment, $"Scenario {recipe.ScenarioId} on {PromiseViews.Describe(pinned.Pin)}.",
            detail, [], Limitations, pinned.Pin);

        Scenario? scenario;
        long scenarioVersion;
        try
        {
            var loaded = await store.Load<ScenarioState>(StreamId.From(recipe.ScenarioId), ct);
            (scenario, scenarioVersion) = (loaded.State.DocumentIn(projectId), loaded.Version);
        }
        catch (DeedboxException e) when (e.Code == DeedboxError.StreamTypeMismatch)
        {
            scenario = null;
            scenarioVersion = 0;
        }
        if (scenario is null)
            return NotRun("The scenario no longer exists in the project.");

        var model = pinned.State;
        var referenced = ContractDecider.ReferencedSchemaIds(model);
        var schemas = await schemasDb.Schemas.AsNoTracking()
            .Where(s => s.ProjectId == projectId && referenced.Contains(s.Id))
            .Select(s => new { s.Id, s.Content })
            .ToListAsync(ct);

        var played = scenario with { Faults = MergeFaults(scenario.Faults, recipe.Faults), Assertions = recipe.Expect };
        var result = TraceEngine.Run(
            TraceModelBuilder.Build(model, schemas.ToDictionary(s => new SchemaId(s.Id), s => s.Content)), played);

        var scope = string.Create(CultureInfo.InvariantCulture,
            $"Scenario '{scenario.Name}' ({recipe.ScenarioId}) at scenario version {scenarioVersion}, played on {PromiseViews.Describe(pinned.Pin)} with {played.Faults.Count} fault(s) by trace engine {result.EngineVersion}. {recipe.Expect.Count} assertion(s) evaluated.");

        if (!result.IsValid)
        {
            return new CheckOutcome(
                RunResult.NotRun, Environment, scope,
                CheckOutcome.Bounded("The scenario cannot be played on this model: "
                    + string.Join(" ", result.Issues.Select(i => $"[{i.Code}{(i.StepId.Length > 0 ? " " + i.StepId : "")}] {i.Message}"))),
                [], Limitations, pinned.Pin);
        }

        var failed = result.Assertions.Where(a => !a.Passed).ToList();
        var hash = PromiseJson.Hash(new
        {
            engine = result.EngineVersion,
            trace = result.Hash,
            assertions = result.Assertions.Select(a => new { a.Id, a.Passed }),
        });

        return new CheckOutcome(
            failed.Count == 0 ? RunResult.Passed : RunResult.Failed, Environment, scope,
            CheckOutcome.Bounded(failed.Count == 0
                ? string.Create(CultureInfo.InvariantCulture, $"All {result.Assertions.Count} assertion(s) hold.")
                : string.Create(CultureInfo.InvariantCulture, $"{failed.Count} of {result.Assertions.Count} assertion(s) fail. ")
                  + string.Join(" ", failed.Select(a => $"[{a.Id}] {a.Detail}"))),
            [new EvidenceItem(EvidenceKind.Trace, Hash: hash, ScenarioId: recipe.ScenarioId)],
            Limitations, pinned.Pin);
    }

    /// <summary>The scenario's faults, with the obligation's fault in place of the scenario's for the same target.</summary>
    internal static IReadOnlyList<Fault> MergeFaults(IReadOnlyList<Fault>? scenario, IReadOnlyList<Fault> obligation)
    {
        var overridden = obligation.Select(f => f.Target).ToHashSet();
        return [.. (scenario ?? []).Where(f => !overridden.Contains(f.Target)), .. obligation];
    }
}
