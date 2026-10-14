using System.Globalization;
using System.Security.Claims;
using Deedbox;
using Vellum.Modules.Stress.Engine;
using Vellum.Modules.Workspaces.Authorization;
using Vellum.Shared;
using static Vellum.Shared.ResultExtensions;

namespace Vellum.Modules.Stress;

/// <param name="Scope"><c>flow</c>, <c>element</c>, <c>operation</c> or <c>subscription</c>.</param>
/// <param name="Delta">Right minus left. Null when one side has no value.</param>
public sealed record MetricDelta(string Scope, Guid? Target, string Name, string Metric, string Unit, double? Left, double? Right, double? Delta);

/// <summary>An input that the two runs do not share. A null side has no such input.</summary>
public sealed record InputDifference(string Input, Guid? Target, string Name, string? Left, string? Right);

/// <summary>
/// Two stored runs side by side: the differences of their numbers and of their inputs. It
/// states differences and gives no verdict; both sides are estimates.
/// </summary>
/// <param name="SameResult">True only when the two runs have the same result hash. An empty list of inputs does not say it.</param>
public sealed record StressComparisonDto(
    string Label, StressRunSummaryDto Left, StressRunSummaryDto Right,
    IReadOnlyList<MetricDelta> Metrics, IReadOnlyList<InputDifference> Inputs, string Note, bool SameResult);

public static class StressCompare
{
    public const string Note =
        "The comparison lists differences between two estimates. It gives no verdict. "
        + "Runs with different seeds differ by chance too.";

    public static async Task<IResult> Handle(
        Guid projectId, Guid left, Guid right, ClaimsPrincipal user, WorkspaceAuthorizationService auth,
        AppDbContext db, IEventStore store, CancellationToken ct)
    {
        await auth.RequireProjectRoleAsync(projectId, user.UserId(), WorkspaceRole.Viewer, ct);

        if (await StressRuns.ReadAsync(db, store, projectId, left, ct) is not { } a
            || await StressRuns.ReadAsync(db, store, projectId, right, ct) is not { } b)
            return NotFound("Stress run");

        return Results.Ok(new StressComparisonDto(StressResult.Estimated, a.Run, b.Run, Metrics(a.Result, b.Result), Inputs(a, b), Note,
            a.Run.Hash == b.Run.Hash));
    }

