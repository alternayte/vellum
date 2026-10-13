using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Vellum.Modules.Modelling.Model;
using Vellum.Modules.Trace.Engine;

namespace Vellum.Modules.Stress.Engine;

/// <summary>The one JSON form of a profile and a result: what is stored, hashed and compared.</summary>
public static class StressJson
{
    public static JsonSerializerOptions Options { get; } = Create();

    public static void AddConverters(IList<JsonConverter> converters)
    {
        converters.Add(new JsonStringEnumConverter<AssumptionLabel>(JsonNamingPolicy.CamelCase));
        converters.Add(new JsonStringEnumConverter<ArrivalKind>(JsonNamingPolicy.CamelCase));
        converters.Add(new JsonStringEnumConverter<DistributionKind>(JsonNamingPolicy.CamelCase));
    }

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        AddConverters(options.Converters);
        return options;
    }
}

/// <summary>
/// A discrete-event simulation of one scenario under load. It walks the same steps as the
/// trace engine, but it moves jobs, not payloads: no schema is checked and no assertion is
/// evaluated. Time is virtual and whole nanoseconds. Events run in the order (time, sequence
/// of scheduling) from one binary heap. Random values come from generators that the module
/// owns (<see cref="StressStreams"/>), and the functions that shape them use only IEEE 754
/// operations (<see cref="StressMath"/>). The engine reads no clock and no I/O, so the same
/// model, scenario, profile and seed give the same result and the same hash on every machine.
/// <para>
/// A change to what any input produces is a new <see cref="Version"/>. The version is part of
/// the result and of its hash (SDD guarantee 4).
/// </para>
/// </summary>
public static class StressEngine
{
    public const string Version = "1";

    private const long NsPerMs = 1_000_000;
    private const double NsPerSecond = 1e9;
    /// <summary>One sampled service time is at most a day. A lognormal tail is cut here, so no sum of times leaves 64 bits.</summary>
    private const double MaxSampledMs = 86_400_000;

    /// <summary>
    /// The effects that this engine leaves out, whatever the profile. The list ships with
    /// every result, so a reader of an estimate sees what the estimate does not cover.
    /// </summary>
    public static IReadOnlyList<UnmodelledEffect> AlwaysUnmodelled { get; } =
    [
        new("network", "fixed", "Network latency, jitter and loss between elements."),
        new("runtime", "fixed", "Garbage collection pauses, cold starts and the warm-up of a runtime."),
        new("autoscaling", "fixed", "Autoscaling. The number of workers and consumers does not change during a run."),
        new("locks", "fixed", "Database locks, contention and transaction conflicts."),
        new("pools", "fixed", "Connection pools and thread pools other than the declared workers."),
        new("caches", "fixed", "Caches. Every piece of work takes a time from its distribution."),
        new("payload-size", "fixed", "Payload size. A large message takes the same time as a small one."),
        new("back-pressure", "fixed", "Back-pressure from systems downstream of the flow, and load from other flows on the same elements."),
        new("correlated-failures", "fixed", "Correlated failures. Only the declared outage windows fail work."),
        new("clocks", "fixed", "Clock effects: skew, timer resolution and time-of-day patterns."),
        new("closed-loop", "fixed", "Closed-loop clients. Arrivals do not slow down when the system is slow, and a person does not start a failed flow again."),
        new("shared-capacity", "fixed", "Capacity shared between the consumers of a subscription and the workers of the same element. Each pool is separate."),
        new("retry-jitter", "fixed", "Retry jitter and growing backoff. A retry waits the fixed backoff of its policy."),
        new("dead-letter-consumers", "fixed", "Consumers of a dead-letter channel. A dead-lettered message is counted and then rests."),
    ];

    public const string ConfidenceRule =
        "The level is 'low' when a service time on the path of the flow is declared without a measurement. "
        + "It is 'medium' when every service time on the path is measured. This engine never reports 'high'. "
        + "The level is not a probability.";

    public const string BottleneckRule =
        "A queue grows when its time-weighted mean depth rises from each quarter of the measured window to the next "
        + "and the last quarter is at least 1 above the first. The bottleneck is the resource with the highest "
        + "utilisation among those whose queue grows; the lower ID wins a tie.";

    /// <summary>What a profile for <paramref name="scenario"/> must state. An invalid scenario has issues and no lists.</summary>
    public static StressRequirements Requirements(TraceModel model, Scenario scenario)
    {
        var issues = ScenarioValidator.Validate(model, scenario);
        if (issues.Count > 0)
            return new StressRequirements(issues, [], [], [], [], [], 0);

        var plan = StressPlan.Read(model, scenario);
        var times = new List<RequiredServiceTime>();
        foreach (var id in plan.Operations)
        {
            var operation = model.Operations[id];
            times.Add(new(id, "operation", $"{operation.Name} on {model.Elements[operation.OwnerId]}",
                operation.TimeoutMs, operation.Retry.MaxAttempts, operation.Retry.BackoffMs));
        }
        foreach (var id in plan.Stores)
            times.Add(new(id, "store", $"Write to {model.Elements[id]}", null, null, null));
        foreach (var id in plan.Subscriptions)
            times.Add(new(id, "subscription", $"Handler of {SubscriptionName(model, model.Subscriptions[id])}", null, null, null));

        var targets = new List<OutageTarget>();
        targets.AddRange(plan.Resources(model).Select(id => new OutageTarget(id, "element", model.Elements[id])));
        targets.AddRange(plan.Subscriptions.Select(id => model.Subscriptions[id].ConsumerId.Value).Distinct().Order()
            .Where(id => targets.All(t => t.Id != id)).Select(id => new OutageTarget(id, "element", model.Elements[id])).ToList());
        targets.AddRange(plan.Operations.Select(id => new OutageTarget(id, "operation", model.Operations[id].Name)));
        targets.AddRange(ChannelsOf(model, plan).Select(id => new OutageTarget(id, "channel", model.Channels[id].Name)));
        targets.AddRange(plan.Subscriptions.Select(id => new OutageTarget(id, "subscription", SubscriptionName(model, model.Subscriptions[id]))));

        return new StressRequirements(
            [],
            [.. plan.Resources(model).Select(id => new RequiredResource(id, model.Elements[id]))],
            times,
            [.. plan.Subscriptions.Select(id => model.Subscriptions[id]).Select(s => new RequiredSubscription(
                s.Id, SubscriptionName(model, s), s.Retry.MaxAttempts, s.Retry.BackoffMs, s.DeadLetterChannelId is not null))],
            plan.Branches,
            targets,
            scenario.Faults.Count);
    }

    /// <summary>The published channels, the bound channels on a route and the dead-letter channels of the reached subscriptions.</summary>
    private static SortedSet<Guid> ChannelsOf(TraceModel model, StressPlan plan)
    {
        var channels = new SortedSet<Guid>(plan.Channels);
        foreach (var route in plan.Routes.Values.SelectMany(r => r))
        {
            foreach (var via in route.Via) channels.Add(via);
            if (route.Subscription.DeadLetterChannelId is { } deadLetter && model.Channels.ContainsKey(deadLetter))
                channels.Add(deadLetter);
        }
        return channels;
    }

