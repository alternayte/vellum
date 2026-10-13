using System.Globalization;
using Vellum.Modules.Modelling.Model;
using Vellum.Modules.Trace.Engine;
using Vellum.Shared;

namespace Vellum.Modules.Stress.Engine;

/// <summary>A subscription that a publish step reaches, the bound channels on the way, and the steps its consumer runs.</summary>
internal sealed record StressRoute(SubscriptionContract Subscription, IReadOnlyList<ChannelId> Via, IReadOnlyList<Step> Handler);

/// <summary>
/// What one scenario uses when it runs: the operations it calls, the stores it writes, the
/// subscriptions its messages reach and the branch steps that need a probability. The walk
/// reads the same <see cref="Scenario"/> and <see cref="TraceModel"/> as the trace engine and
/// routes messages by the same rules (<see cref="ChannelRouting"/>). All sets are ordered by
/// ID, so nothing depends on the order of a dictionary.
/// </summary>
internal sealed class StressPlan
{
    public SortedSet<Guid> Operations { get; } = [];
    public SortedSet<Guid> Stores { get; } = [];
    public SortedSet<Guid> Subscriptions { get; } = [];
    public SortedSet<Guid> Channels { get; } = [];
    public List<string> Branches { get; } = [];
    public Dictionary<PublishStep, IReadOnlyList<StressRoute>> Routes { get; } = new(ReferenceEqualityComparer.Instance);
    public bool TooManyRoutes { get; private set; }

    /// <summary>Every element that holds workers: the owners of the called operations and the written stores.</summary>
    public SortedSet<Guid> Resources(TraceModel model)
    {
        var resources = new SortedSet<Guid>(Stores);
        foreach (var operation in Operations)
            resources.Add(model.Operations[operation].OwnerId);
        return resources;
    }

    /// <summary>The scenario must be valid for the trace engine: every reference resolves.</summary>
    public static StressPlan Read(TraceModel model, Scenario scenario)
    {
        var plan = new StressPlan();
        var subscriptionsOn = model.Subscriptions.Values.OrderBy(s => s.Id.Value).ToLookup(s => s.ChannelId);
        var bindingsFrom = model.Bindings.Values.OrderBy(b => b.Id.Value).ToLookup(b => b.SourceChannelId);
        Walk(scenario.Steps);
        return plan;

        void Walk(IReadOnlyList<Step> steps)
        {
            foreach (var step in steps)
            {
                switch (step)
                {
                    case CallStep call:
                        plan.Operations.Add(call.Operation);
                        Walk(call.Then);
                        Walk(call.OnError);
                        break;
                    case WriteStep write:
                        plan.Stores.Add(write.Store);
                        break;
                    case ParallelStep parallel:
                        foreach (var branch in parallel.Branches)
                            Walk(branch);
                        break;
                    case BranchStep branch:
                        plan.Branches.Add(branch.Id);
                        Walk(branch.Then);
                        Walk(branch.Else);
                        break;
                    case PublishStep publish:
                        plan.Channels.Add(publish.Channel);
                        var routes = new List<StressRoute>();
                        var considered = 0;
                        Route(publish, publish.Channel, [], routes, [publish.Channel], ref considered);
                        plan.Routes[publish] = routes;
                        foreach (var route in routes)
                        {
                            plan.Subscriptions.Add(route.Subscription.Id);
                            Walk(route.Handler);
                        }
                        break;
                }
            }
        }

        // The order of the trace engine: the subscriptions of a channel by ID, then its
        // bindings by ID, each followed to its end. A channel takes a message once.
        void Route(PublishStep step, ChannelId channel, IReadOnlyList<ChannelId> via, List<StressRoute> routes, HashSet<ChannelId> reached, ref int considered)
        {
            foreach (var subscription in subscriptionsOn[channel])
                routes.Add(new StressRoute(subscription, via, step.Handlers.GetValueOrDefault(subscription.Id) ?? []));

            foreach (var binding in bindingsFrom[channel])
            {
                if (++considered > TraceLimits.MaxRoutes)
                {
                    plan.TooManyRoutes = true;
                    return;
                }
                if (!ChannelRouting.Matches(binding.RoutingKey, step.RoutingKey) || !reached.Add(binding.TargetChannelId))
                    continue;
                Route(step, binding.TargetChannelId, [.. via, binding.TargetChannelId], routes, reached, ref considered);
            }
        }
    }
}

