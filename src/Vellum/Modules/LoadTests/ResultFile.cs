using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Vellum.Shared;

namespace Vellum.Modules.LoadTests;

/// <summary>Request durations in milliseconds, as k6 computed them.</summary>
public sealed record MeasuredLatency(
    long Count, double MinMs, double MeanMs, double P50Ms, double P90Ms, double P95Ms, double P99Ms, double MaxMs);

/// <param name="Requests">Requests of iterations that started after the warm-up.</param>
/// <param name="UnexpectedStatus">
/// Requests whose status was not the expected one. A 3xx status is never the expected one.
/// The stored member keeps the name <c>failed</c> of the first format.
/// </param>
/// <param name="Latency">The durations of the requests that expected a 2xx status and got one.</param>
/// <param name="ExpectedFailures">Requests that expected a failure and got one. They are in no success latency.</param>
/// <param name="ExpectedFailureLatency">The durations of those requests. Null when there is none.</param>
public sealed record MeasuredOperation(
    Guid OperationId, string Name, long Requests,
    [property: JsonPropertyName("failed")] long UnexpectedStatus, MeasuredLatency? Latency,
    long ExpectedFailures = 0, MeasuredLatency? ExpectedFailureLatency = null);

/// <param name="Iterations">Flows that started after the warm-up.</param>
/// <param name="UnexpectedStatus">Flows in which a request did not get the expected status. The stored member keeps the name <c>failed</c>.</param>
/// <param name="DroppedIterations">Iterations that k6 did not start because no virtual user was free, over the whole run.</param>
/// <param name="Latency">
/// The sum of the request durations of a flow, for flows in which every request got a 2xx
/// status. Null when a step of the flow expects a failure: such a flow has no success latency.
/// </param>
public sealed record MeasuredFlow(
    long Iterations, [property: JsonPropertyName("failed")] long UnexpectedStatus, long DroppedIterations, MeasuredLatency? Latency);

/// <summary>What one k6 run measured. Every number is read from the imported file.</summary>
/// <param name="ElapsedMs">From the end of <c>setup()</c> to the start of <c>teardown()</c>: the time the executor ran.</param>
/// <param name="Iterations">Iterations that k6 completed over the whole run, warm-up included. Null in a result of the first format.</param>
/// <param name="Load">The executor options that k6 ran, as the script stated them. Null in a result of the first format.</param>
public sealed record MeasuredRun(
    double WarmUpMs, double ElapsedMs, MeasuredFlow Flow, IReadOnlyList<MeasuredOperation> Operations,
    long? Iterations = null, string? Load = null)
{
    /// <summary>The seconds in which the counted iterations ran.</summary>
    public double MeasuredSeconds => (ElapsedMs - WarmUpMs) / 1000;
}

/// <summary>The identity that a Vellum result file states for itself. A k6 summary export states the script hash only.</summary>
public sealed record ClaimedIdentity(
    string? Generator, Guid? ScenarioId, long? ScenarioVersion, Guid? ModelStreamId, long? ModelVersion,
    Guid? TargetId, string? Environment, string? BaseUrlHash);

/// <param name="StatedDurationMs">The planned duration that a Vellum result file states. Null for a summary export.</param>
/// <param name="StatedIterations">The planned iterations that a Vellum result file states. Null for a summary export.</param>
public sealed record ParsedResult(
    string Format, string ScriptHash, ClaimedIdentity? Claimed, DateTimeOffset? StartedAt, MeasuredRun Run,
    double? StatedDurationMs = null, double? StatedIterations = null);

/// <summary>
/// Reads the two files a k6 run of a Vellum script leaves: the Vellum result that
/// <c>handleSummary</c> writes, and the JSON of <c>k6 run --summary-export</c>. A file is
/// untrusted input. It is bounded before it is parsed, and every number is checked before it
/// is stored. Each error names the member of the file that causes it.
/// </summary>
public static partial class ResultFile
{
    public const string SummaryExportFormat = "k6-summary-export";
    public const long MaxBytes = 256 * 1024;
    private const int MaxDepth = 12;
    private const int MaxLoadChars = 8_000;
    private const long MaxCount = 1_000_000_000_000;
    private const double MaxLatencyMs = 86_400_000;
    private const double MaxElapsedMs = 7 * 86_400_000d;