    private static string SubscriptionName(TraceModel model, SubscriptionContract subscription) =>
        $"{model.Elements.GetValueOrDefault(subscription.ConsumerId, "?")} on {(model.Channels.TryGetValue(subscription.ChannelId, out var channel) ? channel.Name : "?")}";

    public static StressOutcome Run(
        TraceModel model, Scenario scenario, StressProfile profile, StressLimits? limits = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(scenario);
        ArgumentNullException.ThrowIfNull(profile);
        limits ??= StressLimits.Default;

        var issues = new List<StressIssue>(StressProfileRules.Check(profile, limits));
        if (issues.Count > 0)
            return new(issues, null);

        issues.AddRange(ScenarioValidator.Validate(model, scenario)
            .Select(i => new StressIssue("invalid-scenario", i.StepId, $"{i.Code}: {i.Message}")));
        if (issues.Count > 0)
            return new(issues, null);

        var plan = StressPlan.Read(model, scenario);
        CheckComplete(model, plan, profile, limits, issues);
        if (issues.Count > 0)
            return new(issues, null);

        var simulation = new Simulation(model, scenario, plan, profile, limits, ct);
        simulation.Run();
        if (simulation.LimitHit is { } limit)
            return new([TooLarge(limit, limits)], null);

        var result = simulation.Result();
        return new([], result with { Hash = Hash(result) });
    }

    private static StressIssue TooLarge(string limit, StressLimits limits) => new("stress-too-large", limit, limit switch
    {
        "duration" => $"The arrival window is longer than {StressProfileRules.Number(limits.MaxDurationSeconds)} virtual seconds.",
        "arrivals" => $"The run has more than {limits.MaxArrivals} arrivals. Lower the rate or the duration.",
        "events" => $"The run needs more than {limits.MaxEvents} events. Lower the rate or the duration, or lower the retry attempts.",
        "queued" => $"More than {limits.MaxQueued} pieces of work wait at the same instant. Set a queue capacity, or lower the rate.",
        "targets" => $"The flow uses more than {limits.MaxTargets} resources, service times and subscriptions.",
        "routes" => $"A message is matched against more than {TraceLimits.MaxRoutes} bindings.",
        _ => $"The run did not end within {StressProfileRules.Number(limits.WallClock.TotalSeconds)} seconds of real time. Lower the rate or the duration.",
    }, limit);

    /// <summary>
    /// Refuses a run that lacks an input. Nothing is defaulted: a missing service time, worker
    /// count or branch probability is an issue that names what lacks it.
    /// </summary>
    private static void CheckComplete(TraceModel model, StressPlan plan, StressProfile profile, StressLimits limits, List<StressIssue> issues)
    {
        if (plan.TooManyRoutes)
            issues.Add(TooLarge("routes", limits));

        var resources = profile.Resources.Select(r => r.ElementId).ToHashSet();
        var times = profile.ServiceTimes.Select(t => t.Target).ToHashSet();
        var subscriptions = profile.Subscriptions.Select(s => s.SubscriptionId).ToHashSet();
        var branches = profile.Branches.Select(b => b.StepId).ToHashSet(StringComparer.Ordinal);

        foreach (var id in plan.Resources(model).Where(id => !resources.Contains(id)))
            issues.Add(new("missing-concurrency", id.ToString(), $"'{model.Elements[id]}' does work in this flow. Set its workers."));
        foreach (var id in plan.Operations.Where(id => !times.Contains(id)))
            issues.Add(new("missing-service-time", id.ToString(), $"Operation '{model.Operations[id].Name}' has no service time. Set one."));
        foreach (var id in plan.Stores.Where(id => !times.Contains(id)))
            issues.Add(new("missing-service-time", id.ToString(), $"A write to '{model.Elements[id]}' has no service time. Set one."));
        foreach (var id in plan.Subscriptions)
        {
            var name = SubscriptionName(model, model.Subscriptions[id]);
            if (!subscriptions.Contains(id))
                issues.Add(new("missing-consumers", id.ToString(), $"Subscription '{name}' receives messages in this flow. Set its consumers."));
            if (!times.Contains(id))
                issues.Add(new("missing-service-time", id.ToString(), $"The handler of subscription '{name}' has no service time. Set one."));
        }
        foreach (var step in plan.Branches.Where(step => !branches.Contains(step)))
        {
            issues.Add(new("missing-branch-probability", step,
                $"Branch step '{step}' reads the outcome of one playback. Under load it needs the probability of its 'then' path. Set one."));
        }

        foreach (var replaced in profile.Overrides)
        {
            var isOperation = plan.Operations.Contains(replaced.Target);
            if (!isOperation && !plan.Subscriptions.Contains(replaced.Target))
                issues.Add(new("unknown-target", replaced.Target.ToString(), $"The override names {replaced.Target}, which is no operation and no subscription of this flow."));
            else if (!isOperation && replaced.TimeoutMs is not null)
                issues.Add(new("invalid-profile", replaced.Target.ToString(), "A subscription has no timeout. Override its attempts or its backoff."));
        }

        foreach (var outage in profile.Outages)
        {
            Guid target = outage.Target;
            if (!model.Elements.ContainsKey(target) && !model.Operations.ContainsKey(target)
                && !model.Channels.ContainsKey(target) && !model.Subscriptions.ContainsKey(target))
            {
                issues.Add(new("unknown-target", target.ToString(), $"The outage names {target}, which is no element, operation, channel or subscription of the model."));
            }
        }

        if (plan.Resources(model).Count + plan.Operations.Count + plan.Stores.Count + 2 * plan.Subscriptions.Count > limits.MaxTargets)
            issues.Add(TooLarge("targets", limits));
        if (profile.Arrivals.DurationSeconds > limits.MaxDurationSeconds)
            issues.Add(TooLarge("duration", limits));
        if (StressProfileRules.ExpectedArrivals(profile.Arrivals) > limits.MaxArrivals)
            issues.Add(TooLarge("arrivals", limits));
    }

    /// <summary>
    /// SHA-256 over the result in canonical JSON (see <see cref="CanonicalJson"/>), with the
    /// hash member empty. The result holds the engine version, the seed and every assumption,
    /// so the hash covers them.
    /// </summary>
    public static string Hash(StressResult result)
    {
        using var document = JsonSerializer.SerializeToDocument(result with { Hash = "" }, StressJson.Options);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
            CanonicalJson.Write(writer, document.RootElement);
        return Convert.ToHexStringLower(SHA256.HashData(buffer.GetBuffer().AsSpan(0, (int)buffer.Length)));
    }

    private static long ToNs(double ms) => (long)Math.Round(Math.Min(ms, MaxSampledMs) * NsPerMs);
    private static double ToMs(long ns) => ns / (double)NsPerMs;

    /// <summary>A binary min-heap on (time, sequence). The sequence makes the order total, so equal times run in the order they were scheduled.</summary>
    private sealed class EventHeap
    {
        private (long At, long Sequence, Action Work)[] _items = new (long, long, Action)[1024];
        private int _count;
        private long _sequence;

        public void Push(long at, Action work)
        {
            if (_count == _items.Length)
                Array.Resize(ref _items, _count * 2);

            var item = (at, _sequence++, work);
            var i = _count++;
            while (i > 0)
            {
                var parent = (i - 1) / 2;
                if (!Before(item, _items[parent]))
                    break;
                _items[i] = _items[parent];
                i = parent;
            }
            _items[i] = item;
        }