/// <summary>
/// The rules a profile must satisfy before it is stored or run. They are limits of range and
/// shape. Whether the profile has every input that its scenario needs is checked when it runs.
/// </summary>
public static class StressProfileRules
{
    public const int MaxEntries = 200;
    public const int MaxOutages = 50;
    public const int MaxBursts = 20;
    public const double MaxRatePerSecond = 100_000;
    public const int MaxWorkers = 10_000;
    public const int MaxQueueCapacity = 1_000_000;
    public const double MaxServiceMs = 3_600_000;
    /// <summary>The longest duration a profile may state. The engine runs less: see <see cref="StressLimits.MaxDurationSeconds"/>.</summary>
    public const double MaxDeclaredSeconds = 86_400;

    public static bool TryParseSeed(string? seed, out ulong value) =>
        ulong.TryParse(seed, NumberStyles.None, CultureInfo.InvariantCulture, out value);

    public static IReadOnlyList<StressIssue> Check(StressProfile profile, StressLimits limits)
    {
        var issues = new List<StressIssue>();
        void Invalid(string field, string message) => issues.Add(new("invalid-profile", field, message));

        if (string.IsNullOrWhiteSpace(profile.Name))
            Invalid("name", "A profile needs a name.");
        else if (TextRule.Line("name", profile.Name) is { } nameError)
            Invalid("name", nameError.Message + ".");
        if (!TryParseSeed(profile.Seed, out _))
            Invalid("seed", "The seed is a whole number from 0 to 18446744073709551615.");

        if (profile.Arrivals is null || profile.Resources is null || profile.ServiceTimes is null || profile.Subscriptions is null
            || profile.Branches is null || profile.Overrides is null || profile.Outages is null)
        {
            Invalid("", "A profile needs arrivals and the lists resources, serviceTimes, subscriptions, branches, overrides and outages; an empty list is valid.");
            return issues;
        }

        CheckArrivals(profile.Arrivals, Invalid);

        Count("resources", profile.Resources.Count, MaxEntries);
        Count("serviceTimes", profile.ServiceTimes.Count, MaxEntries);
        Count("subscriptions", profile.Subscriptions.Count, MaxEntries);
        Count("branches", profile.Branches.Count, MaxEntries);
        Count("overrides", profile.Overrides.Count, MaxEntries);
        Count("outages", profile.Outages.Count, MaxOutages);
        if (issues.Count > 0)
            return issues;

        var horizon = MaxDeclaredSeconds + limits.MaxDrainSeconds;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        bool First(string group, object key) => seen.Add($"{group}:{key}");

        for (var i = 0; i < profile.Resources.Count; i++)
        {
            var field = $"resources[{i}]";
            if (profile.Resources[i] is not { } resource) { Invalid(field, "The entry is empty."); continue; }
            if (!First("resource", resource.ElementId)) Invalid(field, $"Element {resource.ElementId} has two entries.");
            Workers($"{field}.concurrency", resource.Concurrency);
            Capacity($"{field}.queueCapacity", resource.QueueCapacity);
            Label(field, resource.Label, resource.MeasurementRef);
        }

        for (var i = 0; i < profile.ServiceTimes.Count; i++)
        {
            var field = $"serviceTimes[{i}]";
            if (profile.ServiceTimes[i] is not { } time) { Invalid(field, "The entry is empty."); continue; }
            if (!First("time", time.Target)) Invalid(field, $"Target {time.Target} has two service times.");
            CheckDistribution($"{field}.time", time.Time, Invalid);
            Label(field, time.Label, time.MeasurementRef);
        }

        for (var i = 0; i < profile.Subscriptions.Count; i++)
        {
            var field = $"subscriptions[{i}]";
            if (profile.Subscriptions[i] is not { } subscription) { Invalid(field, "The entry is empty."); continue; }
            if (!First("subscription", subscription.SubscriptionId)) Invalid(field, $"Subscription {subscription.SubscriptionId} has two entries.");
            Workers($"{field}.consumers", subscription.Consumers);
            Capacity($"{field}.queueCapacity", subscription.QueueCapacity);
            Label(field, subscription.Label, subscription.MeasurementRef);
        }

        for (var i = 0; i < profile.Branches.Count; i++)
        {
            var field = $"branches[{i}]";
            if (profile.Branches[i] is not { } branch) { Invalid(field, "The entry is empty."); continue; }
            if (string.IsNullOrEmpty(branch.StepId) || branch.StepId.Length > TextRule.MaxIdentifier)
                Invalid($"{field}.stepId", $"A step ID has 1 to {TextRule.MaxIdentifier} characters.");
            else if (!First("branch", branch.StepId))
                Invalid(field, $"Step '{branch.StepId}' has two probabilities.");
            if (!(branch.Probability >= 0 && branch.Probability <= 1))
                Invalid($"{field}.probability", "A probability is a number from 0 to 1.");
            Label(field, branch.Label, branch.MeasurementRef);
        }

        for (var i = 0; i < profile.Overrides.Count; i++)
        {
            var field = $"overrides[{i}]";
            if (profile.Overrides[i] is not { } replaced) { Invalid(field, "The entry is empty."); continue; }
            if (!First("override", replaced.Target)) Invalid(field, $"Target {replaced.Target} has two overrides.");
            if (replaced.TimeoutMs is null && replaced.MaxAttempts is null && replaced.BackoffMs is null)
                Invalid(field, "An override sets a timeout, a number of attempts or a backoff.");
            if (replaced.TimeoutMs is < 0 or > TraceLimits.MaxDurationMs)
                Invalid($"{field}.timeoutMs", $"A timeout is 0 to {TraceLimits.MaxDurationMs} ms; 0 means none.");
            if (replaced.MaxAttempts is < 1 or > TraceLimits.MaxAttempts)
                Invalid($"{field}.maxAttempts", $"Attempts are 1 to {TraceLimits.MaxAttempts}; 1 means no retry.");
            if (replaced.BackoffMs is < 0 or > TraceLimits.MaxDurationMs)
                Invalid($"{field}.backoffMs", $"A backoff is 0 to {TraceLimits.MaxDurationMs} ms.");
            Label(field, replaced.Label, replaced.MeasurementRef);
        }

        for (var i = 0; i < profile.Outages.Count; i++)
        {
            var field = $"outages[{i}]";
            if (profile.Outages[i] is not { } outage) { Invalid(field, "The entry is empty."); continue; }
            if (!(outage.FromSeconds >= 0 && outage.ToSeconds > outage.FromSeconds && outage.ToSeconds <= horizon))
                Invalid(field, $"An outage starts at 0 or later, ends after it starts and ends at {Number(horizon)} s or earlier.");
        }

        return issues;

        void Count(string field, int count, int max)
        {
            if (count > max)
                Invalid(field, $"At most {max} entries are allowed; this list has {count}.");
        }

        void Workers(string field, int value)
        {
            if (value is < 1 or > MaxWorkers)
                Invalid(field, $"The value is a whole number from 1 to {MaxWorkers}.");
        }

        void Capacity(string field, int? value)
        {
            if (value is < 0 or > MaxQueueCapacity)
                Invalid(field, $"A queue capacity is 0 to {MaxQueueCapacity}, or empty for no bound.");
        }

        void Label(string field, AssumptionLabel label, string? measurementRef)
        {
            if (!Enum.IsDefined(label))
                Invalid($"{field}.label", "A label is 'declared' or 'measured'.");
            else if (label == AssumptionLabel.Measured && string.IsNullOrWhiteSpace(measurementRef))
                Invalid($"{field}.measurementRef", "A value is 'measured' only when it names the measurement it comes from.");
            else if (label == AssumptionLabel.Declared && measurementRef is not null)
                Invalid($"{field}.measurementRef", "A declared value names no measurement.");
            else if (TextRule.Line("measurementRef", measurementRef) is { } error)
                Invalid($"{field}.measurementRef", error.Message + ".");
        }
    }

