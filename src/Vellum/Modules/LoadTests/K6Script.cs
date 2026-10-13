using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Vellum.Modules.Build;
using Vellum.Modules.Stress.Engine;
using Vellum.Modules.Trace.Engine;

namespace Vellum.Modules.LoadTests;

/// <summary>An operation that the script sends requests for. <paramref name="Index"/> names its metrics: <c>vellum_op{Index}_duration</c>.</summary>
public sealed record K6Operation(int Index, Guid OperationId, string Name, Guid OwnerId, string OwnerName);

/// <param name="Expect"><c>success</c>: a 2xx status. <c>failure</c>: any other outcome, as the playback gives with the faults of the scenario.</param>
public sealed record K6Step(string StepId, int Operation, string Method, string Path, bool HasBody, string Expect, int TimeoutMs);

/// <summary>A step of the scenario that the script does not perform, and why.</summary>
/// <param name="Kind"><c>call</c>, <c>publish</c>, <c>write</c>, <c>parallel</c> or <c>branch</c>.</param>
public sealed record K6NotCovered(string StepId, string Kind, string Reason);

/// <summary>What a generated script drives and what it leaves out. It is stored with the script and read when a result is imported.</summary>
/// <param name="Load">
/// The executor options in one line, as the script compares them with the options that k6
/// runs. Null in a plan of generator <c>vellum-k6/1</c>, which did not bind a result to its load.
/// </param>
/// <param name="PlannedIterations">The iterations that the executor starts over the whole run, warm-up included. Null in a plan of generator 1.</param>
/// <param name="MinElapsedMs">The time the plan needs to start its iterations, less the tolerance of <see cref="K6Load.Tolerance"/>. Null in a plan of generator 1.</param>
/// <param name="LoadNotes">Where the load of the script differs from the arrivals of the profile. Null in a plan of generator 1.</param>
public sealed record K6Plan(
    string Generator, string Executor,
    IReadOnlyList<K6Operation> Operations, IReadOnlyList<K6Step> Steps,
    IReadOnlyList<K6NotCovered> NotCovered, IReadOnlyList<string> Notes,
    long WarmUpMs, long DurationMs, int Faults,
    string? Load = null, double? PlannedIterations = null, long? MinElapsedMs = null, IReadOnlyList<string>? LoadNotes = null)
{
    /// <summary>The operations that have a step whose expected outcome is a failure.</summary>
    public IReadOnlySet<Guid> ExpectFailure() =>
        Steps.Where(s => s.Expect != "success").Select(s => Operations[s.Operation].OperationId).ToHashSet();
}

/// <summary>How far the iterations of a run may be from the plan of its script.</summary>
public static class K6Load
{
    /// <summary>
    /// 5 % of the planned iterations, and at least 2. k6 starts an iteration at the instant
    /// the rate reaches it, so the first and the last one can fall on either side of the run.
    /// </summary>
    public static double Tolerance(double plannedIterations) => Math.Max(2, plannedIterations * 0.05);
}

public sealed record K6Input(
    Scenario Scenario, long ScenarioVersion, TraceModel Model, Guid ModelStreamId, long ModelVersion,
    Guid TargetId, TargetState Target, Guid ProfileId, int ProfileVersion, Arrivals Arrivals);

/// <param name="Code"><c>target-not-authorised</c>, <c>scenario-not-valid</c>, <c>failure-injection-on-production</c>, <c>no-http-step</c> or <c>rate-too-low</c>.</param>
public sealed record K6Refusal(string Code, string Message, IReadOnlyList<string> Reasons);

public sealed record K6Script(string Hash, string Content, K6Plan Plan);

/// <summary>
/// Model text inside JavaScript. Names, step IDs, paths and payloads come from people, from
/// imports and from AI proposals, so they are untrusted. No such text is ever written as
/// code: each one is a string literal or part of a line comment, in printable ASCII only.
/// </summary>
public static class JsText
{
    /// <summary>
    /// A double-quoted JavaScript string literal. Every character outside printable ASCII,
    /// the quote, the backslash and the characters that could end a script element or start
    /// a template or a comment are written as <c>\uXXXX</c>. A UTF-16 code unit is escaped on
    /// its own, so half of a surrogate pair gives a valid literal too.
    /// </summary>
    public static string Literal(string value)
    {
        var text = new StringBuilder(value.Length + 2).Append('"');
        foreach (var c in value)
        {
            if (c is >= ' ' and <= '~' and not ('"' or '\\' or '<' or '>' or '&' or '\'' or '`' or '$'))
                text.Append(c);
            else
                text.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
        }
        return text.Append('"').ToString();
    }

    /// <summary>
    /// One line of a <c>//</c> comment. A line comment ends at a line terminator only, and
    /// every line terminator of JavaScript is outside printable ASCII, so the escaped text
    /// cannot leave the comment.
    /// </summary>
    public static string Comment(string text)
    {
        var line = new StringBuilder(text.Length + 3).Append("//");
        if (text.Length > 0)
            line.Append(' ');
        foreach (var c in text)
        {
            if (c is >= ' ' and <= '~')
                line.Append(c);
            else
                line.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
        }
        return line.ToString();
    }
}