        public bool TryPop(out long at, out Action work)
        {
            if (_count == 0)
            {
                at = 0;
                work = null!;
                return false;
            }

            (at, _, work) = _items[0];
            var last = _items[--_count];
            _items[_count] = default;
            var i = 0;
            while (true)
            {
                var child = 2 * i + 1;
                if (child >= _count)
                    break;
                if (child + 1 < _count && Before(_items[child + 1], _items[child]))
                    child++;
                if (!Before(_items[child], last))
                    break;
                _items[i] = _items[child];
                i = child;
            }
            if (_count > 0)
                _items[i] = last;
            return true;
        }

        private static bool Before((long At, long Sequence, Action Work) a, (long At, long Sequence, Action Work) b) =>
            a.At < b.At || (a.At == b.At && a.Sequence < b.Sequence);
    }

    /// <summary>Draws one service time in nanoseconds.</summary>
    private sealed class Sampler(Distribution time, Xoshiro256 random)
    {
        // ln(median) and the standard deviation of ln(x): the 95th percentile of a normal is 1.6448536269514722 deviations above the mean.
        private readonly double _mu = time.Kind == DistributionKind.Lognormal ? StressMath.Log(time.MedianMs!.Value) : 0;
        private readonly double _sigma = time.Kind == DistributionKind.Lognormal
            ? (StressMath.Log(time.P95Ms!.Value) - StressMath.Log(time.MedianMs!.Value)) / 1.6448536269514722
            : 0;

        public long Next() => time.Kind switch
        {
            DistributionKind.Fixed => ToNs(time.Ms!.Value),
            DistributionKind.Exponential => ToNs(time.MeanMs!.Value * StressMath.Exponential(random)),
            DistributionKind.Lognormal => ToNs(StressMath.Exp(_mu + _sigma * StressMath.Normal(random))),
            _ => ToNs(time.MinMs!.Value + (time.MaxMs!.Value - time.MinMs.Value) * random.NextUnit()),
        };
    }

    /// <summary>The instants at which flows start, one at a time, in nanoseconds.</summary>
    private sealed class ArrivalClock(Arrivals arrivals, long durationNs, Xoshiro256 random)
    {
        private long _at;
        private long _index;

        /// <summary>The next arrival, or -1 when no arrival is left inside the duration.</summary>
        public long Next()
        {
            switch (arrivals.Kind)
            {
                case ArrivalKind.Constant:
                    // Arrival k is at k / rate, computed from k each time: no rounding error adds up.
                    _at = (long)Math.Floor(_index++ * NsPerSecond / arrivals.RatePerSecond);
                    break;

                case ArrivalKind.Poisson:
                    _at += (long)Math.Round(StressMath.Exponential(random) / arrivals.RatePerSecond * NsPerSecond);
                    break;

                case ArrivalKind.Ramp:
                {
                    // The rate is r0 + (r1 - r0) t / D. Its integral from 0 to t is b t + a t², with
                    // b = r0 and a = (r1 - r0) / 2D. The next arrival is where the integral has grown
                    // by an exponential value of mean 1: the smaller root of a t² + b t - target = 0,
                    // in the form that stays exact when a is 0 or small.
                    var seconds = _at / NsPerSecond;
                    var a = (arrivals.ToRatePerSecond!.Value - arrivals.RatePerSecond) / (2 * arrivals.DurationSeconds);
                    var b = arrivals.RatePerSecond;
                    var target = b * seconds + a * seconds * seconds + StressMath.Exponential(random);
                    var discriminant = b * b + 4 * a * target;
                    if (discriminant < 0)
                        return -1;
                    var denominator = b + Math.Sqrt(discriminant);
                    if (denominator <= 0)
                        return -1;
                    _at = Math.Max(_at, (long)Math.Round(2 * target / denominator * NsPerSecond));
                    break;
                }

                default:
                {
                    // The rate is constant on each piece. An exponential value of mean 1 is spent
                    // piece by piece until the rest fits into one piece.
                    var seconds = _at / NsPerSecond;
                    var rest = StressMath.Exponential(random);
                    while (true)
                    {
                        var (rate, end) = Piece(seconds);
                        if (rate > 0 && rate * (end - seconds) >= rest)
                        {
                            seconds += rest / rate;
                            break;
                        }
                        if (end >= arrivals.DurationSeconds)
                            return -1;
                        rest -= rate * (end - seconds);
                        seconds = end;
                    }
                    _at = Math.Max(_at, (long)Math.Round(seconds * NsPerSecond));
                    break;
                }
            }

            return _at < durationNs ? _at : -1;
        }

        private (double Rate, double End) Piece(double seconds)
        {
            foreach (var window in arrivals.Bursts!)
            {
                if (seconds < window.FromSeconds) return (arrivals.RatePerSecond, window.FromSeconds);
                if (seconds < window.ToSeconds) return (window.RatePerSecond, window.ToSeconds);
            }
            return (arrivals.RatePerSecond, arrivals.DurationSeconds);
        }
    }

    /// <summary>
    /// The samples of one latency. Count, sum, minimum and maximum are exact. Up to the limit
    /// every sample is kept. Above it the kept samples are a uniform reservoir (Vitter's
    /// algorithm R) drawn from a generator of its own, so it is the same in every run.
    /// </summary>
    private sealed class Samples(int limit, Xoshiro256 random)
    {
        private readonly List<long> _kept = [];
        private long _count;
        private Int128 _sum;
        private long _min = long.MaxValue;
        private long _max;

        public void Add(long ns)
        {
            _count++;
            _sum += ns;
            if (ns < _min) _min = ns;
            if (ns > _max) _max = ns;

            if (_kept.Count < limit)
            {
                _kept.Add(ns);
                return;
            }
            var slot = random.NextBelow(_count);
            if (slot < limit)
                _kept[(int)slot] = ns;
        }

        public LatencySummary Summary()
        {
            if (_count == 0)
                return new(StressResult.Estimated, 0, 0, false, null, null, null, null, null, null, null);

            var sorted = _kept.ToArray();
            Array.Sort(sorted);
            // Nearest rank: the smallest value with at least p percent of the samples at or below it.
            double Rank(int percent) => ToMs(sorted[(int)(((long)percent * sorted.Length + 99) / 100) - 1]);
            return new(StressResult.Estimated, _count, sorted.Length, _count > sorted.Length,
                ToMs(_min), (double)_sum / _count / NsPerMs, Rank(50), Rank(90), Rank(95), Rank(99), ToMs(_max));
        }
    }

    private sealed class Flow(long at, bool counted)
    {
        public long At => at;
        public bool Counted => counted;
    }

    /// <summary>One piece of work that asks a pool for a worker. <paramref name="begin"/> runs when it has one.</summary>
    private sealed class Job(long at, bool counted, Action begin)
    {
        public long At => at;
        public bool Counted => counted;
        public Action Begin => begin;
    }

    /// <summary>Workers with a first-in-first-out queue: an element, or the consumers of a subscription.</summary>
    private sealed class Pool(Guid id, string name, string kind, int workers, int? capacity, Samples wait, Samples latency)
    {
        private long _last;