    private static List<MetricDelta> Metrics(StressResult left, StressResult right)
    {
        var rows = new List<MetricDelta>();
        void Add(string scope, Guid? target, string name, string metric, string unit, double? a, double? b) =>
            rows.Add(new(scope, target, name, metric, unit, a, b, a is { } x && b is { } y ? y - x : null));

        void Flow(string metric, string unit, Func<StressResult, double?> read) => Add("flow", null, "Flow", metric, unit, read(left), read(right));
        Flow("arrivals", "flows", r => r.Flow.Arrivals);
        Flow("completed", "flows", r => r.Flow.Counts.Completed);
        Flow("failed", "flows", r => r.Flow.Counts.Failed);
        Flow("rejected", "calls", r => r.Flow.Counts.Rejected);
        Flow("timedOut", "calls", r => r.Flow.Counts.TimedOut);
        Flow("dropped", "messages", r => r.Flow.Counts.Dropped);
        Flow("deadLettered", "messages", r => r.Flow.Counts.DeadLettered);
        Flow("throughput", "per second", r => r.Flow.ThroughputPerSecond);
        Flow("latencyMean", "ms", r => r.Flow.Latency.MeanMs);
        Flow("latencyP50", "ms", r => r.Flow.Latency.P50Ms);
        Flow("latencyP90", "ms", r => r.Flow.Latency.P90Ms);
        Flow("latencyP95", "ms", r => r.Flow.Latency.P95Ms);
        Flow("latencyP99", "ms", r => r.Flow.Latency.P99Ms);
        Flow("latencyMax", "ms", r => r.Flow.Latency.MaxMs);
        Flow("timeToDrain", "ms", r => r.Drain.TimeToDrainMs);

        // A resource or an operation that only one run has gets rows with one empty side.
        Resources("element", left.Elements, right.Elements);
        Resources("subscription", left.Subscriptions, right.Subscriptions);

        var leftOperations = left.Operations.ToDictionary(o => o.Id);
        var rightOperations = right.Operations.ToDictionary(o => o.Id);
        foreach (var id in leftOperations.Keys.Union(rightOperations.Keys).Order())
        {
            var (x, y) = (leftOperations.GetValueOrDefault(id), rightOperations.GetValueOrDefault(id));
            var name = (x ?? y)!.Name;
            Add("operation", id, name, "completed", "attempts", x?.Counts.Completed, y?.Counts.Completed);
            Add("operation", id, name, "failed", "attempts", x?.Counts.Failed, y?.Counts.Failed);
            Add("operation", id, name, "rejected", "attempts", x?.Counts.Rejected, y?.Counts.Rejected);
            Add("operation", id, name, "timedOut", "attempts", x?.Counts.TimedOut, y?.Counts.TimedOut);
            Add("operation", id, name, "retries", "attempts", x?.Retries, y?.Retries);
            Add("operation", id, name, "latencyP95", "ms", x?.Latency.P95Ms, y?.Latency.P95Ms);
        }

        return rows;

        void Resources(string scope, IReadOnlyList<ResourceStats> leftSide, IReadOnlyList<ResourceStats> rightSide)
        {
            var a = leftSide.ToDictionary(r => r.Id);
            var b = rightSide.ToDictionary(r => r.Id);
            foreach (var id in a.Keys.Union(b.Keys).Order())
            {
                var (x, y) = (a.GetValueOrDefault(id), b.GetValueOrDefault(id));
                var name = (x ?? y)!.Name;
                Add(scope, id, name, "utilisation", "of 1", x?.Utilisation, y?.Utilisation);
                Add(scope, id, name, "meanQueueDepth", "waiting", x?.Queue.MeanDepth, y?.Queue.MeanDepth);
                Add(scope, id, name, "waitMean", "ms", x?.Wait.MeanMs, y?.Wait.MeanMs);
                Add(scope, id, name, "latencyP95", "ms", x?.Latency.P95Ms, y?.Latency.P95Ms);
                Add(scope, id, name, "completed", "", x?.Counts.Completed, y?.Counts.Completed);
                if (scope == "element")
                {
                    Add(scope, id, name, "rejected", "", x?.Counts.Rejected, y?.Counts.Rejected);
                    // Work that held a worker and is not completed work. A run of engine version 1 has no count of failed calls.
                    Add(scope, id, name, "wasted", "", x?.WastedCount, y?.WastedCount);
                    Add(scope, id, name, "failedInside", "", x?.FailedInside, y?.FailedInside);
                }
                else
                {
                    Add(scope, id, name, "deadLettered", "", x?.Counts.DeadLettered, y?.Counts.DeadLettered);
                    Add(scope, id, name, "dropped", "", x?.Counts.Dropped, y?.Counts.Dropped);
                }
            }
        }
    }

    /// <summary>
    /// What pins each run, then every limit, assumption and override whose value or label is
    /// not the same on both sides. Whether the two results are the same is a question for
    /// their hashes (<see cref="StressComparisonDto.SameResult"/>), not for this list.
    /// </summary>
    private static List<InputDifference> Inputs(StressRunDto left, StressRunDto right)
    {
        var rows = new List<InputDifference>();
        void Pin(string input, string a, string b)
        {
            if (a != b) rows.Add(new(input, null, "Run", a, b));
        }

        Pin("profile", $"{left.Run.ProfileId} version {left.Run.ProfileVersion}", $"{right.Run.ProfileId} version {right.Run.ProfileVersion}");
        Pin("model", $"{left.Run.ModelStreamId} version {left.Run.ModelVersion}", $"{right.Run.ModelStreamId} version {right.Run.ModelVersion}");
        Pin("scenario", $"{left.Run.ScenarioId} version {left.Run.ScenarioVersion}", $"{right.Run.ScenarioId} version {right.Run.ScenarioVersion}");
        Pin("seed", left.Run.Seed, right.Run.Seed);
        Pin("engineVersion", left.Run.EngineVersion, right.Run.EngineVersion);

        // A result of engine version 1 does not state its limits.
        var (leftLimits, rightLimits) = (Limits(left.Result.Limits), Limits(right.Result.Limits));
        foreach (var name in leftLimits.Keys.Union(rightLimits.Keys).Order(StringComparer.Ordinal))
            Pin($"limits.{name}", leftLimits.GetValueOrDefault(name, "not recorded"), rightLimits.GetValueOrDefault(name, "not recorded"));

        var a = Rows(left.Result);
        var b = Rows(right.Result);
        foreach (var key in a.Keys.Union(b.Keys)
                     .OrderBy(k => k.Group, StringComparer.Ordinal).ThenBy(k => k.Target).ThenBy(k => k.Field, StringComparer.Ordinal))
        {
            var hasLeft = a.TryGetValue(key, out var x);
            var hasRight = b.TryGetValue(key, out var y);
            if (hasLeft && hasRight && x.Text == y.Text)
                continue;
            rows.Add(new($"{key.Group}.{key.Field}", key.Target, hasLeft ? x.Name : y.Name, hasLeft ? x.Text : null, hasRight ? y.Text : null));
        }

        return rows;
    }