    private static void CheckArrivals(Arrivals arrivals, Action<string, string> invalid)
    {
        if (!Enum.IsDefined(arrivals.Kind))
        {
            invalid("arrivals.kind", "The kind is 'constant', 'poisson', 'ramp' or 'burst'.");
            return;
        }
        // A duration above the limit of the engine is not a defect of the profile: the run reports it as stress-too-large.
        if (!(arrivals.DurationSeconds > 0 && arrivals.DurationSeconds <= MaxDeclaredSeconds))
            invalid("arrivals.durationSeconds", $"The duration is more than 0 and at most {Number(MaxDeclaredSeconds)} s.");
        if (!(arrivals.WarmUpSeconds >= 0 && arrivals.WarmUpSeconds < arrivals.DurationSeconds))
            invalid("arrivals.warmUpSeconds", "The warm-up is 0 or more and shorter than the duration.");
        if (!(arrivals.RatePerSecond >= 0 && arrivals.RatePerSecond <= MaxRatePerSecond))
            invalid("arrivals.ratePerSecond", $"A rate is 0 to {Number(MaxRatePerSecond)} per second.");

        if (!Enum.IsDefined(arrivals.Label))
            invalid("arrivals.label", "A label is 'declared' or 'measured'.");
        else if (arrivals.Label == AssumptionLabel.Measured && string.IsNullOrWhiteSpace(arrivals.MeasurementRef))
            invalid("arrivals.measurementRef", "A value is 'measured' only when it names the measurement it comes from.");
        else if (arrivals.Label == AssumptionLabel.Declared && arrivals.MeasurementRef is not null)
            invalid("arrivals.measurementRef", "A declared value names no measurement.");
        else if (TextRule.Line("measurementRef", arrivals.MeasurementRef) is { } error)
            invalid("arrivals.measurementRef", error.Message + ".");

        switch (arrivals.Kind)
        {
            case ArrivalKind.Constant or ArrivalKind.Poisson:
                if (arrivals.RatePerSecond == 0)
                    invalid("arrivals.ratePerSecond", "The rate is more than 0.");
                break;

            case ArrivalKind.Ramp:
                if (arrivals.ToRatePerSecond is not { } to || !(to >= 0 && to <= MaxRatePerSecond))
                    invalid("arrivals.toRatePerSecond", $"A ramp needs the rate it ends at: 0 to {Number(MaxRatePerSecond)} per second.");
                else if (to == 0 && arrivals.RatePerSecond == 0)
                    invalid("arrivals.toRatePerSecond", "A ramp from 0 to 0 has no arrivals.");
                break;

            case ArrivalKind.Burst:
                if (arrivals.Bursts is not { Count: > 0 and <= MaxBursts } bursts)
                {
                    invalid("arrivals.bursts", $"A burst profile has 1 to {MaxBursts} windows.");
                    break;
                }
                var previousEnd = 0.0;
                for (var i = 0; i < bursts.Count; i++)
                {
                    if (bursts[i] is not { } window
                        || !(window.FromSeconds >= previousEnd && window.ToSeconds > window.FromSeconds && window.ToSeconds <= arrivals.DurationSeconds))
                    {
                        invalid($"arrivals.bursts[{i}]", "Windows are in time order, do not overlap and lie inside the duration.");
                        break;
                    }
                    if (!(window.RatePerSecond >= 0 && window.RatePerSecond <= MaxRatePerSecond))
                        invalid($"arrivals.bursts[{i}].ratePerSecond", $"A rate is 0 to {Number(MaxRatePerSecond)} per second.");
                    previousEnd = window.ToSeconds;
                }
                break;
        }
    }