        public Guid Id => id;
        public string Name => name;
        public string Kind => kind;
        public int Workers => workers;
        public int? Capacity => capacity;
        public Samples Wait => wait;
        public Samples Latency => latency;

        public int Busy;
        public readonly Queue<Job> Waiting = new();
        public long Arrived, Waited, Completed, Failed, Rejected, Dropped, DeadLettered, Retries, Overflowed, WastedCount, WastedNs, CompletedInWindow;
        public int MaxDepth;
        public int DepthAtEnd;
        public long BusyNs;
        public readonly long[] DepthNs = new long[4];
        public readonly List<int> DepthSeries = [];
        public readonly List<int> BusySeries = [];

        /// <summary>Adds the time since the last change to the integrals of the measured window, by quarter.</summary>
        public void Advance(long now, long[] quarters)
        {
            for (var q = 0; q < 4; q++)
            {
                var overlap = Math.Min(now, quarters[q + 1]) - Math.Max(_last, quarters[q]);
                if (overlap <= 0)
                    continue;
                BusyNs += Busy * overlap;
                DepthNs[q] += Waiting.Count * overlap;
            }
            _last = now;
        }
    }

    private sealed class OperationRun(OperationContract contract, Pool pool, Sampler time, int timeoutMs, RetryPolicy retry, Samples latency)
    {
        public OperationContract Contract => contract;
        public Pool Pool => pool;
        public Sampler Time => time;
        public int TimeoutMs => timeoutMs;
        public RetryPolicy Retry => retry;
        public Samples Latency => latency;
        public long Attempts, Completed, Failed, Rejected, TimedOut, Retries, Exhausted, Wasted, CompletedInWindow;
    }

    private sealed class SubscriptionRun(SubscriptionContract contract, Pool pool, Sampler time, RetryPolicy retry)
    {
        public SubscriptionContract Contract => contract;
        public Pool Pool => pool;
        public Sampler Time => time;
        public RetryPolicy Retry => retry;
    }

    private sealed class ChannelRun
    {
        public long Published, PublishFailed;
    }

    private enum Failure { Unavailable, Rejected, TimedOut, Inside }

    private sealed class Simulation
    {
        private readonly TraceModel _model;
        private readonly Scenario _scenario;
        private readonly StressPlan _plan;
        private readonly StressProfile _profile;
        private readonly StressLimits _limits;
        private readonly CancellationToken _ct;

        private readonly EventHeap _heap = new();
        private readonly long _warmUp, _duration, _horizon, _interval;
        private readonly long[] _quarters;
        private readonly ArrivalClock _arrivalClock;

        private readonly SortedDictionary<Guid, Pool> _elements = [];
        private readonly SortedDictionary<Guid, Pool> _consumers = [];
        private readonly SortedDictionary<Guid, OperationRun> _operations = [];
        private readonly SortedDictionary<Guid, SubscriptionRun> _subscriptions = [];
        private readonly SortedDictionary<Guid, ChannelRun> _channels = [];
        private readonly Dictionary<Guid, Sampler> _writeTimes = [];
        private readonly Dictionary<string, (double Probability, Xoshiro256 Random)> _branches = new(StringComparer.Ordinal);
        private readonly Dictionary<Guid, List<(long From, long To)>> _outages = [];
        private readonly List<Pool> _pools = [];
        private readonly Samples _flowLatency;

        private long _now, _events, _open, _lastIdleAt, _queued;
        private long _arrivals, _counted, _completed, _failed, _completedInWindow;
        private int _inFlight;
        private long _intervalArrivals, _intervalCompleted, _intervalFailed;
        private readonly List<double> _arrivalSeries = [], _throughputSeries = [], _failureSeries = [];
        private readonly List<int> _inFlightSeries = [];
        private bool _truncated;

        public string? LimitHit { get; private set; }

        public Simulation(TraceModel model, Scenario scenario, StressPlan plan, StressProfile profile, StressLimits limits, CancellationToken ct)
        {
            (_model, _scenario, _plan, _profile, _limits, _ct) = (model, scenario, plan, profile, limits, ct);

            StressProfileRules.TryParseSeed(profile.Seed, out var seed);
            var streams = new StressStreams(seed);
            Samples NewSamples(string purpose, Guid id) => new(limits.MaxSamples, streams.For(purpose, id));

            _warmUp = (long)Math.Round(profile.Arrivals.WarmUpSeconds * NsPerSecond);
            _duration = (long)Math.Round(profile.Arrivals.DurationSeconds * NsPerSecond);
            _horizon = _duration + (long)Math.Round(limits.MaxDrainSeconds * NsPerSecond);
            var window = _duration - _warmUp;
            _quarters = [_warmUp, _warmUp + window / 4, _warmUp + window / 2, _warmUp + window * 3 / 4, _duration];

            // Half of the points cover the arrivals; the rest is for the drain. Whole milliseconds.
            var points = Math.Max(2, limits.MaxSeriesPoints / 2);
            _interval = Math.Max(1, (_duration + points * NsPerMs - 1) / (points * NsPerMs)) * NsPerMs;

            _arrivalClock = new ArrivalClock(profile.Arrivals, _duration, streams.For("arrivals", Guid.Empty));
            _flowLatency = NewSamples("reservoir-flow", Guid.Empty);

            var times = profile.ServiceTimes.ToDictionary(t => t.Target);
            var overrides = profile.Overrides.ToDictionary(o => o.Target);
            RetryPolicy Retry(Guid target, RetryPolicy declared) => overrides.TryGetValue(target, out var replaced)
                ? new RetryPolicy(replaced.MaxAttempts ?? declared.MaxAttempts, replaced.BackoffMs ?? declared.BackoffMs)
                : declared;

            var working = plan.Resources(model);
            foreach (var resource in profile.Resources.Where(r => working.Contains(r.ElementId)))
            {
                _elements[resource.ElementId] = new Pool(resource.ElementId, model.Elements[resource.ElementId], "element",
                    resource.Concurrency, resource.QueueCapacity,
                    NewSamples("reservoir-wait", resource.ElementId), NewSamples("reservoir-latency", resource.ElementId));
            }

            foreach (var id in plan.Operations)
            {
                var contract = model.Operations[id];
                _operations[id] = new OperationRun(contract, _elements[contract.OwnerId],
                    new Sampler(times[id].Time, streams.For("service", id)),
                    overrides.TryGetValue(id, out var replaced) ? replaced.TimeoutMs ?? contract.TimeoutMs : contract.TimeoutMs,
                    Retry(id, contract.Retry), NewSamples("reservoir-call", id));
            }

            foreach (var id in plan.Stores)
                _writeTimes[id] = new Sampler(times[id].Time, streams.For("service", id));

            var consumers = profile.Subscriptions.ToDictionary(s => s.SubscriptionId);
            foreach (var id in plan.Subscriptions)
            {
                var contract = model.Subscriptions[id];
                var pool = new Pool(id, SubscriptionName(model, contract), "subscription",
                    consumers[id].Consumers, consumers[id].QueueCapacity,
                    NewSamples("reservoir-wait", id), NewSamples("reservoir-latency", id));
                _consumers[id] = pool;
                _subscriptions[id] = new SubscriptionRun(contract, pool, new Sampler(times[id].Time, streams.For("service", id)), Retry(id, contract.Retry));
            }

            foreach (var id in ChannelsOf(model, plan))
                _channels[id] = new ChannelRun();

            foreach (var branch in profile.Branches.Where(b => plan.Branches.Contains(b.StepId)))
                _branches[branch.StepId] = (branch.Probability, streams.For("branch", branch.StepId));

            foreach (var outage in profile.Outages)
            {
                if (!_outages.TryGetValue(outage.Target, out var windows))
                    _outages[outage.Target] = windows = [];
                windows.Add(((long)Math.Round(outage.FromSeconds * NsPerSecond), (long)Math.Round(outage.ToSeconds * NsPerSecond)));
            }

            _pools.AddRange(_elements.Values);
            _pools.AddRange(_consumers.Values);
        }