/// <summary>
/// Writes a k6 script for one scenario, one target and one stress profile. The script is a
/// function of its input only: it reads no clock and no random value, so the same scenario
/// revision, model revision, target and profile give the same bytes and the same hash.
/// <para>
/// The script plays the caller of the top-level HTTP calls of the scenario. What happens
/// inside the system under test (nested calls, writes, messages) is the work of that system:
/// the script does not perform it and the header lists it as not covered.
/// </para>
/// </summary>
public static partial class K6ScriptGenerator
{
    /// <summary>
    /// A change to what any input produces is a new version. It is part of the script and of its hash.
    /// Version 2 binds a run to the load of the profile, follows no redirect, names a status
    /// that is not the expected one "unexpected", and keeps the durations of expected failures apart.
    /// </summary>
    public const string Version = "vellum-k6/2";

    public const string ResultFormat = "vellum-k6-result/2";

    /// <summary>The constant with no hash in it. The hash of a script is the SHA-256 of its text with this line in place.</summary>
    public const string HashLinePrefix = "const SCRIPT_HASH = \"";
    private static readonly string ZeroHash = new('0', 64);

    private static readonly string[] BodyMethods = ["POST", "PUT", "PATCH"];
    /// <summary>The model holds no idempotency fact for an operation, so each of these counts as non-idempotent.</summary>
    private static readonly string[] NonIdempotentMethods = ["POST", "PUT", "PATCH", "DELETE"];

    private const int DefaultTimeoutMs = 60_000;
    private const int MaxPreAllocatedVus = 1_000;
    private const int MaxVus = 10_000;

    [GeneratedRegex(@"\{([^{}/?#]+)\}")]
    private static partial Regex Placeholder();