    private static void CheckDistribution(string field, Distribution? time, Action<string, string> invalid)
    {
        bool Time(double? value) => value is >= 0 and <= MaxServiceMs;
        switch (time)
        {
            case null:
                invalid(field, "A service time needs a distribution.");
                break;
            case { Kind: DistributionKind.Fixed }:
                if (!Time(time.Ms)) invalid($"{field}.ms", $"A fixed time is 0 to {Number(MaxServiceMs)} ms.");
                break;
            case { Kind: DistributionKind.Exponential }:
                if (!Time(time.MeanMs) || time.MeanMs == 0) invalid($"{field}.meanMs", $"A mean is more than 0 and at most {Number(MaxServiceMs)} ms.");
                break;
            case { Kind: DistributionKind.Lognormal }:
                if (!Time(time.MedianMs) || time.MedianMs == 0) invalid($"{field}.medianMs", $"A median is more than 0 and at most {Number(MaxServiceMs)} ms.");
                else if (!Time(time.P95Ms) || time.P95Ms < time.MedianMs) invalid($"{field}.p95Ms", $"The 95th percentile is at least the median and at most {Number(MaxServiceMs)} ms.");
                break;
            case { Kind: DistributionKind.Uniform }:
                if (!Time(time.MinMs)) invalid($"{field}.minMs", $"A minimum is 0 to {Number(MaxServiceMs)} ms.");
                else if (!Time(time.MaxMs) || time.MaxMs < time.MinMs) invalid($"{field}.maxMs", $"The maximum is at least the minimum and at most {Number(MaxServiceMs)} ms.");
                break;
            default:
                invalid($"{field}.kind", "The kind is 'fixed', 'exponential', 'lognormal' or 'uniform'.");
                break;
        }
    }