        public void Run()
        {
            // Scheduled first, so it runs before any other event of that instant.
            _heap.Push(_duration, () =>
            {
                foreach (var pool in _pools)
                    pool.DepthAtEnd = pool.Waiting.Count;
            });
            _heap.Push(_interval, Sample);
            NextArrival();

            while (_heap.TryPop(out var at, out var work))
            {
                // Work that is left at the drain limit stays in the system and is reported.
                if (at > _horizon)
                    break;
                if (++_events > _limits.MaxEvents)
                {
                    LimitHit = "events";
                    return;
                }
                if ((_events & 0xFFF) == 0 && _ct.IsCancellationRequested)
                {
                    LimitHit = "wall-clock";
                    return;
                }

                _now = at;
                work();
                if (LimitHit is not null)
                    return;
            }

            foreach (var pool in _pools)
                pool.Advance(Math.Max(_now, _duration), _quarters);
        }

        private void After(long delay, Action work) => _heap.Push(_now + delay, work);

        private bool InWindow => _now >= _warmUp && _now < _duration;

        private void Open() => _open++;

        private void Close()
        {
            if (--_open == 0)
                _lastIdleAt = _now;
        }

        private bool Down(Guid target)
        {
            if (!_outages.TryGetValue(target, out var windows))
                return false;
            foreach (var (from, to) in windows)
            {
                if (_now >= from && _now < to)
                    return true;
            }
            return false;
        }

        private void NextArrival()
        {
            var at = _arrivalClock.Next();
            if (at >= 0)
                _heap.Push(at, Arrive);
        }

        private void Arrive()
        {
            if (++_arrivals > _limits.MaxArrivals)
            {
                LimitHit = "arrivals";
                return;
            }

            var flow = new Flow(_now, _now >= _warmUp);
            if (flow.Counted) _counted++;
            _intervalArrivals++;
            _inFlight++;
            Open();
            Sequence(_scenario.Steps, 0, flow, ok =>
            {
                _inFlight--;
                if (ok)
                {
                    _intervalCompleted++;
                    if (InWindow) _completedInWindow++;
                    if (flow.Counted)
                    {
                        _completed++;
                        _flowLatency.Add(_now - flow.At);
                    }
                }
                else
                {
                    _intervalFailed++;
                    if (flow.Counted) _failed++;
                }
                Close();
            });
            NextArrival();
        }

        private void Sample()
        {
            var seconds = _interval / NsPerSecond;
            _arrivalSeries.Add(_intervalArrivals / seconds);
            _throughputSeries.Add(_intervalCompleted / seconds);
            _failureSeries.Add(_intervalFailed / seconds);
            _inFlightSeries.Add(_inFlight);
            _intervalArrivals = _intervalCompleted = _intervalFailed = 0;
            foreach (var pool in _pools)
            {
                pool.DepthSeries.Add(pool.Waiting.Count);
                pool.BusySeries.Add(pool.Busy);
            }

            if (_now < _duration || _open > 0)
            {
                if (_inFlightSeries.Count < _limits.MaxSeriesPoints)
                    After(_interval, Sample);
                else
                    _truncated = true;
            }
        }

        /// <summary>Asks the pool for a worker. False when no worker is free and the queue is full.</summary>
        private bool Submit(Pool pool, Job job)
        {
            pool.Advance(_now, _quarters);
            if (job.Counted) pool.Arrived++;

            if (pool.Busy < pool.Workers)
            {
                pool.Busy++;
                if (job.Counted) pool.Wait.Add(0);
                Open();
                job.Begin();
                return true;
            }

            if (pool.Capacity is { } capacity && pool.Waiting.Count >= capacity)
                return false;

            if (++_queued > _limits.MaxQueued)
            {
                LimitHit = "queued";
                return false;
            }
            pool.Waiting.Enqueue(job);
            if (job.Counted) pool.Waited++;
            Open();
            if (InWindow && pool.Waiting.Count > pool.MaxDepth)
                pool.MaxDepth = pool.Waiting.Count;
            return true;
        }

        /// <summary>
        /// Frees the worker of a job. The first waiting job takes the worker at this instant;
        /// its work is scheduled, not called, so a long queue does not grow the stack.
        /// </summary>
        private void Release(Pool pool)
        {
            pool.Advance(_now, _quarters);
            pool.Busy--;
            Close();

            if (pool.Waiting.TryDequeue(out var next))
            {
                _queued--;
                pool.Busy++;
                if (next.Counted) pool.Wait.Add(_now - next.At);
                After(0, next.Begin);
            }
        }

        /// <summary>Runs steps in order. A failure that nothing handles ends the sequence.</summary>
        private void Sequence(IReadOnlyList<Step> steps, int index, Flow flow, Action<bool> done)
        {
            if (index == steps.Count)
            {
                done(true);
                return;
            }

            Start(steps[index], flow, ok =>
            {
                if (ok)
                    After(0, () => Sequence(steps, index + 1, flow, done));
                else
                    done(false);
            });
        }

        private void Start(Step step, Flow flow, Action<bool> done)
        {
            switch (step)
            {
                case CallStep call: Call(call, 1, flow, done); break;
                case PublishStep publish: Publish(publish, flow, done); break;
                case WriteStep write: Write(write, flow, done); break;
                case ParallelStep parallel: Parallel(parallel, flow, done); break;
                case BranchStep branch: Branch(branch, flow, done); break;
                default: throw new InvalidOperationException($"Unsupported step {step.GetType().Name}.");
            }
        }

