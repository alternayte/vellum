namespace Vellum.Modules.Stress.Engine;

/// <summary>
/// Where a number in a profile comes from (SDD guarantee 2). A person who types a number
/// declares it. A number is <see cref="Measured"/> only when it names a measurement that
/// was imported.
/// </summary>
public enum AssumptionLabel { Declared, Measured }

public enum ArrivalKind { Constant, Poisson, Ramp, Burst }

public enum DistributionKind { Fixed, Exponential, Lognormal, Uniform }

/// <summary>
/// The load and the capacity that one saved scenario is simulated under. Every number is an
/// assumption with a label. Timeouts and retries are not here: they come from the model, and
/// <see cref="Overrides"/> lists each one the profile replaces.
/// </summary>
/// <param name="Seed">An unsigned 64-bit integer in decimal digits. It is text because JSON readers lose digits of a large number.</param>
public sealed record StressProfile(
    string Name,
    Guid ScenarioId,
    string Seed,
    Arrivals Arrivals,
    IReadOnlyList<ResourceAssumption> Resources,
    IReadOnlyList<ServiceTimeAssumption> ServiceTimes,
    IReadOnlyList<SubscriptionAssumption> Subscriptions,
    IReadOnlyList<BranchAssumption> Branches,
    IReadOnlyList<PolicyOverride> Overrides,
    IReadOnlyList<Outage> Outages);

/// <summary>
/// When flows start. <c>constant</c> starts one every 1/rate seconds. <c>poisson</c> has
/// exponential gaps with that mean. <c>ramp</c> is a Poisson process whose rate moves in a
/// straight line from <paramref name="RatePerSecond"/> to <paramref name="ToRatePerSecond"/>
/// over the duration. <c>burst</c> is a Poisson process at <paramref name="RatePerSecond"/>,
/// except inside each window of <paramref name="Bursts"/>, where the rate of the window applies.
/// </summary>
/// <param name="WarmUpSeconds">Flows that start before this instant are simulated and left out of the statistics.</param>
public sealed record Arrivals(
    ArrivalKind Kind,
    double RatePerSecond,
    double? ToRatePerSecond,
    IReadOnlyList<BurstWindow>? Bursts,
    double DurationSeconds,
    double WarmUpSeconds,
    AssumptionLabel Label = AssumptionLabel.Declared,
    string? MeasurementRef = null);

public sealed record BurstWindow(double FromSeconds, double ToSeconds, double RatePerSecond);

/// <summary>The workers of an element that serves calls or writes, and how many calls may wait for one.</summary>
/// <param name="QueueCapacity">Null for no bound of its own; <see cref="StressLimits.MaxQueued"/> still bounds the run.</param>
public sealed record ResourceAssumption(
    Guid ElementId, int Concurrency, int? QueueCapacity,
    AssumptionLabel Label = AssumptionLabel.Declared, string? MeasurementRef = null);

/// <summary>
/// How long one piece of work holds a worker, without its synchronous children.
/// <paramref name="Target"/> is an operation (one call), a subscription (one delivery to its
/// handler) or a store element (one write).
/// </summary>
public sealed record ServiceTimeAssumption(
    Guid Target, Distribution Time,
    AssumptionLabel Label = AssumptionLabel.Declared, string? MeasurementRef = null);

/// <summary>
/// <c>fixed</c> uses <paramref name="Ms"/>; <c>exponential</c> uses <paramref name="MeanMs"/>;
/// <c>lognormal</c> uses <paramref name="MedianMs"/> and <paramref name="P95Ms"/>;
/// <c>uniform</c> uses <paramref name="MinMs"/> and <paramref name="MaxMs"/>.
/// </summary>
public sealed record Distribution(
    DistributionKind Kind,
    double? Ms = null, double? MeanMs = null,
    double? MedianMs = null, double? P95Ms = null,
    double? MinMs = null, double? MaxMs = null);

/// <summary>The parallel deliveries of a subscription and how many messages may wait for one.</summary>
public sealed record SubscriptionAssumption(
    Guid SubscriptionId, int Consumers, int? QueueCapacity,
    AssumptionLabel Label = AssumptionLabel.Declared, string? MeasurementRef = null);

/// <summary>The share of executions of a branch step that take its <c>then</c> path.</summary>
public sealed record BranchAssumption(
    string StepId, double Probability,
    AssumptionLabel Label = AssumptionLabel.Declared, string? MeasurementRef = null);

/// <summary>
/// Replaces the timeout or the retry policy that the model declares for an operation, or the
/// retry policy of a subscription. A null member keeps the model's value. The result lists
/// every replaced value next to the model's.
/// </summary>
public sealed record PolicyOverride(
    Guid Target, int? TimeoutMs, int? MaxAttempts, int? BackoffMs,
    AssumptionLabel Label = AssumptionLabel.Declared, string? MeasurementRef = null);

/// <summary>A window of virtual time in which an element, an operation, a channel or a subscription is unavailable.</summary>
public sealed record Outage(Guid Target, double FromSeconds, double ToSeconds);

/// <summary>
/// Bounds on one run. A profile is a document that a person or an import wrote, so it is
/// untrusted input: retries multiply work, and the limits keep a run finite and its memory
/// bounded. A run that passes a limit stops with <c>stress-too-large</c> and has no result.
/// Every limit of virtual time and of count is in the result and in its hash
/// (<see cref="EffectiveLimits"/>); <see cref="WallClock"/> is not, because it is real time.
/// </summary>
public sealed record StressLimits
{
    public static StressLimits Default { get; } = new();

    /// <summary>The longest arrival window, in virtual seconds.</summary>
    public double MaxDurationSeconds { get; init; } = 3_600;
    /// <summary>How long the run goes on after the arrivals stop. Work that is left then is reported as not drained.</summary>
    public double MaxDrainSeconds { get; init; } = 3_600;
    public long MaxArrivals { get; init; } = 200_000;
    /// <summary>Scheduled events of every kind, including timers that find nothing to do.</summary>
    public long MaxEvents { get; init; } = 5_000_000;
    /// <summary>Calls, writes and messages that wait at the same instant, over all queues.</summary>
    public int MaxQueued { get; init; } = 250_000;
    /// <summary>
    /// Scheduled events that have not run yet, at the same instant of the run: work in service,
    /// backoffs and timeout timers. With <see cref="MaxQueued"/> it bounds the memory of one run.
    /// </summary>
    public int MaxPendingEvents { get; init; } = 250_000;
    /// <summary>The most samples one latency summary keeps. Above it the summary keeps a uniform reservoir of this size.</summary>
    public int MaxSamples { get; init; } = 50_000;
    /// <summary>The most points of one time series.</summary>
    public int MaxSeriesPoints { get; init; } = 240;
    /// <summary>Resources, service times and subscriptions that one flow uses.</summary>
    public int MaxTargets { get; init; } = 100;
    /// <summary>Real time that one run may take. The host cancels the run when it is spent.</summary>
    public TimeSpan WallClock { get; init; } = TimeSpan.FromSeconds(20);
}