    private static Dictionary<string, string> Limits(EffectiveLimits? limits) => limits is null ? [] : new()
    {
        ["maxDurationSeconds"] = StressProfileRules.Number(limits.MaxDurationSeconds),
        ["maxDrainSeconds"] = StressProfileRules.Number(limits.MaxDrainSeconds),
        ["maxArrivals"] = limits.MaxArrivals.ToString(CultureInfo.InvariantCulture),
        ["maxEvents"] = limits.MaxEvents.ToString(CultureInfo.InvariantCulture),
        ["maxQueued"] = limits.MaxQueued.ToString(CultureInfo.InvariantCulture),
        ["maxPendingEvents"] = limits.MaxPendingEvents.ToString(CultureInfo.InvariantCulture),
        ["maxSamples"] = limits.MaxSamples.ToString(CultureInfo.InvariantCulture),
        ["maxSeriesPoints"] = limits.MaxSeriesPoints.ToString(CultureInfo.InvariantCulture),
        ["maxTargets"] = limits.MaxTargets.ToString(CultureInfo.InvariantCulture),
    };

    /// <summary>
    /// The inputs of a result by a key that names one row. The keys of the rows:
    /// arrivals by field (a burst window by its index); a resource, a service time, a
    /// subscription and a model policy by target and field, one entry for each target; a
    /// branch by its step ID; an override by target and field; an outage window by its target
    /// and its place in time order among the windows of that target. Engine version 1 gave
    /// every window of a target the same field, so the place is counted here and not read.
    /// </summary>
    private static Dictionary<(string Group, Guid? Target, string Field), (string Name, string Text)> Rows(StressResult result)
    {
        var map = new Dictionary<(string, Guid?, string), (string, string)>();
        var windows = new Dictionary<Guid, int>();
        foreach (var a in result.Assumptions.Where(a => a.Group != "seed"))
        {
            var field = a.Group switch
            {
                // A branch row has no target ID: its step ID is its name.
                "branch" => $"{a.Name}.{a.Field}",
                // The rows of a target are in time order in the result.
                "outage" => $"window[{windows[a.Target!.Value] = windows.GetValueOrDefault(a.Target!.Value, -1) + 1}]",
                _ => a.Field,
            };
            Add((a.Group, a.Target, field), (a.Name, Text(a.Value, a.Unit, a.Label, a.MeasurementRef)));
        }
        foreach (var o in result.Overrides)
            Add(("override", o.Target, o.Field), (o.Name, Text(o.ProfileValue.ToString(CultureInfo.InvariantCulture), "", o.Label, o.MeasurementRef)));
        return map;

        // Two rows with one key would hide one of them. No rule above gives such a pair; a result that has one still shows both.
        void Add((string Group, Guid? Target, string Field) key, (string, string) row)
        {
            for (var copy = 2; !map.TryAdd(key, row); copy++)
                key = key with { Field = $"{key.Field} #{copy}" };
        }
    }

    private static string Text(string value, string unit, AssumptionLabel label, string? measurementRef) =>
        $"{value}{(unit.Length > 0 ? " " + unit : "")} ({label.ToString().ToLowerInvariant()}{(measurementRef is null ? "" : ": " + measurementRef)})";
}
