using System.Security.Claims;
using Deedbox;
using Vellum.Modules.Stress;
using Vellum.Modules.Stress.Engine;
using Vellum.Modules.Workspaces.Authorization;
using Vellum.Shared;

namespace Vellum.Modules.LoadTests;

/// <summary>
/// One number from the simulator beside the number that k6 measured for the same thing.
/// </summary>
/// <param name="Scope"><c>flow</c> or <c>operation</c>.</param>
/// <param name="Metric"><c>latencyP50</c>, <c>latencyP90</c>, <c>latencyP95</c>, <c>latencyP99</c>, <c>throughput</c> or <c>errorRate</c>.</param>
/// <param name="Estimated">Null when the simulation has no value for it.</param>
/// <param name="EstimatedLabel"><c>estimated</c>, or <c>unknown</c> when there is no value.</param>
/// <param name="Measured">Null when the script did not measure it. Null is not zero and is not a match.</param>
/// <param name="MeasuredLabel"><c>measured</c>, or <c>unknown</c> when there is no value.</param>
/// <param name="Difference">Measured minus estimated. Null when one side has no value.</param>
public sealed record EstimateRow(
    string Scope, Guid? Target, string Name, string Metric, string Unit,
    double? Estimated, string EstimatedLabel, double? Measured, string MeasuredLabel, double? Difference);

/// <summary>
/// A stored simulation run beside an imported k6 result of the same scenario. It states
/// differences and gives no verdict: an estimate that is near a measurement is not confirmed
/// by it for any other load.
/// </summary>
/// <param name="Differences">What the two sides do not share, and what limits the comparison.</param>
public sealed record EstimateComparisonDto(
    StressRunSummaryDto Estimate, MeasuredResultSummaryDto Measured,
    IReadOnlyList<EstimateRow> Rows, IReadOnlyList<string> Differences, string Note);

/// <param name="Relative">The change divided by the baseline. Null when the baseline is 0 or one side has no value.</param>
/// <param name="Verdict"><c>regressed</c>, <c>improved</c>, <c>unchanged</c> or <c>unknown</c>.</param>
public sealed record RegressionRow(
    string Scope, Guid? Target, string Name, string Metric, string Unit,
    double? Baseline, double? Candidate, double? Change, double? Relative, string Verdict);

/// <summary>Whether a flow or an operation has a regressed row.</summary>
public sealed record RegressionSubject(string Scope, Guid? Target, string Name, bool Regressed, IReadOnlyList<string> RegressedMetrics);

/// <summary>Two measured results of the same scenario and target. Both sides are measured.</summary>
/// <param name="Rule">How a row gets its verdict.</param>
public sealed record RegressionDto(
    string Label, MeasuredResultSummaryDto Baseline, MeasuredResultSummaryDto Candidate,
    IReadOnlyList<RegressionRow> Rows, IReadOnlyList<RegressionSubject> Subjects, int Regressions,
    IReadOnlyList<string> Differences, string Rule);

public static class Comparisons
{
    public const string Unknown = "unknown";

    public const string EstimateNote =
        "Estimated latency is the time of a successful attempt in the model. Measured latency is the duration of a request that got the expected status, "
        + "as k6 saw it, with the network. The comparison states differences and gives no verdict.";

    /// <summary>A latency is worse when it grows by more than this share and by more than <see cref="LatencyFloorMs"/>.</summary>
    public const double LatencyTolerance = 0.10;
    public const double LatencyFloorMs = 1;
    /// <summary>An error rate is worse when it grows by more than this, as a share of the requests.</summary>
    public const double ErrorRateTolerance = 0.001;
    /// <summary>A throughput is worse when it falls by more than this share.</summary>
    public const double ThroughputTolerance = 0.10;