    /// <summary>The arrivals that the profile gives on average: the area under the rate.</summary>
    public static double ExpectedArrivals(Arrivals arrivals) => arrivals.Kind switch
    {
        ArrivalKind.Ramp => (arrivals.RatePerSecond + (arrivals.ToRatePerSecond ?? 0)) / 2 * arrivals.DurationSeconds,
        ArrivalKind.Burst => arrivals.RatePerSecond * arrivals.DurationSeconds
            + (arrivals.Bursts ?? []).Sum(w => (w.RatePerSecond - arrivals.RatePerSecond) * (w.ToSeconds - w.FromSeconds)),
        _ => arrivals.RatePerSecond * arrivals.DurationSeconds,
    };

    internal static string Number(double value) => value.ToString("R", CultureInfo.InvariantCulture);
}

/// <summary>What a profile for one scenario must state, with the names a form shows.</summary>
public sealed record StressRequirements(
    IReadOnlyList<ScenarioIssue> ScenarioIssues,
    IReadOnlyList<RequiredResource> Resources,
    IReadOnlyList<RequiredServiceTime> ServiceTimes,
    IReadOnlyList<RequiredSubscription> Subscriptions,
    IReadOnlyList<string> Branches,
    IReadOnlyList<OutageTarget> OutageTargets,
    int ScenarioFaults);

public sealed record RequiredResource(Guid ElementId, string Name);

/// <param name="Kind"><c>operation</c>, <c>subscription</c> or <c>store</c>.</param>
/// <param name="TimeoutMs">What the model declares for an operation; null for the other kinds.</param>
public sealed record RequiredServiceTime(Guid Target, string Kind, string Name, int? TimeoutMs, int? MaxAttempts, int? BackoffMs);

public sealed record RequiredSubscription(Guid SubscriptionId, string Name, int MaxAttempts, int BackoffMs, bool DeadLetters);

/// <param name="Kind"><c>element</c>, <c>operation</c>, <c>channel</c> or <c>subscription</c>.</param>
public sealed record OutageTarget(Guid Id, string Kind, string Name);