    public static (K6Script? Script, K6Refusal? Refusal) Generate(K6Input input)
    {
        var target = input.Target;
        if (target.AuthorisationInForce is not { } authorisation)
        {
            return (null, new K6Refusal("target-not-authorised",
                "The target is not authorised. An owner authorises the base URL of the target before a script is generated for it.", []));
        }

        var trace = TraceEngine.Run(input.Model, input.Scenario);
        if (!trace.IsValid)
        {
            return (null, new K6Refusal("scenario-not-valid",
                "The scenario is not valid against the model. A script is generated from a valid scenario only.",
                [.. trace.Issues.Select(i => i.StepId.Length > 0 ? $"Step '{i.StepId}': {i.Message}" : i.Message)]));
        }

        var allowNonIdempotent = !target.IsProduction || target.Override is { AllowNonIdempotent: true };
        var allowFailureInjection = !target.IsProduction || target.Override is { AllowFailureInjection: true };
        if (input.Scenario.Faults.Count > 0 && !allowFailureInjection)
        {
            return (null, new K6Refusal("failure-injection-on-production",
                $"The scenario declares {input.Scenario.Faults.Count} fault(s), and its checks expect the outcomes that those faults cause. "
                + "A script for a scenario with faults is not generated for a production-class target, unless an owner sets the failure-injection override on the target.", []));
        }

        var operations = new List<K6Operation>();
        var steps = new List<K6Step>();
        var bodies = new List<string?>();
        var notCovered = new List<K6NotCovered>();
        var notes = new List<string>();
        Walk(input.Scenario.Steps);

        if (steps.Count == 0)
        {
            return (null, new K6Refusal("no-http-step",
                notCovered.Any(n => n.Reason.StartsWith("Excluded:", StringComparison.Ordinal))
                    ? "No step is left to drive. The production rules exclude the HTTP steps of this scenario that k6 could drive."
                    : "The scenario has no step that k6 can drive. A script needs a top-level call of an http operation whose name is a method and a path, such as 'POST /orders'.",
                [.. notCovered.Select(n => $"Step '{n.StepId}' ({n.Kind}): {n.Reason}")]));
        }

        var loadNotes = new List<string>();
        var (load, tooLow) = Executor(input.Arrivals, loadNotes);
        if (load is null)
            return (null, new K6Refusal("rate-too-low", tooLow!, []));
        var plan = new K6Plan(
            Version, load.Executor, operations, steps, notCovered, [.. notes, .. loadNotes],
            (long)Math.Round(input.Arrivals.WarmUpSeconds * 1000), (long)Math.Round(input.Arrivals.DurationSeconds * 1000),
            input.Scenario.Faults.Count,
            load.Signature, load.PlannedIterations, load.MinElapsedMs, loadNotes);

        var (before, after) = Render(input, authorisation, plan, bodies, load.Options, allowNonIdempotent, allowFailureInjection);
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(before + HashLinePrefix + ZeroHash + "\";\n" + after)));
        return (new K6Script(hash, before + HashLinePrefix + hash + "\";\n" + after, plan), null);

        // The top-level sequence. A branch is resolved as the playback resolves it.
        void Walk(IReadOnlyList<Step> sequence)
        {
            foreach (var step in sequence)
            {
                switch (step)
                {
                    case CallStep call:
                        if (Drive(call) is { } reason)
                            notCovered.Add(new(call.Id, "call", reason));
                        Inside(call.Then, "The system under test does this while it serves the call. The load generator does not.");
                        Inside(call.OnError, "The caller's error handling is not driven.");
                        break;

                    case PublishStep publish:
                        notCovered.Add(new(publish.Id, "publish", "k6 sends HTTP requests only. It publishes no message."));
                        foreach (var handler in publish.Handlers.OrderBy(h => h.Key.Value))
                            Inside(handler.Value, "A consumer of the system under test does this. The load generator does not.");
                        break;

                    case WriteStep write:
                        notCovered.Add(new(write.Id, "write", "k6 sends HTTP requests only. It writes to no store."));
                        break;

                    case ParallelStep parallel:
                        notCovered.Add(new(parallel.Id, "parallel", "The script sends its requests one after the other. It does not drive parallel branches."));
                        foreach (var branch in parallel.Branches)
                            Inside(branch, "Part of a parallel step, which the script does not drive.");
                        break;

                    case BranchStep branch:
                        var taken = trace.Outcomes.GetValueOrDefault(branch.WhenStep, StepOutcome.Skipped) == branch.Is;
                        Walk(taken ? branch.Then : branch.Else);
                        Inside(taken ? branch.Else : branch.Then, "The playback of the scenario does not take this path.");
                        break;
                }
            }
        }

        void Inside(IReadOnlyList<Step> sequence, string reason)
        {
            foreach (var step in sequence)
            {
                notCovered.Add(new(step.Id, Kind(step), reason));
                switch (step)
                {
                    case CallStep call:
                        Inside(call.Then, reason);
                        Inside(call.OnError, reason);
                        break;
                    case PublishStep publish:
                        foreach (var handler in publish.Handlers.OrderBy(h => h.Key.Value))
                            Inside(handler.Value, reason);
                        break;
                    case ParallelStep parallel:
                        foreach (var branch in parallel.Branches)
                            Inside(branch, reason);
                        break;
                    case BranchStep branch:
                        Inside(branch.Then, reason);
                        Inside(branch.Else, reason);
                        break;
                }
            }
        }

        // Null when the script drives the call, else why it does not.
        string? Drive(CallStep call)
        {
            var operation = input.Model.Operations[call.Operation];
            if (operation.Transport != Transport.Http)
                return $"The transport of the operation is {operation.Transport.ToString().ToLowerInvariant()}. k6 sends HTTP requests only.";
            if (ContractDocuments.RouteOf(operation.Name) is not (var method, var template))
                return "The name of the operation is not a method and a path, so the script has no request to send.";

            var outcome = trace.Outcomes.GetValueOrDefault(call.Id, StepOutcome.Skipped);
            if (outcome == StepOutcome.Skipped)
                return "The playback of the scenario skips this step.";
            if (!allowNonIdempotent && NonIdempotentMethods.Contains(method))
                return $"Excluded: {method} is a non-idempotent operation and the target is production-class. The model declares no idempotency for an operation. An owner can set the non-idempotent override on the target.";

            var fixture = call.Request is { ValueKind: JsonValueKind.Object } request ? request : (JsonElement?)null;
            string? missing = null;
            var path = Placeholder().Replace(template, match =>
            {
                var name = match.Groups[1].Value;
                if (fixture is { } members && members.TryGetProperty(name, out var value))
                {
                    switch (value.ValueKind)
                    {
                        case JsonValueKind.String: return Uri.EscapeDataString(value.GetString()!);
                        case JsonValueKind.Number: return Uri.EscapeDataString(value.GetRawText());
                        case JsonValueKind.True: return "true";
                        case JsonValueKind.False: return "false";
                    }
                }
                missing ??= name;
                return match.Value;
            });
            if (missing is not null)
                return $"The path has the placeholder {{{missing}}} and the request fixture of the step has no text, number or boolean member of that name.";

            var hasBody = BodyMethods.Contains(method) && call.Request is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) };
            if (!BodyMethods.Contains(method) && call.Request is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) })
                notes.Add($"Step '{call.Id}': the request fixture is not sent, because a {method} request has no body here. Its members fill the placeholders of the path only.");

            var index = operations.FindIndex(o => o.OperationId == operation.Id.Value);
            if (index < 0)
            {
                index = operations.Count;
                operations.Add(new(index, operation.Id.Value, operation.Name, operation.OwnerId.Value, input.Model.Elements.GetValueOrDefault(operation.OwnerId, "")));
            }
            steps.Add(new(call.Id, index, method, path, hasBody, outcome == StepOutcome.Succeeded ? "success" : "failure",
                operation.TimeoutMs > 0 ? operation.TimeoutMs : DefaultTimeoutMs));
            bodies.Add(hasBody ? Canonical(call.Request!.Value) : null);
            return null;
        }
    }

    private static string Kind(Step step) => step switch
    {
        CallStep => "call", PublishStep => "publish", WriteStep => "write", ParallelStep => "parallel", _ => "branch",
    };

    private static string Canonical(JsonElement value)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
            CanonicalJson.Write(writer, value);
        return Encoding.UTF8.GetString(buffer.GetBuffer().AsSpan(0, (int)buffer.Length));
    }

    private static string N(long value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>A k6 duration: whole seconds when the value has no part of a second, else milliseconds.</summary>
    private static string Duration(double seconds)
    {
        var ms = (long)Math.Round(seconds * 1000);
        return ms % 1000 == 0 ? $"'{N(ms / 1000)}s'" : $"'{N(ms)}ms'";
    }

    /// <param name="Options">The members of the k6 scenario, as JavaScript.</param>
    /// <param name="Signature">The same members in one line. The script builds this line from the options that k6 runs and stops when the two differ.</param>
    private sealed record Load(string Executor, string Options, string Signature, double PlannedIterations, long MinElapsedMs);

    /// <summary>
    /// The arrivals of the profile as a k6 arrival-rate executor. k6 takes whole iterations
    /// for each time unit, so the unit is the smallest of a second, a minute and an hour in
    /// which every rate of the profile is a whole number. A rate that is not 0 and rounds to
    /// no iteration in an hour has no executor: the script would run another load, so there
    /// is no script, with the reason.
    /// </summary>
    private static (Load? Load, string? Refusal) Executor(Arrivals arrivals, List<string> notes)
    {
        var bursts = arrivals.Kind == ArrivalKind.Burst ? arrivals.Bursts ?? [] : [];
        double[] rates = arrivals.Kind switch
        {
            ArrivalKind.Ramp => [arrivals.RatePerSecond, arrivals.ToRatePerSecond ?? arrivals.RatePerSecond],
            ArrivalKind.Burst => [arrivals.RatePerSecond, .. bursts.Select(b => b.RatePerSecond)],
            _ => [arrivals.RatePerSecond],
        };

        var (scale, unit) = new[] { (1, "1s"), (60, "1m"), (3600, "1h") }
            .Where(u => rates.All(r => Math.Abs(r * u.Item1 - Math.Round(r * u.Item1)) < 1e-6))
            .DefaultIfEmpty((3600, "1h")).First();
        long Rate(double perSecond) => (long)Math.Round(perSecond * scale);

        if (rates.Any(r => r > 0 && Rate(r) == 0))
            return (null, "A rate of the profile is more than 0 and below one iteration in two hours. k6 takes whole iterations for each hour, so the script cannot run this rate. Raise the rate in the profile.");
        if (rates.Any(r => Math.Abs(r * scale - Math.Round(r * scale)) >= 1e-6))
            notes.Add("A rate of the profile is not a whole number of iterations in an hour. The script rounds it to the nearest one.");

        // The rate over time as k6 runs it: a straight line from one rate to the next, for some seconds.
        var segments = new List<(double From, double To, double Seconds)>();
        var stages = new List<string>();
        var stageSignature = new List<string>();
        long current = 0;
        void Stage(double rate, double seconds)
        {
            var ms = (long)Math.Round(seconds * 1000);
            stages.Add($"{{ target: {N(Rate(rate))}, duration: {Duration(seconds)} }}");
            stageSignature.Add($"{N(Rate(rate))}:{N(ms)}");
            if (ms > 0)
                segments.Add(((double)current / scale, (double)Rate(rate) / scale, ms / 1000d));
            current = Rate(rate);
        }

        string executor, members, signature;
        var durationMs = (long)Math.Round(arrivals.DurationSeconds * 1000);
        if (arrivals.Kind is ArrivalKind.Constant or ArrivalKind.Poisson)
        {
            if (arrivals.Kind == ArrivalKind.Poisson)
                notes.Add("The profile has Poisson arrivals. k6 starts iterations at a constant rate with the same mean; it does not reproduce the random gaps.");
            executor = "constant-arrival-rate";
            var rate = Rate(arrivals.RatePerSecond);
            segments.Add(((double)rate / scale, (double)rate / scale, durationMs / 1000d));
            members = $"rate: {N(rate)}, timeUnit: '{unit}', duration: {Duration(arrivals.DurationSeconds)}";
            signature = $"rate={N(rate)} unit={N(scale * 1000L)} duration={N(durationMs)}";
        }
        else
        {
            executor = "ramping-arrival-rate";
            long start;
            if (arrivals.Kind == ArrivalKind.Ramp)
            {
                notes.Add("The profile has a ramp of Poisson arrivals. k6 moves the rate in a straight line and starts iterations evenly; it does not reproduce the random gaps.");
                current = start = Rate(arrivals.RatePerSecond);
                Stage(arrivals.ToRatePerSecond ?? arrivals.RatePerSecond, arrivals.DurationSeconds);
            }
            else
            {
                notes.Add("The profile has bursts of Poisson arrivals. k6 changes the rate at the start and at the end of each window and starts iterations evenly; it does not reproduce the random gaps.");
                // A stage of no duration changes the rate at once; the stage after it holds the rate.
                var first = bursts.Count > 0 && bursts[0].FromSeconds == 0 ? bursts[0].RatePerSecond : arrivals.RatePerSecond;
                current = start = Rate(first);
                var (held, at) = (first, 0.0);
                void Hold(double rate, double until)
                {
                    if (until <= at)
                        return;
                    if (rate != held)
                        Stage(rate, 0);
                    Stage(rate, until - at);
                    (held, at) = (rate, until);
                }
                foreach (var window in bursts)
                {
                    Hold(arrivals.RatePerSecond, window.FromSeconds);
                    Hold(window.RatePerSecond, window.ToSeconds);
                }
                Hold(arrivals.RatePerSecond, arrivals.DurationSeconds);
            }
            members = $"startRate: {N(start)}, timeUnit: '{unit}'";
            signature = $"start={N(start)} unit={N(scale * 1000L)}";
        }

        var planned = segments.Sum(s => (s.From + s.To) / 2 * s.Seconds);
        if (planned < 1)
            return (null, $"The profile starts fewer than one iteration in its duration ({planned.ToString("0.###", CultureInfo.InvariantCulture)}). A run with no iteration measures nothing. Raise the rate or the duration in the profile.");

        var peak = segments.Max(s => Math.Max(s.From, s.To));
        var preAllocated = (int)Math.Clamp(Math.Ceiling(peak), 1, MaxPreAllocatedVus);
        var most = (int)Math.Clamp(Math.Ceiling(peak * 10), preAllocated, MaxVus);
        notes.Add($"k6 starts with {N(preAllocated)} virtual users and adds up to {N(most)}. When the target answers too slowly for the rate, k6 drops iterations, and the result states how many.");

        var vus = $"preAllocatedVUs: {N(preAllocated)}, maxVUs: {N(most)}";
        var options = $"executor: '{executor}', {members}, {vus}" + (stages.Count == 0 ? "" : $", stages: [{string.Join(", ", stages)}]");
        signature = $"{executor} {signature} pre={N(preAllocated)} max={N(most)}" + (stages.Count == 0 ? "" : $" stages={string.Join(",", stageSignature)}");
        var minElapsedMs = (long)Math.Floor(SecondsToStart(segments, planned - K6Load.Tolerance(planned)) * 1000);
        return (new Load(executor, options, signature, planned, minElapsedMs), null);
    }

    /// <summary>The seconds after which the rate has started <paramref name="iterations"/> iterations.</summary>
    private static double SecondsToStart(IReadOnlyList<(double From, double To, double Seconds)> segments, double iterations)
    {
        var (at, left) = (0.0, iterations);
        foreach (var (from, to, seconds) in segments)
        {
            if (left <= 0)
                break;
            var inSegment = (from + to) / 2 * seconds;
            if (inSegment < left)
            {
                (at, left) = (at + seconds, left - inSegment);
                continue;
            }
            // from * t + slope * t * t / 2 = left, for the t inside this segment.
            var slope = (to - from) / seconds;
            return at + (Math.Abs(slope) < 1e-12 ? left / from : (Math.Sqrt(from * from + 2 * slope * left) - from) / slope);
        }
        return at;
    }

    private static (string Before, string After) Render(
        K6Input input, TargetAuthorisation authorisation, K6Plan plan, IReadOnlyList<string?> bodies, string executorOptions,
        bool allowNonIdempotent, bool allowFailureInjection)
    {
        var target = input.Target;
        var text = new StringBuilder();
        void Line(string line = "") => text.Append(line).Append('\n');
        void Note(string line = "") => Line(JsText.Comment(line));
        static string At(DateTimeOffset at) => at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

        Note($"Vellum k6 load test, generated by Vellum ({Version}).");
        Note("Do not edit this file. Vellum accepts a result only for the script that it generated.");
        Note();
        Note($"Scenario: {JsText.Literal(input.Scenario.Name)} {input.Scenario.Id.Value}, version {N(input.ScenarioVersion)}");
        Note($"Model:    stream {input.ModelStreamId}, version {N(input.ModelVersion)}");
        Note($"Profile:  {input.ProfileId}, version {N(input.ProfileVersion)}");
        Note($"Target:   {JsText.Literal(target.Name!)} {input.TargetId}, class {target.Environment}");
        Note($"          base URL {JsText.Literal(authorisation.BaseUrl)}");
        Note($"          authorised by user {JsText.Literal(authorisation.By)} at {At(authorisation.At)}");
        Note();
        Note("The script sends requests to the authorised base URL only. It stops in setup() when");
        Note("VELLUM_BASE_URL is another URL. It follows no redirect, so no request and no credential");
        Note("goes to another URL: a 3xx status counts as an unexpected status.");
        Note("The script runs the load of the profile only. It stops in setup() when an option of the");
        Note("command line or of the environment replaces the executor, for example --vus or --duration.");
        Note("Vellum itself sends no load: you run this file with k6.");
        Note();
        if (!target.IsProduction)
        {
            Note($"Production rules: the target is of class {target.Environment}, so no exclusion applies.");
        }
        else
        {
            Note("Production rules: the target is production-class.");
            Note($"  Non-idempotent operations (POST, PUT, PATCH, DELETE): {(allowNonIdempotent ? "ALLOWED by override" : "excluded")}.");
            Note($"  Failure injection (a scenario with faults): {(allowFailureInjection ? "ALLOWED by override" : "excluded")}.");
            if (target.Override is { } set)
            {
                Note($"  Override set by user {JsText.Literal(set.By)} at {At(set.At)}.");
                Note($"  Reason: {JsText.Literal(set.Reason)}");
            }
            else
            {
                Note("  No override is set.");
            }
        }
        Note();
        Note("Driven steps, in this order:");
        for (var i = 0; i < plan.Steps.Count; i++)
        {
            var step = plan.Steps[i];
            var operation = plan.Operations[step.Operation];
            Note($"  {N(i + 1)}. step {JsText.Literal(step.StepId)}: {step.Method} {JsText.Literal(step.Path)}"
                 + $" (operation {JsText.Literal(operation.Name)} {operation.OperationId}),"
                 + (step.Expect == "success" ? " expects a 2xx status" : " expects a status that is not 2xx and not 3xx, as the playback gives with the faults"));
        }
        Note();
        if (plan.NotCovered.Count == 0)
        {
            Note("Not covered: nothing. The script drives every step of the scenario.");
        }
        else
        {
            Note("Not covered. The script does not perform these steps, and its result says nothing about them:");
            foreach (var skipped in plan.NotCovered)
                Note($"  - step {JsText.Literal(skipped.StepId)} ({skipped.Kind}): {skipped.Reason}");
        }
        if (plan.Faults > 0)
        {
            Note();
            Note($"Faults: the scenario declares {N(plan.Faults)} fault(s). The script does not inject them.");
            Note("Inject them in the target system before the run. Without them the checks of this script fail.");
        }
        Note();
        Note("Notes:");
        foreach (var note in plan.Notes)
            Note($"  - {note}");
        Note("  - Outages and policy overrides of the profile are inputs of the simulator. The script does not apply them.");
        Note("  - A request duration is the time from the request to the response as k6 sees it, with the network.");
        Note("    It is not the pure service time of the operation.");
        Note("  - Each request uses the request fixture of its step. A value from one response is not used in a later request.");
        Note();
        Note("Run:");
        Note("  k6 run -e VELLUM_BASE_URL=<the authorised base URL> <this file>");
        Note("Environment:");
        Note("  VELLUM_BASE_URL       required. The base URL of the authorised target.");
        Note("  VELLUM_AUTHORIZATION  optional. The value of the Authorization header, for example 'Bearer <token>'.");
        Note("  VELLUM_HEADERS        optional. A JSON object of more request headers, for example an API key.");
        Note("  VELLUM_RESULT_FILE    optional. Where the result is written. Default: vellum-k6-result.json.");
        Note($"The result file has the format {ResultFormat}. Import it in Vellum: Stress, Measure, Import results.");
        Line();
        Line("import http from 'k6/http';");
        Line("import { check } from 'k6';");
        Line("import crypto from 'k6/crypto';");
        Line("import exec from 'k6/execution';");
        Line("import { Counter, Gauge, Rate, Trend } from 'k6/metrics';");
        Line();
        var before = text.ToString();
        text.Clear();

        Line("const IDENTITY = {");
        Line($"  format: '{ResultFormat}',");
        Line($"  generator: '{Version}',");
        Line($"  scenarioId: '{input.Scenario.Id.Value}',");
        Line($"  scenarioVersion: {N(input.ScenarioVersion)},");
        Line($"  modelStreamId: '{input.ModelStreamId}',");
        Line($"  modelVersion: {N(input.ModelVersion)},");
        Line($"  targetId: '{input.TargetId}',");
        Line($"  environment: '{target.Environment}',");
        Line($"  baseUrlHash: '{TargetRules.UrlHash(authorisation.BaseUrl)}',");
        Line("};");
        Line($"const WARM_UP_MS = {N(plan.WarmUpMs)};");
        Line($"const LOAD = '{plan.Load}';");
        Line($"const PLANNED_DURATION_MS = {N(plan.DurationMs)};");
        Line($"const PLANNED_ITERATIONS = {plan.PlannedIterations!.Value.ToString("R", CultureInfo.InvariantCulture)};");
        Line();
        Line("// Every text below comes from the model. Each one is a string literal: none is code.");
        Line("const OPERATIONS = [");
        foreach (var operation in plan.Operations)
            Line($"  {{ id: '{operation.OperationId}', name: {JsText.Literal(operation.Name)} }},");
        Line("];");
        Line("const STEPS = [");
        for (var i = 0; i < plan.Steps.Count; i++)
        {
            var step = plan.Steps[i];
            Line($"  {{ id: {JsText.Literal(step.StepId)}, operation: {N(step.Operation)}, method: '{step.Method}', path: {JsText.Literal(step.Path)},"
                 + $" body: {(bodies[i] is { } body ? JsText.Literal(body) : "null")}, expectSuccess: {(step.Expect == "success" ? "true" : "false")}, timeoutMs: {N(step.TimeoutMs)} }},");
        }
        Line("];");
        Line();
        Line("export const options = {");
        Line("  scenarios: {");
        Line($"    vellum: {{ {executorOptions} }},");
        Line("  },");
        Line("  summaryTrendStats: ['min', 'avg', 'med', 'p(90)', 'p(95)', 'p(99)', 'max', 'count'],");
        Line("};");
        Line();
        Line(Body);
        return (before, text.ToString());
    }

    /// <summary>
    /// The part of every script that holds no model text. Its line ends are line feeds
    /// whatever the line ends of this source file are, so the hash of a script does not
    /// depend on how the file was checked out.
    /// </summary>
    private static readonly string Body = BodySource.ReplaceLineEndings("\n");

    private const string BodySource = """
        const BASE_URL = String(__ENV.VELLUM_BASE_URL || '').replace(/\/+$/, '');
        const ALL_EXPECT_SUCCESS = STEPS.every((step) => step.expectSuccess);

        // The name of this metric carries the identity of the script into a k6 summary export.
        const identity = new Counter('vellum_script_' + SCRIPT_HASH);
        const elapsed = new Gauge('vellum_run_elapsed_ms');
        const flowDuration = new Trend('vellum_flow_duration', true);
        const flowUnexpected = new Rate('vellum_flow_unexpected');
        const operationDuration = OPERATIONS.map((_, i) => new Trend('vellum_op' + i + '_duration', true));
        const operationFailureDuration = OPERATIONS.map((_, i) => new Trend('vellum_op' + i + '_expected_failure_duration', true));
        const operationUnexpected = OPERATIONS.map((_, i) => new Rate('vellum_op' + i + '_unexpected'));

        // Text that goes to a terminal or into an error: printable ASCII only.
        function printable(text) {
          return String(text).replace(/[^\x20-\x7e]/g, '?');
        }

        // A k6 duration in milliseconds. k6 states the options it runs as '1m0s', '1.5s' or '250ms'.
        function milliseconds(duration) {
          if (typeof duration === 'number') return Math.round(duration);
          const factor = { h: 3600000, m: 60000, s: 1000, ms: 1, us: 0.001, '\u00b5s': 0.001, ns: 0.000001 };
          let total = 0;
          String(duration).replace(/([0-9]*\.?[0-9]+)(ms|us|\u00b5s|ns|h|m|s)/g, (_, value, unit) => {
            total += Number(value) * factor[unit];
            return '';
          });
          return Math.round(total);
        }

        // The load that k6 runs now, in the form of LOAD. An option of the command line replaces the scenarios of a script.
        function effectiveLoad() {
          const scenarios = exec.test.options.scenarios || {};
          const names = Object.keys(scenarios);
          if (names.length !== 1 || names[0] !== 'vellum') return 'scenarios=' + names.join(',');
          const s = scenarios.vellum;
          const vus = ' pre=' + s.preAllocatedVUs + ' max=' + s.maxVUs;
          if (s.executor === 'constant-arrival-rate') {
            return s.executor + ' rate=' + s.rate + ' unit=' + milliseconds(s.timeUnit) + ' duration=' + milliseconds(s.duration) + vus;
          }
          if (s.executor === 'ramping-arrival-rate') {
            const stages = (s.stages || []).map((stage) => stage.target + ':' + milliseconds(stage.duration));
            return s.executor + ' start=' + s.startRate + ' unit=' + milliseconds(s.timeUnit) + vus + ' stages=' + stages.join(',');
          }
          return String(s.executor);
        }

        function requestHeaders() {
          const headers = { 'Content-Type': 'application/json' };
          if (__ENV.VELLUM_HEADERS) {
            const more = JSON.parse(__ENV.VELLUM_HEADERS);
            for (const name of Object.keys(more)) headers[name] = String(more[name]);
          }
          if (__ENV.VELLUM_AUTHORIZATION) headers['Authorization'] = __ENV.VELLUM_AUTHORIZATION;
          return headers;
        }

        export function setup() {
          if (BASE_URL === '') {
            throw new Error('Set VELLUM_BASE_URL to the base URL of the authorised target.');
          }
          if (crypto.sha256(BASE_URL, 'hex') !== IDENTITY.baseUrlHash) {
            throw new Error('VELLUM_BASE_URL is not the base URL that this script is authorised for. Generate a script for the other target in Vellum.');
          }
          const load = effectiveLoad();
          if (load !== LOAD) {
            throw new Error('k6 does not run the load that this script was generated for, so the run stops. '
              + 'Run the script with no option that sets virtual users, a duration, iterations, stages or scenarios. '
              + 'Generated: ' + LOAD + '. Effective: ' + printable(load) + '.');
          }
          return { startedAtMs: Date.now(), load: load };
        }

        export default function (data) {
          // An iteration that starts in the warm-up sends its requests and adds nothing to the result.
          const measured = Date.now() - data.startedAtMs >= WARM_UP_MS;
          const headers = requestHeaders();
          let completed = true;
          let total = 0;
          for (const step of STEPS) {
            // No redirect is followed: a redirect can lead off the authorised base URL, and the headers would go with it.
            const response = http.request(step.method, BASE_URL + step.path, step.body, {
              headers: headers,
              timeout: step.timeoutMs,
              redirects: 0,
              tags: {
                vellum_scenario: IDENTITY.scenarioId,
                vellum_operation: OPERATIONS[step.operation].id,
                vellum_step: step.id,
              },
            });
            const success = response.status >= 200 && response.status < 300;
            const redirect = response.status >= 300 && response.status < 400;
            const met = !redirect && (step.expectSuccess ? success : !success);
            check(response, { 'vellum: the status is the expected one': () => met }, { vellum_operation: OPERATIONS[step.operation].id });
            if (measured) {
              operationUnexpected[step.operation].add(!met);
              // The duration of a failure that the scenario expects is not the latency of a success.
              if (met && step.expectSuccess) operationDuration[step.operation].add(response.timings.duration);
              if (met && !step.expectSuccess) operationFailureDuration[step.operation].add(response.timings.duration);
            }
            if (!met) {
              // A flow ends at its first status that is not the expected one.
              completed = false;
              break;
            }
            total += response.timings.duration;
          }
          if (measured) {
            flowUnexpected.add(!completed);
            if (completed && ALL_EXPECT_SUCCESS) flowDuration.add(total);
          }
          identity.add(1);
          elapsed.add(Date.now() - data.startedAtMs);
        }

        export function teardown(data) {
          // The executor has ended: this is the length of the run.
          elapsed.add(Date.now() - data.startedAtMs);
        }

        function latency(data, name) {
          const metric = data.metrics[name];
          if (!metric || !metric.values || !(metric.values.count > 0)) return null;
          const v = metric.values;
          return { count: v.count, min: v.min, mean: v.avg, p50: v.med, p90: v['p(90)'], p95: v['p(95)'], p99: v['p(99)'], max: v.max };
        }

        // A Rate metric counts the true values as passes. Here a true value is a status that is not the expected one.
        function unexpected(data, name) {
          const metric = data.metrics[name];
          if (!metric || !metric.values) return { total: 0, unexpected: 0 };
          return { total: metric.values.passes + metric.values.fails, unexpected: metric.values.passes };
        }

        function count(data, name) {
          const metric = data.metrics[name];
          return metric && metric.values && metric.values.count ? metric.values.count : 0;
        }

        export function handleSummary(data) {
          // setup() stopped the run, or k6 ran with --no-setup: nothing binds numbers to a start and a load.
          if (!data.setup_data) {
            return { stdout: 'Vellum k6: the run stopped before its first request. No result file is written.\n' };
          }
          const flow = unexpected(data, 'vellum_flow_unexpected');
          const elapsedMetric = data.metrics.vellum_run_elapsed_ms;
          const result = Object.assign({ scriptHash: SCRIPT_HASH }, IDENTITY, {
            startedAt: data.setup_data ? new Date(data.setup_data.startedAtMs).toISOString() : null,
            window: { warmUpMs: WARM_UP_MS, elapsedMs: elapsedMetric && elapsedMetric.values ? elapsedMetric.values.max : 0 },
            load: {
              effective: data.setup_data ? data.setup_data.load : null,
              plannedDurationMs: PLANNED_DURATION_MS,
              plannedIterations: PLANNED_ITERATIONS,
              iterations: count(data, 'iterations'),
              droppedIterations: count(data, 'dropped_iterations'),
            },
            flow: {
              iterations: flow.total,
              unexpectedStatus: flow.unexpected,
              latencyMs: latency(data, 'vellum_flow_duration'),
            },
            operations: OPERATIONS.map((operation, i) => {
              const counted = unexpected(data, 'vellum_op' + i + '_unexpected');
              const failures = latency(data, 'vellum_op' + i + '_expected_failure_duration');
              return {
                operationId: operation.id,
                requests: counted.total,
                unexpectedStatus: counted.unexpected,
                expectedFailures: failures ? failures.count : 0,
                latencyMs: latency(data, 'vellum_op' + i + '_duration'),
                expectedFailureLatencyMs: failures,
              };
            }),
          });

          const file = __ENV.VELLUM_RESULT_FILE || 'vellum-k6-result.json';
          const lines = ['Vellum k6 result (measured), script ' + SCRIPT_HASH.slice(0, 12)];
          lines.push('  flows: ' + result.flow.iterations + ', with an unexpected status: ' + result.flow.unexpectedStatus + ', dropped: ' + result.load.droppedIterations);
          for (let i = 0; i < result.operations.length; i++) {
            const o = result.operations[i];
            const p95 = o.latencyMs ? o.latencyMs.p95.toFixed(2) + ' ms' : 'no value';
            lines.push('  ' + OPERATIONS[i].name + ': ' + o.requests + ' requests, ' + o.unexpectedStatus + ' with an unexpected status, p95 ' + p95);
          }
          lines.push('  written to ' + file);
          // A name of the model or of the environment must not reach the terminal as an escape sequence.
          const output = { stdout: lines.map(printable).join('\n') + '\n' };
          output[file] = JSON.stringify(result, null, 2) + '\n';
          return output;
        }
        """;
}