        /// <summary>
        /// One attempt of a call. The caller starts its timeout, then asks the callee for a
        /// worker. The worker is held for the service time and then for the steps inside the
        /// callee. A caller that gave up does not stop the callee: the work goes on and is
        /// counted as wasted when it ends.
        /// </summary>
        private void Call(CallStep step, int attempt, Flow flow, Action<bool> done)
        {
            var operation = _operations[step.Operation];
            var pool = operation.Pool;
            var settled = false;
            var issuedAt = _now;
            if (flow.Counted) operation.Attempts++;

            if (Down(operation.Contract.Id) || Down(pool.Id))
            {
                Fail(Failure.Unavailable);
                return;
            }

            // Queued before the callee's work, so a response at the deadline instant is late.
            if (operation.TimeoutMs > 0)
                After(operation.TimeoutMs * NsPerMs, () => Fail(Failure.TimedOut));

            var job = new Job(_now, flow.Counted, () =>
            {
                var startedAt = _now;
                // An outage that began while the call waited fails it when it reaches a worker.
                if (Down(operation.Contract.Id) || Down(pool.Id))
                {
                    Leave();
                    Fail(Failure.Unavailable);
                    return;
                }

                After(operation.Time.Next(), () => Sequence(step.Then, 0, flow, ok =>
                {
                    Leave();
                    if (flow.Counted) pool.Completed++;
                    if (InWindow) pool.CompletedInWindow++;

                    if (settled)
                    {
                        if (flow.Counted)
                        {
                            operation.Wasted++;
                            pool.WastedCount++;
                            pool.WastedNs += _now - startedAt;
                        }
                        return;
                    }
                    if (!ok)
                    {
                        Fail(Failure.Inside);
                        return;
                    }

                    settled = true;
                    if (InWindow) operation.CompletedInWindow++;
                    if (flow.Counted)
                    {
                        operation.Completed++;
                        operation.Latency.Add(_now - issuedAt);
                    }
                    done(true);
                }));
            });

            if (!Submit(pool, job))
            {
                if (flow.Counted) pool.Rejected++;
                Fail(Failure.Rejected);
            }

            void Leave()
            {
                if (flow.Counted) pool.Latency.Add(_now - issuedAt);
                Release(pool);
            }

            void Fail(Failure failure)
            {
                if (settled)
                    return;
                settled = true;

                if (flow.Counted)
                {
                    switch (failure)
                    {
                        case Failure.TimedOut: operation.TimedOut++; break;
                        case Failure.Rejected: operation.Rejected++; break;
                        default: operation.Failed++; break;
                    }
                    if (failure == Failure.Unavailable) pool.Failed++;
                }

                if (attempt < operation.Retry.MaxAttempts)
                {
                    if (flow.Counted) operation.Retries++;
                    After(operation.Retry.BackoffMs * NsPerMs, () => Call(step, attempt + 1, flow, done));
                    return;
                }

                if (flow.Counted) operation.Exhausted++;
                if (step.OnError.Count > 0)
                    Sequence(step.OnError, 0, flow, done);
                else
                    done(false);
            }
        }

        /// <summary>
        /// Hands one message to every subscription the step reaches. The publisher does not
        /// wait. An outage of the channel fails the step; an outage of a bound channel closes
        /// that route only, and the subscriptions behind it lose the message.
        /// </summary>
        private void Publish(PublishStep step, Flow flow, Action<bool> done)
        {
            var channel = _channels[step.Channel];
            if (Down(step.Channel))
            {
                if (flow.Counted) channel.PublishFailed++;
                done(false);
                return;
            }

            if (flow.Counted) channel.Published++;
            var publishedAt = _now;
            foreach (var route in _plan.Routes[step])
            {
                var subscription = _subscriptions[route.Subscription.Id];
                var closed = false;
                foreach (var via in route.Via)
                    closed |= Down(via);
                if (closed)
                {
                    if (flow.Counted) subscription.Pool.Dropped++;
                    continue;
                }

                Open();
                After(0, () => Deliver(subscription, route.Handler, 1, flow, publishedAt));
            }

            done(true);
        }

        /// <summary>
        /// One delivery attempt. The message waits for a consumer, which holds it for the
        /// service time of the handler and then for the handler's steps. A failed attempt
        /// waits the backoff and joins the back of the queue. After the last attempt, and when
        /// the queue is full, the message is dead-lettered, or dropped when the subscription
        /// declares no dead-letter channel or that channel is down.
        /// </summary>
        private void Deliver(SubscriptionRun subscription, IReadOnlyList<Step> handler, int attempt, Flow flow, long publishedAt)
        {
            var pool = subscription.Pool;
            var job = new Job(_now, flow.Counted, () =>
            {
                if (Down(subscription.Contract.Id) || Down(subscription.Contract.ConsumerId))
                {
                    Release(pool);
                    Failed();
                    return;
                }

                After(subscription.Time.Next(), () => Sequence(handler, 0, flow, ok =>
                {
                    Release(pool);
                    if (!ok)
                    {
                        Failed();
                        return;
                    }
                    if (InWindow) pool.CompletedInWindow++;
                    if (flow.Counted)
                    {
                        pool.Completed++;
                        pool.Latency.Add(_now - publishedAt);
                    }
                    Close();
                }));
            });

            if (!Submit(pool, job))
            {
                if (flow.Counted) pool.Overflowed++;
                Lose();
            }

            void Failed()
            {
                if (flow.Counted) pool.Failed++;
                if (attempt < subscription.Retry.MaxAttempts)
                {
                    if (flow.Counted) pool.Retries++;
                    After(subscription.Retry.BackoffMs * NsPerMs, () => Deliver(subscription, handler, attempt + 1, flow, publishedAt));
                    return;
                }
                Lose();
            }

            void Lose()
            {
                if (flow.Counted)
                {
                    if (subscription.Contract.DeadLetterChannelId is { } deadLetter && !Down(deadLetter))
                        pool.DeadLettered++;
                    else
                        pool.Dropped++;
                }
                Close();
            }
        }

        /// <summary>A write holds a worker of the store for its service time. It has no timeout and no retry: the model declares none.</summary>
        private void Write(WriteStep step, Flow flow, Action<bool> done)
        {
            var pool = _elements[step.Store];
            if (Down(pool.Id))
            {
                if (flow.Counted) pool.Failed++;
                done(false);
                return;
            }

            var issuedAt = _now;
            var job = new Job(_now, flow.Counted, () =>
            {
                if (Down(pool.Id))
                {
                    Leave();
                    if (flow.Counted) pool.Failed++;
                    done(false);
                    return;
                }

                After(_writeTimes[step.Store].Next(), () =>
                {
                    Leave();
                    if (InWindow) pool.CompletedInWindow++;
                    if (flow.Counted) pool.Completed++;
                    done(true);
                });
            });

            if (!Submit(pool, job))
            {
                if (flow.Counted) pool.Rejected++;
                done(false);
            }

            void Leave()
            {
                if (flow.Counted) pool.Latency.Add(_now - issuedAt);
                Release(pool);
            }
        }

        /// <summary>Branches start at the same instant; the step ends when the slowest one does.</summary>
        private void Parallel(ParallelStep step, Flow flow, Action<bool> done)
        {
            var remaining = step.Branches.Count;
            var ok = true;
            if (remaining == 0)
            {
                done(true);
                return;
            }

            foreach (var branch in step.Branches)
            {
                After(0, () => Sequence(branch, 0, flow, branchOk =>
                {
                    ok &= branchOk;
                    if (--remaining == 0)
                        done(ok);
                }));
            }
        }

        /// <summary>Takes the <c>then</c> path with the declared probability, from the step's own generator.</summary>
        private void Branch(BranchStep step, Flow flow, Action<bool> done)
        {
            var (probability, random) = _branches[step.Id];
            Sequence(random.NextUnit() < probability ? step.Then : step.Else, 0, flow, done);
        }

