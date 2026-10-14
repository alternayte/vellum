using System.Text.Json;
using Deedbox;
using Microsoft.EntityFrameworkCore;
using Vellum.Modules.Stress.Engine;
using Vellum.Shared;

namespace Vellum.Modules.LoadTests;

/// <summary>A measured service time of a profile and whether the result it names is stale now.</summary>
/// <param name="Target">The operation of the service time.</param>
/// <param name="MeasurementStale">True when the scenario or the model stream of the result has another version now, or the result is gone.</param>
/// <param name="StaleBecause"><c>model</c>, <c>scenario</c>, <c>missing</c>, or more than one.</param>
public sealed record ProfileMeasurement(Guid Target, string MeasurementRef, bool MeasurementStale, IReadOnlyList<string> StaleBecause);

/// <summary>
/// The one way a value of a stress profile is <c>measured</c> (SDD guarantee 2): it is the
/// service time of an operation, it names an imported k6 result of the scenario of the
/// profile, that result covers the operation, and the numbers are the numbers of the result.
/// The caller picks the result and the distribution. The server writes the numbers, so a
/// person cannot give a typed number the label <c>measured</c>.
/// <para>
/// The mapping from the measured request latency of the operation to the parameters:
/// <c>fixed</c> takes the mean; <c>exponential</c> takes the mean; <c>lognormal</c> takes the
/// median (p50) and the 95th percentile; <c>uniform</c> takes the minimum and the maximum.
/// </para>
/// <para>
/// The limit. k6 measures the time from the request to the response, from outside. That time
/// holds the network, the wait for a worker at the target and every step inside the call. The
/// service time of the model is the time one call holds a worker without its steps. So the
/// measured value is an upper estimate of the service time, and it holds for the load of that
/// run only. The confidence rule of the engine says so.
/// </para>
/// </summary>
public static class MeasuredServiceTimes
{
    internal static Distribution Map(DistributionKind kind, MeasuredLatency latency) => kind switch
    {
        DistributionKind.Fixed => new(kind, Ms: latency.MeanMs),
        DistributionKind.Exponential => new(kind, MeanMs: latency.MeanMs),
        DistributionKind.Lognormal => new(kind, MedianMs: latency.P50Ms, P95Ms: latency.P95Ms),
        _ => new(kind, MinMs: latency.MinMs, MaxMs: latency.MaxMs),
    };

    private sealed record Source(ResultEntity Row, MeasuredRun Run);

    /// <summary>The result that each reference names in the project. One spelling for each result: the ID in lower case with dashes.</summary>
    private static async Task<Dictionary<string, Source>> SourcesAsync(
        AppDbContext db, Guid projectId, IEnumerable<string?> references, CancellationToken ct)
    {
        var ids = references.OfType<string>().Distinct(StringComparer.Ordinal)
            .Where(r => r.StartsWith(MeasurementRefs.Prefix, StringComparison.Ordinal)
                && Guid.TryParseExact(r[MeasurementRefs.Prefix.Length..], "D", out var id) && MeasurementRefs.For(id) == r)
            .ToDictionary(r => Guid.ParseExact(r[MeasurementRefs.Prefix.Length..], "D"), r => r);
        if (ids.Count == 0)
            return [];
        var wanted = ids.Keys.ToList();
        var rows = await db.Results.AsNoTracking().Where(r => r.ProjectId == projectId && wanted.Contains(r.Id)).ToListAsync(ct);
        return rows.ToDictionary(r => ids[r.Id], r => new Source(r, JsonSerializer.Deserialize<MeasuredRun>(r.Document, Scripts.Json)!), StringComparer.Ordinal);
    }

    /// <summary>Null when the result gives the service time of the target, else why it does not.</summary>
    private static string? Refusal(ServiceTimeAssumption time, Guid scenarioId, Source? source, out MeasuredLatency? latency)
    {
        latency = null;
        if (source is null)
        {
            return $"A service time is 'measured' only when it names a measured result that Vellum imported for this project, as '{MeasurementRefs.Prefix}<result ID>'. "
                + $"'{time.MeasurementRef}' names none. Import the k6 result first, or use 'declared'.";
        }
        if (source.Row.ScenarioId != scenarioId)
            return "The measured result is of another scenario than the profile. A measurement of another flow is no measurement of this one.";
        latency = source.Run.Operations.FirstOrDefault(o => o.OperationId == time.Target)?.Latency;
        return latency is null
            ? "The measured result has no latency for this target. A value is 'measured' only for an operation that the k6 script drove and that answered with the expected 2xx status."
            : null;
    }