    public const string RegressionRule =
        "A latency row is regressed when the candidate is more than 10 % and more than 1 ms above the baseline, and improved when it is that much below. "
        + "An error rate row is regressed when the candidate is more than 0.1 percentage points above the baseline, and improved when it is that much below. "
        + "A throughput row is regressed when the candidate is more than 10 % below the baseline, and improved when it is that much above. "
        + "A row with no value on one side is unknown. A flow or an operation is regressed when one of its rows is.";

    /// <summary><c>GET compare/estimate?resultId=&amp;runId=</c>.</summary>
    public static async Task<IResult> Estimate(
        Guid projectId, Guid resultId, Guid runId, ClaimsPrincipal user, WorkspaceAuthorizationService auth,
        LoadTestsDbContext db, StressDbContext stressDb, IEventStore store, CancellationToken ct)
    {
        await auth.RequireProjectRoleAsync(projectId, Targets.UserId(user), WorkspaceRole.Viewer, ct);

        if (await MeasuredResults.ReadAsync(db, store, projectId, resultId, ct) is not { } measured)
            return Targets.NotFound("Measured result");
        if (await StressRuns.ReadAsync(stressDb, store, projectId, runId, ct) is not { } run)
            return Targets.NotFound("Stress run");
        if (run.Run.ScenarioId != measured.Result.ScenarioId)
            return Invalid("runId", "The stress run and the measured result are of different scenarios.");

        return Results.Ok(new EstimateComparisonDto(run.Run, measured.Result, EstimateRows(run.Result, measured), EstimateDifferences(run, measured), EstimateNote));
    }

    private static List<EstimateRow> EstimateRows(StressResult estimate, MeasuredResultDto measured)
    {
        var rows = new List<EstimateRow>();
        void Add(string scope, Guid? target, string name, string metric, string unit, double? estimated, double? value) =>
            rows.Add(new(scope, target, name, metric, unit,
                estimated, estimated is null ? Unknown : StressResult.Estimated,
                value, value is null ? Unknown : MeasuredResults.Measured,
                estimated is { } e && value is { } m ? m - e : null));

        void Latency(string scope, Guid? target, string name, LatencySummary? estimated, MeasuredLatencyDto? value)
        {
            Add(scope, target, name, "latencyP50", "ms", estimated?.P50Ms, value?.P50Ms);
            Add(scope, target, name, "latencyP90", "ms", estimated?.P90Ms, value?.P90Ms);
            Add(scope, target, name, "latencyP95", "ms", estimated?.P95Ms, value?.P95Ms);
            Add(scope, target, name, "latencyP99", "ms", estimated?.P99Ms, value?.P99Ms);
        }

        var flow = measured.Result.Flow;
        Latency("flow", null, "Flow", estimate.Flow.Latency, flow.Latency);
        Add("flow", null, "Flow", "throughput", "per second", estimate.Flow.ThroughputPerSecond, flow.ThroughputPerSecond);
        Add("flow", null, "Flow", "errorRate", "of 1",
            estimate.Flow.Arrivals > 0 ? (double)estimate.Flow.Counts.Failed / estimate.Flow.Arrivals : null, flow.ErrorRate);

        // Every operation of either side gets its rows. An operation that the script did not
        // drive has an estimate and no measurement: its measured side is unknown.
        var estimated = estimate.Operations.ToDictionary(o => o.Id);
        var driven = measured.Operations.ToDictionary(o => o.OperationId);
        foreach (var id in estimated.Keys.Union(driven.Keys).Order())
        {
            var (e, m) = (estimated.GetValueOrDefault(id), driven.GetValueOrDefault(id));
            var name = e?.Name ?? m!.Name;
            // An operation with no counted request has no measured throughput: nothing was sent to it.
            var counted = m is { Requests: > 0 };
            Latency("operation", id, name, e?.Latency, m?.Latency);
            Add("operation", id, name, "throughput", "per second", e?.ThroughputPerSecond, counted ? m!.ThroughputPerSecond : null);
            Add("operation", id, name, "errorRate", "of 1",
                e is { Attempts: > 0 } ? 1 - (double)e.Counts.Completed / e.Attempts : null, m?.ErrorRate);
        }
        return rows;
    }