        public StressResult Result()
        {
            var windowSeconds = (_duration - _warmUp) / NsPerSecond;
            var ownedTimeouts = _operations.Values.ToLookup(o => o.Pool.Id, o => o.TimedOut);

            ResourceStats Stats(Pool pool)
            {
                var quarters = new double[4];
                long total = 0;
                for (var q = 0; q < 4; q++)
                {
                    var length = _quarters[q + 1] - _quarters[q];
                    quarters[q] = length > 0 ? pool.DepthNs[q] / (double)length : 0;
                    total += pool.DepthNs[q];
                }

                return new ResourceStats(
                    StressResult.Estimated, pool.Id, pool.Name, pool.Kind, pool.Workers, pool.Capacity,
                    pool.Arrived, pool.Waited,
                    new Counts(pool.Completed, pool.Failed, pool.Rejected, ownedTimeouts[pool.Id].Sum(), pool.Dropped, pool.DeadLettered),
                    pool.Retries, pool.Overflowed, pool.WastedCount, ToMs(pool.WastedNs),
                    pool.BusyNs / ((double)pool.Workers * (_duration - _warmUp)),
                    pool.CompletedInWindow / windowSeconds,
                    new QueueStats(total / (double)(_duration - _warmUp), quarters, pool.MaxDepth, pool.DepthAtEnd),
                    pool.Wait.Summary(), pool.Latency.Summary());
            }

            var elements = _elements.Values.Select(Stats).ToList();
            var subscriptions = _consumers.Values.Select(Stats).ToList();
            var operations = _operations.Values.Select(o => new OperationStats(
                StressResult.Estimated, o.Contract.Id, o.Contract.Name, o.Contract.OwnerId,
                o.TimeoutMs, o.Retry.MaxAttempts, o.Retry.BackoffMs,
                o.Attempts, new Counts(o.Completed, o.Failed, o.Rejected, o.TimedOut, 0, 0),
                o.Retries, o.Exhausted, o.Wasted, o.CompletedInWindow / windowSeconds, o.Latency.Summary())).ToList();

            var flow = new FlowStats(
                StressResult.Estimated, _counted,
                new Counts(_completed, _failed,
                    elements.Sum(e => e.Counts.Rejected), operations.Sum(o => o.Counts.TimedOut),
                    subscriptions.Sum(s => s.Counts.Dropped), subscriptions.Sum(s => s.Counts.DeadLettered)),
                _completedInWindow / windowSeconds, _flowLatency.Summary());

            var series = new StressSeries(
                StressResult.Estimated, ToMs(_interval), _inFlightSeries.Count, _truncated,
                _arrivalSeries, _throughputSeries, _failureSeries, _inFlightSeries,
                [.. _pools.Select(p => new ResourceSeries(p.Id, p.Name, p.Kind, p.DepthSeries, p.BusySeries))]);

            var drained = _open == 0;
            return new StressResult(
                Version, StressResult.Estimated, _profile.Seed,
                new StressWindow(ToMs(_warmUp), ToMs(_duration), ToMs(_now)),
                flow, elements, operations, subscriptions,
                [.. _channels.Select(c => new ChannelStats(StressResult.Estimated, c.Key, _model.Channels[c.Key].Name, c.Value.Published, c.Value.PublishFailed))],
                series,
                new DrainStats(StressResult.Estimated, drained, drained ? ToMs(Math.Max(0, _lastIdleAt - _duration)) : null, _open),
                FindBottleneck([.. elements, .. subscriptions], windowSeconds),
                Assumptions(), Overrides(), Unmodelled(), ConfidenceOf(),
                new StressMethods(
                    "Sorted nearest rank: the value at rank ceil(p / 100 * n) of the n kept samples in ascending order.",
                    "Counts and latencies cover the flows that start at or after the warm-up and all the work they cause. "
                    + "Throughput, utilisation and queue depth cover the time from the warm-up to the end of the arrivals.",
                    $"A summary keeps every sample up to {_limits.MaxSamples}. Above that it keeps a uniform reservoir of {_limits.MaxSamples}; "
                    + "count, minimum, mean and maximum stay exact.",
                    "Virtual time in whole nanoseconds. Events run in the order (time, sequence of scheduling)."),
                "");
        }

        private static Bottleneck FindBottleneck(IReadOnlyList<ResourceStats> resources, double windowSeconds)
        {
            ResourceStats? found = null;
            foreach (var resource in resources)
            {
                var q = resource.Queue.MeanDepthByQuarter;
                var grows = q[0] < q[1] && q[1] < q[2] && q[2] < q[3] && q[3] - q[0] >= 1;
                if (grows && (found is null || resource.Utilisation > found.Utilisation
                        || (resource.Utilisation == found.Utilisation && resource.Id.CompareTo(found.Id) < 0)))
                    found = resource;
            }

            if (found is null)
                return new(StressResult.Estimated, false, null, null, null, null, null, null, null, BottleneckRule);

            var quarters = found.Queue.MeanDepthByQuarter;
            // The centres of the first and the last quarter are three quarters of the window apart.
            return new(StressResult.Estimated, true, found.Id, found.Name, found.Kind, found.Utilisation, quarters,
                found.Queue.DepthAtEndOfArrivals, (quarters[3] - quarters[0]) / (0.75 * windowSeconds), BottleneckRule);
        }