    /// <summary>
    /// The profile with the numbers of each measured service time taken from the result it
    /// names, or the reasons a label <c>measured</c> is refused. Numbers that the caller sent
    /// for a measured service time are not read.
    /// </summary>
    public static async Task<(StressProfile Profile, IReadOnlyList<FieldError> Errors)> ApplyAsync(
        AppDbContext db, Guid projectId, StressProfile profile, CancellationToken ct)
    {
        // A profile that lacks a list is refused by the shape rules; there is nothing to resolve in it.
        if (profile.Arrivals is null || profile.Resources is null || profile.ServiceTimes is null || profile.Subscriptions is null
            || profile.Branches is null || profile.Overrides is null)
            return (profile, []);

        var errors = new List<FieldError>();
        void NotMeasurable(string field, AssumptionLabel? label)
        {
            if (label == AssumptionLabel.Measured)
                errors.Add(new($"{field}.label", "Only the service time of an operation can be 'measured'. A k6 result holds no measurement of this value. Use 'declared'."));
        }
        for (var i = 0; i < profile.Resources.Count; i++) NotMeasurable($"resources[{i}]", profile.Resources[i]?.Label);
        for (var i = 0; i < profile.Subscriptions.Count; i++) NotMeasurable($"subscriptions[{i}]", profile.Subscriptions[i]?.Label);
        for (var i = 0; i < profile.Branches.Count; i++) NotMeasurable($"branches[{i}]", profile.Branches[i]?.Label);
        for (var i = 0; i < profile.Overrides.Count; i++) NotMeasurable($"overrides[{i}]", profile.Overrides[i]?.Label);
        NotMeasurable("arrivals", profile.Arrivals.Label);

        var measured = profile.ServiceTimes.Select((time, index) => (time, index))
            .Where(x => x.time is { Label: AssumptionLabel.Measured }).ToList();
        if (measured.Count == 0)
            return (profile, errors);

        var sources = await SourcesAsync(db, projectId, measured.Select(x => x.time.MeasurementRef), ct);
        var times = profile.ServiceTimes.ToList();
        foreach (var (time, index) in measured)
        {
            if (time.Time is null || !Enum.IsDefined(time.Time.Kind))
                errors.Add(new($"serviceTimes[{index}].time.kind", "A measured service time states its distribution: 'fixed', 'exponential', 'lognormal' or 'uniform'. The server takes the numbers from the result."));
            else if (Refusal(time, profile.ScenarioId, time.MeasurementRef is { } name ? sources.GetValueOrDefault(name) : null, out var latency) is { } refusal)
                errors.Add(new($"serviceTimes[{index}].measurementRef", refusal));
            else
                times[index] = time with { Time = Map(time.Time.Kind, latency!) };
        }
        return (profile with { ServiceTimes = times }, errors);
    }

    /// <summary>
    /// The measured service times of a stored profile that do not count for a run of
    /// <paramref name="modelStreamId"/> at the two versions. A profile that was stored before
    /// the server wrote the numbers can hold a typed number with the label: it does not count.
    /// </summary>
    public static async Task<IReadOnlyDictionary<Guid, MeasurementDoubt>> DoubtsAsync(
        AppDbContext db, Guid projectId, StressProfile profile, Guid modelStreamId, long modelVersion, long scenarioVersion, CancellationToken ct)
    {
        var measured = profile.ServiceTimes.Where(t => t.Label == AssumptionLabel.Measured).ToList();
        var doubts = new Dictionary<Guid, MeasurementDoubt>();
        if (measured.Count == 0)
            return doubts;

        var sources = await SourcesAsync(db, projectId, measured.Select(t => t.MeasurementRef), ct);
        foreach (var time in measured)
        {
            var source = time.MeasurementRef is { } name ? sources.GetValueOrDefault(name) : null;
            if (source is null)
                doubts[time.Target] = new(true, "the measured result is not in the project.");
            else if (Refusal(time, profile.ScenarioId, source, out var latency) is not null || Map(time.Time.Kind, latency!) != time.Time)
                doubts[time.Target] = new(false, "the value is not the value of the measured result that it names. Save the profile again to take the value from the result.");
            else if (source.Row.ModelStreamId != modelStreamId)
                doubts[time.Target] = new(true, "the measured result is of another branch of the model.");
            else if (source.Row.ModelVersion != modelVersion || source.Row.ScenarioVersion != scenarioVersion)
                doubts[time.Target] = new(true, $"the measured result is stale. It is bound to model version {source.Row.ModelVersion} and scenario version {source.Row.ScenarioVersion}; the run uses model version {modelVersion} and scenario version {scenarioVersion}.");
        }
        return doubts;
    }

    /// <summary>The measured service times of a profile with the staleness of their results as the project is now.</summary>
    public static async Task<IReadOnlyList<ProfileMeasurement>> StateAsync(
        AppDbContext db, IEventStore store, Guid projectId, StressProfile profile, CancellationToken ct)
    {
        var measured = profile.ServiceTimes.Where(t => t is { Label: AssumptionLabel.Measured, MeasurementRef: not null }).ToList();
        if (measured.Count == 0)
            return [];

        var sources = await SourcesAsync(db, projectId, measured.Select(t => t.MeasurementRef), ct);
        var current = new MeasuredResults.CurrentVersions(store, projectId);
        var states = new List<ProfileMeasurement>();
        foreach (var time in measured)
        {
            IReadOnlyList<string> because = sources.TryGetValue(time.MeasurementRef!, out var source)
                ? await current.BecauseAsync(source.Row, ct)
                : ["missing"];
            states.Add(new(time.Target, time.MeasurementRef!, because.Count > 0, because));
        }
        return states;
    }
}
