using System.Text.Json.Serialization;

namespace Vellum.Modules.Stress.Engine;

/// <summary>Why a run was refused, or which limit stopped it. A refused run has no result.</summary>
/// <param name="Target">The step, the contract or the profile field that the issue is about.</param>
/// <param name="Limit">The name of the limit, for <c>stress-too-large</c>.</param>
public sealed record StressIssue(string Code, string Target, string Message, string? Limit = null);

public sealed record StressOutcome(IReadOnlyList<StressIssue> Issues, StressResult? Result);

/// <summary>
/// What a simulation of the declared flow under the declared load gives. Every number is an
/// estimate from a model: none is a measurement of a running system, and
/// every block says so in <c>Label</c>.
/// </summary>
public sealed record StressResult(
    string EngineVersion,
    string Label,
    string Seed,
    StressWindow Window,
    FlowStats Flow,
    IReadOnlyList<ResourceStats> Elements,
    IReadOnlyList<OperationStats> Operations,
    IReadOnlyList<ResourceStats> Subscriptions,
    IReadOnlyList<ChannelStats> Channels,
    StressSeries Series,
    DrainStats Drain,
    Bottleneck Bottleneck,
    IReadOnlyList<AssumptionRow> Assumptions,
    IReadOnlyList<OverrideRow> Overrides,
    IReadOnlyList<UnmodelledEffect> Unmodelled,
    Confidence Confidence,
    StressMethods Methods,
    EffectiveLimits? Limits,
    string Hash)
{
    public const string Estimated = "estimated";
}

/// <summary>
/// The limits that the run used. A limit can change a result (the drain limit, the series
/// points, the kept samples) or decide whether there is one, so all of them are inputs and
/// are in the hash. The limit of real time is not here: it never changes a result. A result
/// of engine version 1 has no limits.
/// </summary>
public sealed record EffectiveLimits(
    double MaxDurationSeconds, double MaxDrainSeconds, long MaxArrivals, long MaxEvents,
    int MaxQueued, int MaxPendingEvents, int MaxSamples, int MaxSeriesPoints, int MaxTargets);

/// <param name="EndedAtMs">
/// The last virtual instant at which work was open: a flow, a call, a write or a message.
/// When work was left at the drain limit, it is the drain limit. In a result of engine
/// version 1 it is the instant of the last event, timers that found nothing to do included.
/// </param>
public sealed record StressWindow(double WarmUpMs, double DurationMs, double EndedAtMs);

/// <summary>
/// Counts over the flows that started at or after the warm-up, and all the work those flows
/// caused, also when it ran after the arrivals stopped.
/// </summary>
/// <param name="Completed">
/// Work that ended with success. For an element: calls that responded with success to a caller
/// that still waited, and writes. A call that its caller had given up, and a call that failed
/// because a step inside it failed, are not here (see <see cref="ResourceStats.WastedCount"/>
/// and <see cref="ResourceStats.FailedInside"/>). A result of engine version 1 counted them here.
/// </param>
public sealed record Counts(long Completed, long Failed, long Rejected, long TimedOut, long Dropped, long DeadLettered);

/// <summary>
/// Sorted nearest-rank percentiles: the value at rank ceil(p / 100 * n) of the n kept samples
/// in ascending order. Count, minimum, mean and maximum cover every sample. When
/// <paramref name="Reservoir"/> is true, more samples arrived than the limit keeps, and the
/// percentiles come from a uniform sample of <paramref name="Kept"/> of them.
/// </summary>
public sealed record LatencySummary(
    string Label, long Count, long Kept, bool Reservoir,
    double? MinMs, double? MeanMs, double? P50Ms, double? P90Ms, double? P95Ms, double? P99Ms, double? MaxMs);

/// <param name="Arrivals">Flows that started at or after the warm-up.</param>
/// <param name="ThroughputPerSecond">Flows that completed between the warm-up and the end of the arrivals, per second of that window.</param>
/// <param name="Latency">From the start of a flow to the end of its last synchronous step, for completed flows.</param>
public sealed record FlowStats(
    string Label, long Arrivals, Counts Counts, double ThroughputPerSecond, LatencySummary Latency);

/// <summary>
/// The time-weighted queue of a resource between the warm-up and the end of the arrivals.
/// The four quarters of that window describe the queue over time. No rule reads them: the
/// saturation rule and the growth rule use <see cref="Saturation"/>.
/// </summary>
public sealed record QueueStats(
    double MeanDepth, IReadOnlyList<double> MeanDepthByQuarter, int MaxDepth, int DepthAtEndOfArrivals);