    private static List<string> EstimateDifferences(StressRunDto run, MeasuredResultDto measured)
    {
        var notes = new List<string>();
        var (estimate, result) = (run.Run, measured.Result);
        if (estimate.ModelStreamId != result.ModelStreamId)
            notes.Add("The simulation and the measurement are of different branches of the model.");
        else if (estimate.ModelVersion != result.ModelVersion)
            notes.Add($"The simulation used model version {estimate.ModelVersion}. The measurement is bound to model version {result.ModelVersion}.");
        if (estimate.ScenarioVersion != result.ScenarioVersion)
            notes.Add($"The simulation used scenario version {estimate.ScenarioVersion}. The measurement is bound to scenario version {result.ScenarioVersion}.");
        if (estimate.ProfileId != result.ProfileId)
            notes.Add("The simulation used another stress profile than the one the script was generated from, so the load can differ.");
        else if (estimate.ProfileVersion != result.ProfileVersion)
            notes.Add($"The simulation used profile version {estimate.ProfileVersion}. The script was generated from profile version {result.ProfileVersion}, so the load can differ.");
        if (result.Stale)
            notes.Add($"The measured result is stale: the {string.Join(" and the ", result.StaleBecause)} changed after the script was generated.");
        if (estimate.Outdated)
            notes.Add($"The simulation run is outdated: the {string.Join(" and the ", estimate.OutdatedBecause)} changed after the run.");
        if (measured.NotCovered.Count > 0)
            notes.Add($"The script does not perform {measured.NotCovered.Count} step(s) of the scenario. The measured flow is the driven requests only, and a step that is not driven has no measured value.");
        if (result.Flow.DroppedIterations > 0)
            notes.Add($"k6 dropped {result.Flow.DroppedIterations} iteration(s): the load of the run was below the rate of the profile.");
        return notes;
    }

    /// <summary><c>GET compare/results?baseline=&amp;candidate=</c>.</summary>
    public static async Task<IResult> Regression(
        Guid projectId, Guid baseline, Guid candidate, ClaimsPrincipal user, WorkspaceAuthorizationService auth,
        LoadTestsDbContext db, IEventStore store, CancellationToken ct)
    {
        await auth.RequireProjectRoleAsync(projectId, Targets.UserId(user), WorkspaceRole.Viewer, ct);

        if (await MeasuredResults.ReadAsync(db, store, projectId, baseline, ct) is not { } before
            || await MeasuredResults.ReadAsync(db, store, projectId, candidate, ct) is not { } after)
            return Targets.NotFound("Measured result");
        if (before.Result.ScenarioId != after.Result.ScenarioId)
            return Invalid("candidate", "The two results are of different scenarios. A regression is between two runs of the same scenario.");
        if (before.Result.Provenance.TargetId != after.Result.Provenance.TargetId)
            return Invalid("candidate", "The two results are of different targets. A regression is between two runs against the same target.");

        return Results.Ok(Compare(before, after));
    }