    [GeneratedRegex(@"\A[0-9a-f]{64}\z")]
    private static partial Regex Sha256Hex();

    [GeneratedRegex(@"\Avellum_script_([0-9a-f]{64})\z")]
    private static partial Regex IdentityMetric();

    public static bool IsHash(string? text) => text is not null && Sha256Hex().IsMatch(text);

    /// <summary>The document, or the reason it is not one.</summary>
    public static (JsonDocument? Document, string? Error) Open(ReadOnlyMemory<byte> bytes)
    {
        try
        {
            var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = MaxDepth });
            if (document.RootElement.ValueKind == JsonValueKind.Object)
                return (document, null);
            document.Dispose();
            return (null, "The file is not a JSON object.");
        }
        catch (JsonException e)
        {
            return (null, $"The file is not valid JSON, or it nests deeper than {MaxDepth} levels. {e.Message}");
        }
    }

    /// <summary>
    /// The format and the script hash of the file. This is all that is read before the script
    /// is known. <paramref name="Format"/> is null when the file is neither format, or when it
    /// is a format that cannot carry the identity of a script.
    /// </summary>
    public static (string? Format, string? ScriptHash, FieldError? Error) Identify(JsonElement root)
    {
        if (root.TryGetProperty("format", out var format))
        {
            if (format.ValueKind != JsonValueKind.String || format.GetString() != K6ScriptGenerator.ResultFormat)
                return (null, null, new("format", $"The format is not supported. This server reads '{K6ScriptGenerator.ResultFormat}' and the JSON of 'k6 run --summary-export'."));
            return root.TryGetProperty("scriptHash", out var hash) && hash.ValueKind == JsonValueKind.String && IsHash(hash.GetString())
                ? (K6ScriptGenerator.ResultFormat, hash.GetString(), null)
                : (null, null, new("scriptHash", "The script hash is 64 lower-case hexadecimal characters."));
        }

        if (root.TryGetProperty("metrics", out var metrics) && metrics.ValueKind == JsonValueKind.Object)
        {
            // A summary export has no member for an identity. A script that Vellum generated
            // counts its iterations in a metric whose name holds its hash, and that name is in
            // the export. An export of any other script has no such metric and is refused.
            var hashes = metrics.EnumerateObject().Select(m => IdentityMetric().Match(m.Name))
                .Where(m => m.Success).Select(m => m.Groups[1].Value).Take(2).ToList();
            return hashes.Count == 1
                ? (SummaryExportFormat, hashes[0], null)
                : (null, null, new("metrics", hashes.Count == 0
                    ? "This k6 summary export names no Vellum script, so nothing binds its numbers to a scenario, a model revision and a target. "
                      + "An export carries the identity only when a script that Vellum generated produced it: such a script has the metric 'vellum_script_<hash>'."
                    : "This k6 summary export names more than one Vellum script. A result belongs to one script."));
        }

        return (null, null, new("format", $"The file is not a Vellum k6 result ('{K6ScriptGenerator.ResultFormat}') and not a k6 summary export."));
    }

    /// <summary>Reads the numbers of a file whose script is known. <paramref name="plan"/> says which operations the script measured.</summary>
    public static (ParsedResult? Result, IReadOnlyList<FieldError> Errors) Read(JsonElement root, string format, string scriptHash, K6Plan plan)
    {
        var errors = new List<FieldError>();
        var reader = new Reader(errors);
        var result = format == SummaryExportFormat
            ? ReadSummaryExport(root, scriptHash, plan, reader)
            : ReadVellum(root, scriptHash, plan, reader);
        if (errors.Count == 0)
            Check(result, plan, format == SummaryExportFormat, errors);
        return errors.Count == 0 ? (result, errors) : (null, errors);
    }

    private static ParsedResult ReadVellum(JsonElement root, string scriptHash, K6Plan plan, Reader read)
    {
        var claimed = new ClaimedIdentity(
            read.Text(root, "generator", "generator"),
            read.Id(root, "scenarioId", "scenarioId"), read.Whole(root, "scenarioVersion", "scenarioVersion"),
            read.Id(root, "modelStreamId", "modelStreamId"), read.Whole(root, "modelVersion", "modelVersion"),
            read.Id(root, "targetId", "targetId"), read.Text(root, "environment", "environment"), read.Text(root, "baseUrlHash", "baseUrlHash"));

        DateTimeOffset? startedAt = null;
        if (read.Text(root, "startedAt", "startedAt") is { } started)
        {
            if (DateTimeOffset.TryParse(started, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at))
                startedAt = at;
            else
                read.Error("startedAt", "The start of the run is a date and a time in ISO 8601 form.");
        }

        var window = read.Object(root, "window", "window");
        var warmUp = window is { } w ? read.Number(w, "warmUpMs", "window.warmUpMs") : null;
        var elapsed = window is { } w2 ? read.Number(w2, "elapsedMs", "window.elapsedMs") : null;

        string? effective = null;
        long? iterations = null;
        long dropped = 0;
        double? statedDuration = null, statedIterations = null;
        if (read.Object(root, "load", "load") is { } l)
        {
            effective = read.Text(l, "effective", "load.effective", MaxLoadChars);
            statedDuration = read.Number(l, "plannedDurationMs", "load.plannedDurationMs");
            statedIterations = read.Number(l, "plannedIterations", "load.plannedIterations");
            iterations = read.Whole(l, "iterations", "load.iterations");
            dropped = read.Whole(l, "droppedIterations", "load.droppedIterations") ?? 0;
        }

        var flow = new MeasuredFlow(0, 0, dropped, null);
        if (read.Object(root, "flow", "flow") is { } f)
        {
            flow = new MeasuredFlow(
                read.Whole(f, "iterations", "flow.iterations") ?? 0, read.Whole(f, "unexpectedStatus", "flow.unexpectedStatus") ?? 0,
                dropped, Latency(f, "latencyMs", "flow.latencyMs", read));
        }

        var operations = new List<MeasuredOperation>();
        if (!root.TryGetProperty("operations", out var list) || list.ValueKind != JsonValueKind.Array)
        {
            read.Error("operations", "The list of operations is missing.");
        }
        else if (list.GetArrayLength() != plan.Operations.Count)
        {
            read.Error("operations", $"The script measures {plan.Operations.Count} operation(s); the file has {list.GetArrayLength()}.");
        }
        else
        {
            var names = plan.Operations.ToDictionary(o => o.OperationId, o => o.Name);
            var index = 0;
            foreach (var item in list.EnumerateArray())
            {
                var at = $"operations[{index++}]";
                if (item.ValueKind != JsonValueKind.Object)
                {
                    read.Error(at, "An operation is an object.");
                    continue;
                }
                var id = read.Id(item, "operationId", $"{at}.operationId");
                if (id is { } known && !names.ContainsKey(known))
                    read.Error($"{at}.operationId", "The script does not drive this operation.");
                else if (id is { } again && operations.Any(o => o.OperationId == again))
                    read.Error($"{at}.operationId", "The operation is in the file more than once.");
                operations.Add(new(
                    id ?? Guid.Empty, id is { } named ? names.GetValueOrDefault(named, "") : "",
                    read.Whole(item, "requests", $"{at}.requests") ?? 0, read.Whole(item, "unexpectedStatus", $"{at}.unexpectedStatus") ?? 0,
                    Latency(item, "latencyMs", $"{at}.latencyMs", read),
                    read.Whole(item, "expectedFailures", $"{at}.expectedFailures") ?? 0,
                    Latency(item, "expectedFailureLatencyMs", $"{at}.expectedFailureLatencyMs", read)));
            }
        }

        return new ParsedResult(
            K6ScriptGenerator.ResultFormat, scriptHash, claimed, startedAt,
            new MeasuredRun(warmUp ?? 0, elapsed ?? 0, flow, operations, iterations, effective), statedDuration, statedIterations);

        static MeasuredLatency? Latency(JsonElement parent, string name, string at, Reader read)
        {
            if (!parent.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
                return null;
            if (value.ValueKind != JsonValueKind.Object)
            {
                read.Error(at, "A latency summary is an object, or null when no request counted.");
                return null;
            }
            return new MeasuredLatency(
                read.Whole(value, "count", $"{at}.count") ?? 0,
                read.Number(value, "min", $"{at}.min") ?? 0, read.Number(value, "mean", $"{at}.mean") ?? 0,
                read.Number(value, "p50", $"{at}.p50") ?? 0, read.Number(value, "p90", $"{at}.p90") ?? 0,
                read.Number(value, "p95", $"{at}.p95") ?? 0, read.Number(value, "p99", $"{at}.p99") ?? 0,
                read.Number(value, "max", $"{at}.max") ?? 0);
        }
    }

    /// <summary>
    /// The same numbers from the metrics of a summary export. The export holds no scenario,
    /// revision or target: those come from the stored script that the hash names.
    /// </summary>
    private static ParsedResult ReadSummaryExport(JsonElement root, string scriptHash, K6Plan plan, Reader read)
    {
        var metrics = root.GetProperty("metrics");

        DateTimeOffset? startedAt = null;
        string? effective = null;
        if (root.TryGetProperty("setup_data", out var setup) && setup.ValueKind == JsonValueKind.Object
            && read.Number(setup, "startedAtMs", "setup_data.startedAtMs") is { } startedAtMs)
        {
            if (startedAtMs is >= 0 and < 253_402_300_800_000)
                startedAt = DateTimeOffset.FromUnixTimeMilliseconds((long)startedAtMs);
            else
                read.Error("setup_data.startedAtMs", "The start of the run is not a time.");
            effective = read.Text(setup, "load", "setup_data.load", MaxLoadChars);
        }
        else
        {
            read.Error("setup_data.startedAtMs", "The export does not state when the run started.");
        }

        double? elapsed = null;
        if (read.Object(metrics, "vellum_run_elapsed_ms", "metrics.vellum_run_elapsed_ms") is { } gauge)
            elapsed = read.Number(gauge, "max", "metrics.vellum_run_elapsed_ms.max");

        var (flowTotal, flowUnexpected) = Unexpected("vellum_flow_unexpected");
        var flow = new MeasuredFlow(flowTotal, flowUnexpected, Count("dropped_iterations"), Latency("vellum_flow_duration"));

        var operations = plan.Operations.Select(o =>
        {
            var (total, unexpected) = Unexpected($"vellum_op{o.Index}_unexpected");
            var failures = Latency($"vellum_op{o.Index}_expected_failure_duration");
            return new MeasuredOperation(o.OperationId, o.Name, total, unexpected, Latency($"vellum_op{o.Index}_duration"), failures?.Count ?? 0, failures);
        }).ToList();

        return new ParsedResult(
            SummaryExportFormat, scriptHash, null, startedAt,
            new MeasuredRun(plan.WarmUpMs, elapsed ?? 0, flow, operations, Count("iterations"), effective));

        // k6 leaves a metric out when it got no sample: no request counted.
        (long Total, long Unexpected) Unexpected(string name)
        {
            if (!metrics.TryGetProperty(name, out var metric) || metric.ValueKind != JsonValueKind.Object)
                return (0, 0);
            var unexpected = read.Whole(metric, "passes", $"metrics.{name}.passes") ?? 0;
            var met = read.Whole(metric, "fails", $"metrics.{name}.fails") ?? 0;
            return (unexpected + met, unexpected);
        }

        long Count(string name) =>
            metrics.TryGetProperty(name, out var metric) && metric.ValueKind == JsonValueKind.Object
                ? read.Whole(metric, "count", $"metrics.{name}.count") ?? 0
                : 0;

        MeasuredLatency? Latency(string name)
        {
            if (!metrics.TryGetProperty(name, out var metric) || metric.ValueKind != JsonValueKind.Object)
                return null;
            var at = $"metrics.{name}";
            var count = read.Whole(metric, "count", $"{at}.count") ?? 0;
            if (count == 0)
                return null;
            return new MeasuredLatency(
                count,
                read.Number(metric, "min", $"{at}.min") ?? 0, read.Number(metric, "avg", $"{at}.avg") ?? 0,
                read.Number(metric, "med", $"{at}.med") ?? 0, read.Number(metric, "p(90)", $"{at}.p(90)") ?? 0,
                read.Number(metric, "p(95)", $"{at}.p(95)") ?? 0, read.Number(metric, "p(99)", $"{at}.p(99)") ?? 0,
                read.Number(metric, "max", $"{at}.max") ?? 0);
        }
    }

    /// <summary>
    /// What must hold between the numbers when the script wrote them. A file that breaks one
    /// of these did not come from a run of the script as Vellum generated it.
    /// <para>
    /// The load of the run is one of them. The script stops in <c>setup()</c> when k6 runs
    /// other executor options than the generated ones, and it states the options that k6 ran.
    /// So a file is refused when those options are not the ones of the plan, when the run is
    /// shorter than the plan needs to start its iterations, and when the iterations that k6
    /// completed and dropped are not the planned arrivals. The tolerance is
    /// <see cref="K6Load.Tolerance"/>: 5 % of the planned iterations, and at least 2.
    /// </para>
    /// </summary>
    private static void Check(ParsedResult result, K6Plan plan, bool export, List<FieldError> errors)
    {
        var run = result.Run;
        static string N(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
        var elapsedAt = export ? "metrics.vellum_run_elapsed_ms.max" : "window.elapsedMs";
        if (run.WarmUpMs != plan.WarmUpMs)
            errors.Add(new("window.warmUpMs", $"The script has a warm-up of {plan.WarmUpMs} ms; the file states {N(run.WarmUpMs)} ms."));
        if (run.ElapsedMs > MaxElapsedMs)
            errors.Add(new(elapsedAt, "A run is at most 7 days long."));
        else if (run.ElapsedMs <= run.WarmUpMs)
            errors.Add(new(elapsedAt, $"The run ended inside the warm-up of {plan.WarmUpMs} ms. It has no measured window."));
        else if (run.ElapsedMs < plan.MinElapsedMs)
            errors.Add(new(elapsedAt, $"The run lasted {N(run.ElapsedMs)} ms. The plan of the script has a duration of {plan.DurationMs} ms and needs {plan.MinElapsedMs} ms to start its iterations. A shorter run did not apply the load of the profile."));
        if (result.StartedAt is null && !export)
            errors.Add(new("startedAt", "The file does not state when the run started."));

        var loadAt = export ? "setup_data.load" : "load.effective";
        if (run.Load != plan.Load)
            errors.Add(new(loadAt, $"The run used the executor options '{run.Load}'. The script was generated with '{plan.Load}'. A run with other options did not apply the load of the profile."));
        if (result.StatedDurationMs is { } statedDuration && statedDuration != plan.DurationMs)
            errors.Add(new("load.plannedDurationMs", $"The script plans {plan.DurationMs} ms; the file states {N(statedDuration)} ms."));
        var planned = plan.PlannedIterations!.Value;
        if (result.StatedIterations is { } statedIterations && Math.Abs(statedIterations - planned) > 1e-6)
            errors.Add(new("load.plannedIterations", $"The script plans {N(planned)} iteration(s); the file states {N(statedIterations)}."));

        var iterationsAt = export ? "metrics.iterations.count" : "load.iterations";
        var started = (run.Iterations ?? 0) + run.Flow.DroppedIterations;
        var tolerance = K6Load.Tolerance(planned);
        if (Math.Abs(started - planned) > tolerance)
            errors.Add(new(iterationsAt, $"k6 completed {run.Iterations ?? 0} iteration(s) and dropped {run.Flow.DroppedIterations}. The plan of the script starts {N(planned)}, and the two may differ by {N(tolerance)}. The run did not apply the load of the profile, or k6 stopped iterations before they ended."));
        else if (run.Flow.Iterations > (run.Iterations ?? 0))
            errors.Add(new(iterationsAt, $"The file counts {run.Flow.Iterations} flow(s) after the warm-up and {run.Iterations ?? 0} iteration(s) in the whole run."));

        var expectFailure = plan.ExpectFailure();
        var flowAt = export ? "metrics.vellum_flow_unexpected" : "flow";
        var flowLatencyAt = export ? "metrics.vellum_flow_duration" : "flow.latencyMs";
        if (run.Flow.UnexpectedStatus > run.Flow.Iterations)
            errors.Add(new(flowAt, "More flows with an unexpected status than flows."));
        // The script records the duration of a flow only when no step of the flow expects a failure.
        else if (expectFailure.Count > 0 && run.Flow.Latency is not null)
            errors.Add(new(flowLatencyAt, "A step of the scenario expects a failure, so the script records no flow latency. The file has one."));
        else if (expectFailure.Count == 0 && (run.Flow.Latency?.Count ?? 0) != run.Flow.Iterations - run.Flow.UnexpectedStatus)
            errors.Add(new(flowLatencyAt, $"The latency summary counts {run.Flow.Latency?.Count ?? 0} flow(s); {run.Flow.Iterations - run.Flow.UnexpectedStatus} got the expected status. The script records one duration for each of those."));
        Order(flowLatencyAt, run.Flow.Latency);
        if (run.Flow.Iterations == 0 && errors.Count == 0)
            errors.Add(new(flowAt, "No flow started after the warm-up. The file holds no measurement."));

        for (var i = 0; i < run.Operations.Count; i++)
        {
            var operation = run.Operations[i];
            // In an export the metrics are found by the index of the operation in the script.
            var index = export ? plan.Operations.First(o => o.OperationId == operation.OperationId).Index : i;
            var at = export ? $"metrics.vellum_op{index}_unexpected" : $"operations[{i}]";
            var latencyAt = export ? $"metrics.vellum_op{index}_duration" : $"operations[{i}].latencyMs";
            var failureAt = export ? $"metrics.vellum_op{index}_expected_failure_duration" : $"operations[{i}].expectedFailureLatencyMs";

            var successes = operation.Requests - operation.UnexpectedStatus - operation.ExpectedFailures;
            if (successes < 0)
                errors.Add(new(at, "More requests with an unexpected status and expected failures than requests."));
            else if (operation.ExpectedFailures > 0 && !expectFailure.Contains(operation.OperationId))
                errors.Add(new(failureAt, "No step of this operation expects a failure. The file counts expected failures for it."));
            // The script records a duration for each request that got the expected status: a success in one summary, an expected failure in the other.
            else if ((operation.Latency?.Count ?? 0) != successes)
                errors.Add(new(latencyAt, $"The latency summary counts {operation.Latency?.Count ?? 0} request(s); {successes} got the 2xx status they expected. The script records one duration for each of those."));
            else if ((operation.ExpectedFailureLatency?.Count ?? 0) != operation.ExpectedFailures)
                errors.Add(new(failureAt, $"The summary of expected failures counts {operation.ExpectedFailureLatency?.Count ?? 0} request(s); the file states {operation.ExpectedFailures} expected failure(s)."));
            Order(latencyAt, operation.Latency);
            Order(failureAt, operation.ExpectedFailureLatency);
        }

        void Order(string latencyAt, MeasuredLatency? latency)
        {
            if (latency is null)
                return;
            double[] ascending = [latency.MinMs, latency.P50Ms, latency.P90Ms, latency.P95Ms, latency.P99Ms, latency.MaxMs];
            if (ascending.Any(v => v > MaxLatencyMs) || latency.MeanMs > MaxLatencyMs)
                errors.Add(new(latencyAt, "A duration is at most a day."));
            else if (!ascending.Zip(ascending.Skip(1)).All(pair => pair.First <= pair.Second) || latency.MeanMs < latency.MinMs || latency.MeanMs > latency.MaxMs)
                errors.Add(new(latencyAt, "The values are not in order: minimum, p50, p90, p95, p99, maximum, with the mean between the minimum and the maximum."));
        }
    }

    /// <summary>Reads members of the file and records each defect at the member.</summary>
    private sealed class Reader(List<FieldError> errors)
    {
        public void Error(string at, string message) => errors.Add(new(at, message));

        public JsonElement? Object(JsonElement parent, string name, string at)
        {
            if (parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object)
                return value;
            Error(at, "The member is missing or is not an object.");
            return null;
        }

        public string? Text(JsonElement parent, string name, string at, int max = 200)
        {
            if (parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                && value.GetString() is { } text && text.Length <= max && !text.Any(char.IsControl))
                return text;
            Error(at, "The member is missing or is not a line of text.");
            return null;
        }

        public Guid? Id(JsonElement parent, string name, string at)
        {
            if (parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && Guid.TryParse(value.GetString(), out var id))
                return id;
            Error(at, "The member is missing or is not an ID.");
            return null;
        }

        /// <summary>A finite number that is 0 or more.</summary>
        public double? Number(JsonElement parent, string name, string at)
        {
            if (parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
                && value.TryGetDouble(out var number) && double.IsFinite(number) && number >= 0)
                return number;
            Error(at, "The member is missing or is not a number that is 0 or more.");
            return null;
        }

        /// <summary>A whole number from 0 to a million million. k6 writes a count as a number with no fraction.</summary>
        public long? Whole(JsonElement parent, string name, string at)
        {
            if (parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
                && value.TryGetDouble(out var number) && number >= 0 && number <= MaxCount && Math.Floor(number) == number)
                return (long)number;
            Error(at, "The member is missing or is not a whole number that is 0 or more.");
            return null;
        }
    }
}