/// <summary>
/// A pool of workers with a queue: an element that serves calls or writes, or a subscription
/// with its consumers.
/// </summary>
/// <param name="Kind"><c>element</c> or <c>subscription</c>.</param>
/// <param name="Utilisation">Busy worker time between the warm-up and the end of the arrivals, divided by workers times the length of that window.</param>
/// <param name="Arrived">Work that asked for a worker, rejected work included.</param>
/// <param name="Waited">Work that found no free worker and waited in the queue.</param>
/// <param name="WastedCount">Calls that the element finished after their caller had given up. They held a worker and are not in <c>Counts.Completed</c>.</param>
/// <param name="ThroughputPerSecond">Work that ended with success between the warm-up and the end of the arrivals, per second of that window. Wasted and failed work is not in it.</param>
/// <param name="Wait">From arrival at the resource to the start of service.</param>
/// <param name="Latency">For a subscription: from publish to the end of the handler, for deliveries that succeeded. For an element: from arrival at the element to the release of the worker.</param>
/// <param name="FailedInside">Calls that held a worker and failed because a step inside them failed, while their caller still waited. Null in a result of engine version 1.</param>
/// <param name="Saturation">What the saturation rule found for this resource. Null in a result of engine version 1.</param>
public sealed record ResourceStats(
    string Label, Guid Id, string Name, string Kind, int Workers, int? QueueCapacity,
    long Arrived, long Waited, Counts Counts, long Retries, long Overflowed, long WastedCount, double WastedMs,
    double Utilisation, double ThroughputPerSecond, QueueStats Queue, LatencySummary Wait, LatencySummary Latency,
    long? FailedInside, Saturation? Saturation);

/// <summary>
/// The inputs and the outcome of the saturation rule for one resource
/// (<see cref="StressEngine.BottleneckRule"/>). The numbers cover the measured window, from
/// the warm-up to the end of the arrivals; <paramref name="TurnedAway"/> also covers the drain.
/// </summary>
/// <param name="Saturated">True when the resource turned work away or has a backlog window.</param>
/// <param name="TurnedAway">Calls, writes and messages that found every worker busy and the queue full, from the warm-up to the end of the run.</param>
/// <param name="FirstTurnedAwayMs">The instant of the first piece of work that was turned away, or null.</param>
/// <param name="Arrivals">Work that asked for a worker inside the measured window: n of the rule.</param>
/// <param name="ArrivalVariation">The squared coefficient of variation of the gaps between those arrivals; 1 when it has no value.</param>
/// <param name="ServiceVariation">The squared coefficient of variation of the times that a worker held work it served; 1 when it has no value.</param>
/// <param name="NoiseBound">The queue depth that a resource with spare capacity reaches by chance with a probability of at most 1 % for each of the two parts of the rule.</param>
/// <param name="Windows">The backlog windows whose peak is at or above <paramref name="NoiseBound"/>, in time order. At most <see cref="StressEngine.MaxBacklogWindows"/>: the ones with the highest peak.</param>
/// <param name="MoreWindows">True when the list is full, so the run can have more windows than it lists.</param>
/// <param name="DepthAtMidWindow">The queue depth at the middle of the measured window.</param>
/// <param name="QueueGrows">The outcome of the growth rule (<see cref="StressEngine.GrowthRule"/>). It is a statement of its own: a saturated resource can have a queue that does not grow.</param>
/// <param name="GrowthPerSecond">Depth at the end of the arrivals minus depth at the middle of the measured window, per second. Null when the queue does not grow.</param>
public sealed record Saturation(
    bool Saturated,
    long TurnedAway, double? FirstTurnedAwayMs, double? LastTurnedAwayMs,
    long Arrivals, double ArrivalVariation, double ServiceVariation, int NoiseBound,
    IReadOnlyList<BacklogWindow> Windows, bool MoreWindows,
    int DepthAtMidWindow, bool QueueGrows, double? GrowthPerSecond);

/// <summary>
/// A time in which work waited at a resource without a break. It starts when the queue stops
/// being empty and ends when the queue is empty again; both instants can be outside the
/// measured window.
/// </summary>
/// <param name="ToMs">Null when work still waited at the end of the run.</param>
/// <param name="PeakDepth">The deepest queue of the window inside the measured window.</param>
public sealed record BacklogWindow(double FromMs, double? ToMs, int PeakDepth, double PeakAtMs);