    internal static RegressionDto Compare(MeasuredResultDto before, MeasuredResultDto after)
    {
        var rows = new List<RegressionRow>();
        void Add(string scope, Guid? target, string name, string metric, string unit, double? a, double? b)
        {
            double? change = a is { } x && b is { } y ? y - x : null;
            double? relative = change is { } c && a is > 0 ? c / a.Value : null;
            rows.Add(new(scope, target, name, metric, unit, a, b, change, relative, Verdict(metric, a, change, relative)));
        }

        void Subject(string scope, Guid? target, string name, MeasuredLatencyDto? a, MeasuredLatencyDto? b, double? aThroughput, double? bThroughput, double? aErrors, double? bErrors)
        {
            Add(scope, target, name, "latencyP50", "ms", a?.P50Ms, b?.P50Ms);
            Add(scope, target, name, "latencyP95", "ms", a?.P95Ms, b?.P95Ms);
            Add(scope, target, name, "latencyP99", "ms", a?.P99Ms, b?.P99Ms);
            Add(scope, target, name, "throughput", "per second", aThroughput, bThroughput);
            Add(scope, target, name, "errorRate", "of 1", aErrors, bErrors);
        }

        var (x, y) = (before.Result.Flow, after.Result.Flow);
        Subject("flow", null, "Flow", x.Latency, y.Latency, x.ThroughputPerSecond, y.ThroughputPerSecond, x.ErrorRate, y.ErrorRate);

        var left = before.Operations.ToDictionary(o => o.OperationId);
        var right = after.Operations.ToDictionary(o => o.OperationId);
        foreach (var id in left.Keys.Union(right.Keys).Order())
        {
            var (a, b) = (left.GetValueOrDefault(id), right.GetValueOrDefault(id));
            Subject("operation", id, (b ?? a)!.Name, a?.Latency, b?.Latency,
                a is { Requests: > 0 } ? a.ThroughputPerSecond : null, b is { Requests: > 0 } ? b.ThroughputPerSecond : null,
                a?.ErrorRate, b?.ErrorRate);
        }

        var subjects = rows.GroupBy(r => (r.Scope, r.Target, r.Name))
            .Select(g => new RegressionSubject(g.Key.Scope, g.Key.Target, g.Key.Name,
                g.Any(r => r.Verdict == "regressed"), [.. g.Where(r => r.Verdict == "regressed").Select(r => r.Metric)]))
            .ToList();

        var notes = new List<string>();
        var (b0, c0) = (before.Result, after.Result);
        if (b0.Provenance.ScriptHash != c0.Provenance.ScriptHash)
            notes.Add("The two runs used different scripts. A change of the scenario, the model, the profile or the authorisation gives a new script.");
        if (b0.ScenarioVersion != c0.ScenarioVersion)
            notes.Add($"The baseline is bound to scenario version {b0.ScenarioVersion}, the candidate to version {c0.ScenarioVersion}.");
        if (b0.ModelStreamId != c0.ModelStreamId || b0.ModelVersion != c0.ModelVersion)
            notes.Add($"The baseline is bound to model version {b0.ModelVersion}, the candidate to version {c0.ModelVersion}.");
        if (b0.ProfileId != c0.ProfileId || b0.ProfileVersion != c0.ProfileVersion)
            notes.Add("The two scripts were generated from different stress profiles or profile versions, so the load can differ.");
        if (b0.Flow.DroppedIterations != c0.Flow.DroppedIterations)
            notes.Add($"k6 dropped {b0.Flow.DroppedIterations} iteration(s) in the baseline and {c0.Flow.DroppedIterations} in the candidate.");

        return new RegressionDto(
            MeasuredResults.Measured, b0, c0, rows, subjects, subjects.Count(s => s.Regressed), notes, RegressionRule);
    }

    private static string Verdict(string metric, double? baseline, double? change, double? relative)
    {
        if (change is not { } c)
            return Unknown;
        if (metric == "errorRate")
            return c > ErrorRateTolerance ? "regressed" : c < -ErrorRateTolerance ? "improved" : "unchanged";
        if (metric == "throughput")
            return relative is not { } share ? (baseline == 0 && c > 0 ? "improved" : "unchanged")
                : share < -ThroughputTolerance ? "regressed" : share > ThroughputTolerance ? "improved" : "unchanged";

        // A latency. A baseline of 0 ms has no share to grow by; the floor decides alone.
        var grows = relative is not { } r || r > LatencyTolerance;
        var falls = relative is { } f && f < -LatencyTolerance;
        return grows && c > LatencyFloorMs ? "regressed" : falls && c < -LatencyFloorMs ? "improved" : "unchanged";
    }

    private static IResult Invalid(string field, string message) =>
        Results.BadRequest(new ErrorResponse("validation_error", "Validation failed", Errors: [new FieldError(field, message)]));
}
