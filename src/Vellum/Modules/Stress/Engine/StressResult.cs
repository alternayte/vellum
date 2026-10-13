namespace Vellum.Modules.Stress.Engine;

/// <summary>Why a run was refused, or which limit stopped it. A refused run has no result.</summary>
/// <param name="Target">The step, the contract or the profile field that the issue is about.</param>
/// <param name="Limit">The name of the limit, for <c>stress-too-large</c>.</param>
public sealed record StressIssue(string Code, string Target, string Message, string? Limit = null);

public sealed record StressOutcome(IReadOnlyList<StressIssue> Issues, StressResult? Result);

/// <summary>
/// What a simulation of the declared flow under the declared load gives. Every number is an
/// estimate from a model: none is a measurement of a running system (SDD guarantee 8), and
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
    string Hash)
{
    public const string Estimated = "estimated";
}

/// <param name="EndedAtMs">The virtual instant of the last event.</param>
public sealed record StressWindow(double WarmUpMs, double DurationMs, double EndedAtMs);

/// <summary>
/// Counts over the flows that started at or after the warm-up, and all the work those flows
/// caused, also when it ran after the arrivals stopped.
/// </summary>
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
/// The four quarters of that window show whether the queue grows.
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
/// <param name="WastedCount">Calls that the element finished after their caller had given up.</param>
/// <param name="Wait">From arrival at the resource to the start of service.</param>
/// <param name="Latency">For a subscription: from publish to the end of the handler, for deliveries that succeeded. For an element: from arrival at the element to the release of the worker.</param>
public sealed record ResourceStats(
    string Label, Guid Id, string Name, string Kind, int Workers, int? QueueCapacity,
    long Arrived, long Waited, Counts Counts, long Retries, long Overflowed, long WastedCount, double WastedMs,
    double Utilisation, double ThroughputPerSecond, QueueStats Queue, LatencySummary Wait, LatencySummary Latency);

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
/// The resource with the highest utilisation among those whose queue grows, or none. It
/// names where the model saturates first under these assumptions; it does not say what a
/// running system does.
/// </summary>
public sealed record Bottleneck(
    string Label, bool Found, Guid? Id, string? Name, string? Kind,
    double? Utilisation, IReadOnlyList<double>? MeanDepthByQuarter,
    int? DepthAtEndOfArrivals, double? GrowthPerSecond, string Rule);

/// <summary>One input of the run with its label. Together the rows are every number the result depends on.</summary>
public sealed record AssumptionRow(
    string Group, Guid? Target, string Name, string Field, string Value, string Unit,
    AssumptionLabel Label, string? MeasurementRef);

/// <summary>A timeout or retry value that the profile sets against the model.</summary>
public sealed record OverrideRow(
    Guid Target, string Name, string Field, int ModelValue, int ProfileValue,
    AssumptionLabel Label, string? MeasurementRef);

/// <param name="Kind"><c>fixed</c> ships with every result; <c>run</c> is about this profile and scenario.</param>
public sealed record UnmodelledEffect(string Code, string Kind, string Text);

/// <summary>Not a probability. <paramref name="Rule"/> states how the level is chosen.</summary>
public sealed record Confidence(string Level, string Rule, IReadOnlyList<string> Reasons);

public sealed record StressMethods(string Percentiles, string Counting, string Sampling, string Time);