/// <param name="Attempts">Every attempt of every call, the first one included.</param>
/// <param name="Exhausted">Calls that failed in their last attempt.</param>
/// <param name="Latency">From the start of an attempt to its response, for attempts that succeeded.</param>
public sealed record OperationStats(
    string Label, Guid Id, string Name, Guid OwnerId,
    int TimeoutMs, int MaxAttempts, int BackoffMs,
    long Attempts, Counts Counts, long Retries, long Exhausted, long Wasted,
    double ThroughputPerSecond, LatencySummary Latency);

public sealed record ChannelStats(string Label, Guid Id, string Name, long Published, long PublishFailed);

/// <summary>
/// Samples at a fixed virtual interval; point i is at (i + 1) * <paramref name="IntervalMs"/>.
/// Rates are per second over the interval that ends at the point; depths are at the point.
/// </summary>
/// <param name="Truncated">True when the run went on after the last point that the limit allows.</param>
public sealed record StressSeries(
    string Label, double IntervalMs, int Points, bool Truncated,
    IReadOnlyList<double> ArrivalsPerSecond,
    IReadOnlyList<double> ThroughputPerSecond,
    IReadOnlyList<double> FailuresPerSecond,
    IReadOnlyList<int> InFlight,
    IReadOnlyList<ResourceSeries> Resources);

/// <param name="QueueDepth">Work that waits for a worker: the backlog of a subscription, or the waiting calls of an element.</param>
public sealed record ResourceSeries(Guid Id, string Name, string Kind, IReadOnlyList<int> QueueDepth, IReadOnlyList<int> InService);

/// <param name="TimeToDrainMs">From the end of the arrivals to the instant no work is left. Null when work was left at the drain limit.</param>
/// <param name="LeftInSystem">Flows, calls, writes and messages still open when the run ended.</param>
public sealed record DrainStats(string Label, bool Drained, double? TimeToDrainMs, long LeftInSystem);

/// <summary>
/// The saturated resource with the highest utilisation, or none. It names where the model
/// saturates under these assumptions; it does not say what a running system does. The numbers
/// behind it are in <see cref="ResourceStats.Saturation"/> of the named resource.
/// </summary>
/// <param name="Saturated">Every saturated resource in the order of the rule; the first one is the bottleneck. Null in a result of engine version 1.</param>
/// <param name="Rule">The rule that this result used, in words. A result of engine version 1 states the rule of that version.</param>
/// <param name="GrowthRule">The rule behind <see cref="Saturation.QueueGrows"/>. Null in a result of engine version 1.</param>
public sealed record Bottleneck(
    string Label, bool Found, Guid? Id, string? Name, string? Kind, double? Utilisation,
    IReadOnlyList<Guid>? Saturated, string Rule, string? GrowthRule)
{
    // What engine version 1 stored. A stored run of that version keeps its numbers; version 2 writes none of them.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<double>? MeanDepthByQuarter { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? DepthAtEndOfArrivals { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? GrowthPerSecond { get; init; }
}

/// <summary>One input of the run with its label. Together the rows are every number the result depends on.</summary>
public sealed record AssumptionRow(
    string Group, Guid? Target, string Name, string Field, string Value, string Unit,
    AssumptionLabel Label, string? MeasurementRef)
{
    /// <summary>
    /// For a measured value: true when the measured result was bound to another model version
    /// or scenario version than the run used, or was not in the project. Null for a declared
    /// value and in a result of engine version 1 or 2.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? MeasurementStale { get; init; }
}

/// <summary>
/// Why the measurement behind a measured service time does not count for the confidence level.
/// </summary>
/// <param name="Stale">True when the measured result is of another model version or scenario version than the run, or is gone.</param>
/// <param name="Reason">One sentence for the confidence reasons.</param>
public sealed record MeasurementDoubt(bool Stale, string Reason);

/// <summary>A timeout or retry value that the profile sets against the model.</summary>
public sealed record OverrideRow(
    Guid Target, string Name, string Field, int ModelValue, int ProfileValue,
    AssumptionLabel Label, string? MeasurementRef);

/// <param name="Kind"><c>fixed</c> ships with every result; <c>run</c> is about this profile and scenario.</param>
public sealed record UnmodelledEffect(string Code, string Kind, string Text);

/// <summary>Not a probability. <paramref name="Rule"/> states how the level is chosen.</summary>
public sealed record Confidence(string Level, string Rule, IReadOnlyList<string> Reasons);

public sealed record StressMethods(string Percentiles, string Counting, string Sampling, string Time);