        /// <summary>Every input the run used, in a fixed order. An input for something the flow does not use is not here.</summary>
        private List<AssumptionRow> Assumptions()
        {
            var rows = new List<AssumptionRow>();
            var arrivals = _profile.Arrivals;
            void Arrival(string field, string value, string unit) =>
                rows.Add(new("arrivals", null, "Arrivals", field, value, unit, arrivals.Label, arrivals.MeasurementRef));
            static string N(double value) => StressProfileRules.Number(value);

            Arrival("kind", arrivals.Kind.ToString().ToLowerInvariant(), "");
            Arrival("ratePerSecond", N(arrivals.RatePerSecond), "per second");
            if (arrivals.Kind == ArrivalKind.Ramp)
                Arrival("toRatePerSecond", N(arrivals.ToRatePerSecond!.Value), "per second");
            if (arrivals.Kind == ArrivalKind.Burst)
            {
                for (var i = 0; i < arrivals.Bursts!.Count; i++)
                {
                    var window = arrivals.Bursts[i];
                    Arrival($"bursts[{i}]", $"{N(window.RatePerSecond)} per second from {N(window.FromSeconds)} s to {N(window.ToSeconds)} s", "");
                }
            }
            Arrival("durationSeconds", N(arrivals.DurationSeconds), "s");
            Arrival("warmUpSeconds", N(arrivals.WarmUpSeconds), "s");
            rows.Add(new("seed", null, "Seed", "seed", _profile.Seed, "", AssumptionLabel.Declared, null));

            foreach (var resource in _profile.Resources.Where(r => _elements.ContainsKey(r.ElementId)).OrderBy(r => r.ElementId))
            {
                var name = _model.Elements[resource.ElementId];
                rows.Add(new("resource", resource.ElementId, name, "concurrency", resource.Concurrency.ToString(), "workers", resource.Label, resource.MeasurementRef));
                rows.Add(new("resource", resource.ElementId, name, "queueCapacity", resource.QueueCapacity?.ToString() ?? "unbounded", "waiting", resource.Label, resource.MeasurementRef));
            }

            foreach (var time in _profile.ServiceTimes.OrderBy(t => t.Target))
            {
                string name;
                if (_operations.TryGetValue(time.Target, out var operation)) name = operation.Contract.Name;
                else if (_subscriptions.TryGetValue(time.Target, out var subscription)) name = $"Handler of {subscription.Pool.Name}";
                else if (_writeTimes.ContainsKey(time.Target)) name = $"Write to {_model.Elements[time.Target]}";
                else continue;

                void Time(string field, string value, string unit) =>
                    rows.Add(new("service-time", time.Target, name, field, value, unit, time.Label, time.MeasurementRef));
                Time("kind", time.Time.Kind.ToString().ToLowerInvariant(), "");
                switch (time.Time.Kind)
                {
                    case DistributionKind.Fixed: Time("ms", N(time.Time.Ms!.Value), "ms"); break;
                    case DistributionKind.Exponential: Time("meanMs", N(time.Time.MeanMs!.Value), "ms"); break;
                    case DistributionKind.Lognormal:
                        Time("medianMs", N(time.Time.MedianMs!.Value), "ms");
                        Time("p95Ms", N(time.Time.P95Ms!.Value), "ms");
                        break;
                    default:
                        Time("minMs", N(time.Time.MinMs!.Value), "ms");
                        Time("maxMs", N(time.Time.MaxMs!.Value), "ms");
                        break;
                }
            }

            foreach (var subscription in _profile.Subscriptions.Where(s => _subscriptions.ContainsKey(s.SubscriptionId)).OrderBy(s => s.SubscriptionId))
            {
                var name = _consumers[subscription.SubscriptionId].Name;
                rows.Add(new("subscription", subscription.SubscriptionId, name, "consumers", subscription.Consumers.ToString(), "consumers", subscription.Label, subscription.MeasurementRef));
                rows.Add(new("subscription", subscription.SubscriptionId, name, "queueCapacity", subscription.QueueCapacity?.ToString() ?? "unbounded", "waiting", subscription.Label, subscription.MeasurementRef));
            }

            foreach (var branch in _profile.Branches.Where(b => _branches.ContainsKey(b.StepId)).OrderBy(b => b.StepId, StringComparer.Ordinal))
                rows.Add(new("branch", null, branch.StepId, "probability", N(branch.Probability), "of 1", branch.Label, branch.MeasurementRef));

            foreach (var outage in _profile.Outages.OrderBy(o => o.Target).ThenBy(o => o.FromSeconds))
                rows.Add(new("outage", outage.Target, NameOf(outage.Target), "window", $"from {N(outage.FromSeconds)} s to {N(outage.ToSeconds)} s", "", AssumptionLabel.Declared, null));

            // The model declares these. They are inputs of the run like the rest.
            foreach (var operation in _operations.Values)
            {
                void Policy(string field, int value, string unit) =>
                    rows.Add(new("model-policy", operation.Contract.Id, operation.Contract.Name, field, value.ToString(), unit, AssumptionLabel.Declared, null));
                Policy("timeoutMs", operation.Contract.TimeoutMs, "ms");
                Policy("maxAttempts", operation.Contract.Retry.MaxAttempts, "attempts");
                Policy("backoffMs", operation.Contract.Retry.BackoffMs, "ms");
            }
            foreach (var subscription in _subscriptions.Values)
            {
                void Policy(string field, int value, string unit) =>
                    rows.Add(new("model-policy", subscription.Contract.Id, subscription.Pool.Name, field, value.ToString(), unit, AssumptionLabel.Declared, null));
                Policy("maxAttempts", subscription.Contract.Retry.MaxAttempts, "attempts");
                Policy("backoffMs", subscription.Contract.Retry.BackoffMs, "ms");
            }

            return rows;
        }

        private string NameOf(Guid id) =>
            _model.Elements.TryGetValue(id, out var element) ? element
            : _model.Operations.TryGetValue(id, out var operation) ? operation.Name
            : _model.Channels.TryGetValue(id, out var channel) ? channel.Name
            : _model.Subscriptions.TryGetValue(id, out var subscription) ? SubscriptionName(_model, subscription)
            : id.ToString();

        private List<OverrideRow> Overrides()
        {
            var rows = new List<OverrideRow>();
            foreach (var replaced in _profile.Overrides.OrderBy(o => o.Target))
            {
                var (name, timeout, retry) = _operations.TryGetValue(replaced.Target, out var operation)
                    ? (operation.Contract.Name, (int?)operation.Contract.TimeoutMs, operation.Contract.Retry)
                    : (_consumers[replaced.Target].Name, null, _subscriptions[replaced.Target].Contract.Retry);
                void Row(string field, int model, int? profile)
                {
                    if (profile is { } value && value != model)
                        rows.Add(new(replaced.Target, name, field, model, value, replaced.Label, replaced.MeasurementRef));
                }
                if (timeout is { } declared) Row("timeoutMs", declared, replaced.TimeoutMs);
                Row("maxAttempts", retry.MaxAttempts, replaced.MaxAttempts);
                Row("backoffMs", retry.BackoffMs, replaced.BackoffMs);
            }
            return rows;
        }

        private List<UnmodelledEffect> Unmodelled()
        {
            var effects = new List<UnmodelledEffect>(AlwaysUnmodelled);
            if (_scenario.Faults.Count > 0)
            {
                effects.Add(new("scenario-faults", "run",
                    $"The scenario declares {_scenario.Faults.Count} fault(s). A fault fails the first attempts of one playback, so a stress run does not apply it. Use outage windows."));
            }
            if (_scenario.Assertions.Count > 0)
                effects.Add(new("scenario-assertions", "run", "The assertions of the scenario. A stress run moves jobs, not payloads, and evaluates no assertion."));

            var used = new HashSet<Guid>([.. _operations.Keys, .. _subscriptions.Keys, .. _writeTimes.Keys]);
            var unused = _profile.ServiceTimes.Count(t => !used.Contains(t.Target))
                + _profile.Resources.Count(r => !_elements.ContainsKey(r.ElementId))
                + _profile.Subscriptions.Count(s => !_subscriptions.ContainsKey(s.SubscriptionId))
                + _profile.Branches.Count(b => !_branches.ContainsKey(b.StepId));
            if (unused > 0)
                effects.Add(new("unused-inputs", "run", $"The profile has {unused} input(s) for things that this flow does not use. They have no effect and are not in the assumptions."));
            return effects;
        }

        private Confidence ConfidenceOf()
        {
            var used = new HashSet<Guid>([.. _operations.Keys, .. _subscriptions.Keys, .. _writeTimes.Keys]);
            var declared = _profile.ServiceTimes
                .Where(t => used.Contains(t.Target) && t.Label != AssumptionLabel.Measured)
                .OrderBy(t => t.Target)
                .Select(t => $"The service time of '{(_operations.TryGetValue(t.Target, out var o) ? o.Contract.Name : _subscriptions.TryGetValue(t.Target, out var s) ? $"Handler of {s.Pool.Name}" : $"Write to {_model.Elements[t.Target]}")}' is declared, not measured.")
                .ToList();
            return declared.Count > 0
                ? new("low", ConfidenceRule, declared)
                : new("medium", ConfidenceRule, ["Every service time on the path of the flow names a measurement."]);
        }
    }
}
