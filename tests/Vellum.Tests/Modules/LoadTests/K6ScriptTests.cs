using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Vellum.Modules.LoadTests;
using Vellum.Modules.Modelling.Model;
using Vellum.Modules.Stress.Engine;
using Vellum.Modules.Trace.Engine;

namespace Vellum.Tests.Modules.LoadTests;

/// <summary>
/// A test that needs the <c>k6</c> program. On a machine of a developer that does not have
/// it, the test is skipped with the reason. In CI (the variable <c>CI</c> is set) it is not
/// skipped: it runs and fails, so a pipeline without k6 cannot pass with these tests unrun.
/// </summary>
public sealed class K6FactAttribute : FactAttribute
{
    public static string? Program { get; } = (Environment.GetEnvironmentVariable("PATH") ?? "")
        .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
        .Select(directory => Path.Combine(directory, OperatingSystem.IsWindows() ? "k6.exe" : "k6"))
        .FirstOrDefault(File.Exists);

    public K6FactAttribute() => Skip = SkipReason(Program, Environment.GetEnvironmentVariable("CI"));

    /// <summary>Why the test is skipped, or null when it runs.</summary>
    public static string? SkipReason(string? program, string? ci) =>
        program is null && string.IsNullOrEmpty(ci)
            ? "k6 is not on PATH. Install k6 to run this test: it runs the generated script through the real k6."
            : null;

    /// <summary>Runs k6 in <paramref name="directory"/> and returns its exit code with all it wrote.</summary>
    public static async Task<(int ExitCode, string Output, string Error)> RunAsync(string directory, params string[] arguments)
    {
        if (Program is null)
            throw new InvalidOperationException("k6 is not on PATH, and CI is set. Install k6 in the pipeline: these tests run the generated script through the real k6.");
        var start = new ProcessStartInfo(Program) { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        // The test decides what k6 gets: no setting of the developer's shell reaches the run.
        start.Environment["K6_NO_USAGE_REPORT"] = "true";
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, await output, await error);
    }
}

/// <summary>
/// The k6 script generator without a host: the same input gives the same bytes, model text
/// never becomes code, and the production rules decide what a script may hold.
/// </summary>
public partial class K6ScriptTests
{
    private static Guid G(int n) => new($"00000000-0000-0000-0000-{n:D12}");

    private static readonly ElementId Customer = G(1), Api = G(3), Db = G(4), Relay = G(7);
    private static readonly OperationId PlaceOrder = G(10), RelayOutbox = G(11), GetOrder = G(12);
    private static readonly ChannelId OrderEvents = G(20);
    private static readonly MessageId OrderCreated = G(40);
    private static readonly Guid TargetId = G(70), ProfileId = G(71), Stream = G(72);
    private static readonly DateTimeOffset At = new(2026, 10, 13, 9, 0, 0, TimeSpan.Zero);

    private static TraceModel Model(string placeName = "POST /orders", bool reversed = false)
    {
        IReadOnlyDictionary<TKey, TValue> Map<TKey, TValue>(params (TKey Key, TValue Value)[] entries) where TKey : notnull =>
            (reversed ? entries.Reverse() : entries).ToDictionary(e => e.Key, e => e.Value);

        return new TraceModel(
            Map((Customer, "Customer"), (Api, "Orders API"), (Db, "Orders DB"), (Relay, "Outbox Relay")),
            Map((PlaceOrder, new OperationContract(PlaceOrder, Api, placeName, Transport.Http, null, null, 2000, RetryPolicy.None)),
                (GetOrder, new OperationContract(GetOrder, Api, "GET /orders/{orderId}", Transport.Http, null, null, 0, RetryPolicy.None)),
                (RelayOutbox, new OperationContract(RelayOutbox, Relay, "Relay outbox", Transport.Command, null, null, 0, RetryPolicy.None))),
            Map((OrderEvents, new ChannelContract(OrderEvents, "order-events", ChannelKind.Topic))),
            new Dictionary<SubscriptionId, SubscriptionContract>(),
            Map((OrderCreated, new MessageContract(OrderCreated, "OrderCreated", null))),
            new Dictionary<SchemaId, string>());
    }

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement;

    private static CallStep Place(string id = "place", string request = """{"sku":"apple","qty":2}""") => new(
        id, Customer, PlaceOrder, Json(request), Json("""{"orderId":"o-1"}"""),
        [new WriteStep("save-order", Api, Db, "orders", Json("""{"orderId":"o-1"}"""))], []);

    private static CallStep Read() => new("read", Customer, GetOrder, Json("""{"orderId":"o 1/2"}"""), null, [], []);

    private static PublishStep Publish() => new(
        "publish-order-created", Api, OrderEvents, OrderCreated, Json("""{"orderId":"o-1"}"""),
        new Dictionary<string, string>(), new Dictionary<SubscriptionId, IReadOnlyList<Step>>());

    /// <summary>The Orders reference flow: the customer places an order, the API stores it and publishes OrderCreated.</summary>
    private static Scenario Orders(string name = "Place an order", params Step[] steps) =>
        new(G(99), name, steps.Length > 0 ? steps : [Place(), Publish()], [], []);

    private static TargetState Target(string environment = TargetEnvironment.Test, bool authorised = true, TargetOverride? set = null, string name = "Staging") => new(
        G(98), name, "https://orders.test.example", environment,
        authorised ? new TargetAuthorisation("https://orders.test.example", environment, "user-1", At) : null, set, false, At);

    private static Arrivals Constant(double rate = 40, double duration = 60, double warmUp = 5) =>
        new(ArrivalKind.Constant, rate, null, null, duration, warmUp);

    private static K6Input Input(Scenario? scenario = null, TraceModel? model = null, TargetState? target = null, Arrivals? arrivals = null, int profileVersion = 1) =>
        new(scenario ?? Orders(), 3, model ?? Model(), Stream, 41, TargetId, target ?? Target(), ProfileId, profileVersion, arrivals ?? Constant());

    private static K6Script Script(K6Input input)
    {
        var (script, refusal) = K6ScriptGenerator.Generate(input);
        Assert.True(script is not null, refusal?.Message);
        return script!;
    }

    private static K6Refusal Refusal(K6Input input)
    {
        var (script, refusal) = K6ScriptGenerator.Generate(input);
        Assert.Null(script);
        return refusal!;
    }

    [GeneratedRegex("\"((?:[^\"\\\\]|\\\\u[0-9a-f]{4})*)\"")]
    private static partial Regex Literal();

    /// <summary>Reads a string literal as a JavaScript engine does: the only escape the generator writes is \uXXXX.</summary>
    private static string Decode(string literal) =>
        Regex.Replace(literal[1..^1], @"\\u([0-9a-f]{4})", m => ((char)Convert.ToInt32(m.Groups[1].Value, 16)).ToString());

    private static IEnumerable<string> CodeLines(string script) => script.Split('\n').Where(line => !line.StartsWith("//", StringComparison.Ordinal));

    [Fact]
    public void The_same_scenario_revision_target_and_workload_give_the_same_bytes_and_the_hash_is_of_those_bytes()
    {
        var first = Script(Input());
        // The order of a dictionary of the model decides nothing.
        var second = Script(Input(model: Model(reversed: true)));
        Assert.Equal(first.Content, second.Content);
        Assert.Equal(first.Hash, second.Hash);

        // The hash is the SHA-256 of the script with the hash constant set to zeros.
        var hashLine = $"{K6ScriptGenerator.HashLinePrefix}{first.Hash}\";\n";
        Assert.Single(Regex.Matches(first.Content, Regex.Escape(first.Hash)));
        var zeroed = first.Content.Replace(hashLine, $"{K6ScriptGenerator.HashLinePrefix}{new string('0', 64)}\";\n");
        Assert.Equal(first.Hash, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(zeroed))));

        // Every input that pins the script changes the hash.
        var hashes = new[]
        {
            first.Hash,
            Script(Input(profileVersion: 2)).Hash,
            Script(Input(arrivals: Constant(rate: 41))).Hash,
            Script(Input(target: Target(environment: TargetEnvironment.Staging))).Hash,
            Script(Input(scenario: Orders("Place an order", Place(request: """{"sku":"pear","qty":2}"""), Publish()))).Hash,
            Script(Input() with { ScenarioVersion = 4 }).Hash,
            Script(Input() with { ModelVersion = 42 }).Hash,
        };
        Assert.Equal(hashes.Length, hashes.Distinct().Count());

        // The script holds no secret and no clock value: the base URL and the credential come from the environment of k6.
        Assert.Contains("__ENV.VELLUM_BASE_URL", first.Content);
        Assert.Contains("__ENV.VELLUM_AUTHORIZATION", first.Content);
        Assert.Contains("executor: 'constant-arrival-rate', rate: 40, timeUnit: '1s', duration: '60s'", first.Content);
        Assert.Contains("const WARM_UP_MS = 5000;", first.Content);
        Assert.Contains($"vellum_operation: OPERATIONS[step.operation].id", first.Content);
        Assert.Contains("vellum_scenario: IDENTITY.scenarioId", first.Content);
        Assert.Contains("export function handleSummary(data)", first.Content);
    }

    [Fact]
    public void A_step_that_k6_cannot_drive_is_listed_as_not_covered_and_never_dropped()
    {
        var relay = new CallStep("relay", Relay, RelayOutbox, null, null, [Publish()], []);
        var script = Script(Input(Orders("Place an order", Place(), Read(), relay)));

        Assert.Equal(["place", "read"], script.Plan.Steps.Select(s => s.StepId));
        Assert.Equal([("save-order", "write"), ("relay", "call"), ("publish-order-created", "publish")], script.Plan.NotCovered.Select(n => (n.StepId, n.Kind)));
        // Every step of the scenario is either driven or listed.
        foreach (var skipped in script.Plan.NotCovered)
            Assert.Contains($"//   - step \"{skipped.StepId}\" ({skipped.Kind}):", script.Content);

        // The placeholder of the path takes the value of the fixture, percent-encoded; a GET sends no body.
        var read = script.Plan.Steps[1];
        Assert.Equal(("GET", "/orders/o%201%2F2", false, 60_000), (read.Method, read.Path, read.HasBody, read.TimeoutMs));
        Assert.Equal(("POST", "/orders", true, 2000), (script.Plan.Steps[0].Method, script.Plan.Steps[0].Path, script.Plan.Steps[0].HasBody, script.Plan.Steps[0].TimeoutMs));
        Assert.Contains("""body: "{\u0022qty\u0022:2,\u0022sku\u0022:\u0022apple\u0022}", expectSuccess: true, timeoutMs: 2000""", script.Content);
    }

    [Fact]
    public void A_scenario_that_is_not_valid_or_has_no_http_step_gets_no_script_and_a_reason()
    {
        var unknown = new CallStep("ghost", Customer, G(555), null, null, [], []);
        var invalid = Refusal(Input(Orders("Broken", unknown)));
        Assert.Equal("scenario-not-valid", invalid.Code);
        Assert.Contains(invalid.Reasons, r => r.Contains("ghost"));

        var noHttp = Refusal(Input(Orders("Relay only", new CallStep("relay", Relay, RelayOutbox, null, null, [], []), Publish())));
        Assert.Equal("no-http-step", noHttp.Code);
        Assert.Equal(2, noHttp.Reasons.Count);

        // A name that is not a method and a path is no route.
        var unrouted = Refusal(Input(model: Model(placeName: "Place order")));
        Assert.Equal("no-http-step", unrouted.Code);
        Assert.Contains(unrouted.Reasons, r => r.Contains("not a method and a path"));

        // A placeholder with no value in the fixture: the step is not driven, and nothing else is left.
        var noValue = Refusal(Input(Orders("Read", new CallStep("read", Customer, GetOrder, null, null, [], []))));
        Assert.Contains(noValue.Reasons, r => r.Contains("{orderId}"));
    }

    [Fact]
    public void No_script_is_generated_for_a_target_that_no_person_authorised()
    {
        Assert.Equal("target-not-authorised", Refusal(Input(target: Target(authorised: false))).Code);

        // An authorisation for another base URL is not one for this target.
        var moved = Target() with { BaseUrl = "https://orders.prod.example" };
        Assert.Equal("target-not-authorised", Refusal(Input(target: moved)).Code);
        var relabelled = Target() with { Environment = TargetEnvironment.Test, Authorisation = new("https://orders.test.example", TargetEnvironment.Staging, "user-1", At) };
        Assert.Equal("target-not-authorised", Refusal(Input(target: relabelled)).Code);
    }

    [Fact]
    public void A_production_target_excludes_non_idempotent_operations_and_failure_injection_until_a_person_overrides()
    {
        var production = Target(TargetEnvironment.Production);
        var withFault = Orders() with { Faults = [new Fault(OrderEvents.Value, FaultKind.Outage, 1)] };

        // POST is excluded, and nothing else is left.
        var refused = Refusal(Input(target: production));
        Assert.Equal("no-http-step", refused.Code);
        Assert.Contains(refused.Reasons, r => r.Contains("Excluded: POST is a non-idempotent operation"));

        // A GET stays. The header states the exclusion.
        var readOnly = Script(Input(Orders("Place and read", Place(), Read()), target: production));
        Assert.Equal(["read"], readOnly.Plan.Steps.Select(s => s.StepId));
        Assert.Contains(readOnly.Plan.NotCovered, n => n.StepId == "place" && n.Reason.StartsWith("Excluded:"));
        Assert.Contains("//   Non-idempotent operations (POST, PUT, PATCH, DELETE): excluded.", readOnly.Content);
        Assert.Contains("//   Failure injection (a scenario with faults): excluded.", readOnly.Content);
        Assert.Contains("//   No override is set.", readOnly.Content);

        // A scenario with faults gets no script for production at all.
        Assert.Equal("failure-injection-on-production", Refusal(Input(withFault with { Steps = [Place(), Read()] }, target: production)).Code);

        // Each override opens its own exclusion, and the header names who set it and why.
        var allowWrites = Target(TargetEnvironment.Production, set: new TargetOverride(true, false, "Load test window CHG-1", "user-2", At));
        var writes = Script(Input(target: allowWrites));
        Assert.Equal(["place"], writes.Plan.Steps.Select(s => s.StepId));
        Assert.Contains("//   Non-idempotent operations (POST, PUT, PATCH, DELETE): ALLOWED by override.", writes.Content);
        Assert.Contains("//   Failure injection (a scenario with faults): excluded.", writes.Content);
        Assert.Contains("//   Override set by user \"user-2\" at 2026-10-13T09:00:00.000Z.", writes.Content);
        Assert.Contains("//   Reason: \"Load test window CHG-1\"", writes.Content);
        Assert.Equal("failure-injection-on-production", Refusal(Input(withFault, target: allowWrites)).Code);

        var allowBoth = Target(TargetEnvironment.Production, set: new TargetOverride(true, true, "Game day", "user-2", At));
        var faulted = Script(Input(withFault, target: allowBoth));
        Assert.Contains("// Faults: the scenario declares 1 fault(s). The script does not inject them.", faulted.Content);

        // A target of another class has no exclusion, and a scenario with faults is generated with the warning.
        Assert.Contains("the target is of class test, so no exclusion applies", Script(Input()).Content);
        Assert.Contains("// Faults: the scenario declares 1 fault(s)", Script(Input(withFault)).Content);
    }

    [Fact]
    public void Arrivals_of_a_profile_become_the_options_of_a_k6_arrival_rate_executor()
    {
        // Half an iteration each second is 30 each minute: k6 takes whole numbers.
        Assert.Contains("executor: 'constant-arrival-rate', rate: 30, timeUnit: '1m', duration: '90s', preAllocatedVUs: 1, maxVUs: 5",
            Script(Input(arrivals: Constant(rate: 0.5, duration: 90))).Content);
        Assert.Contains("duration: '1500ms'", Script(Input(arrivals: Constant(duration: 1.5, warmUp: 0))).Content);

        var ramp = Script(Input(arrivals: new Arrivals(ArrivalKind.Ramp, 10, 100, null, 120, 0)));
        Assert.Contains("executor: 'ramping-arrival-rate', startRate: 10, timeUnit: '1s', preAllocatedVUs: 100, maxVUs: 1000, stages: [{ target: 100, duration: '120s' }]", ramp.Content);

        var burst = Script(Input(arrivals: new Arrivals(ArrivalKind.Burst, 5, null, [new(10, 20, 50), new(20, 30, 0)], 60, 0)));
        Assert.Contains(
            "startRate: 5, timeUnit: '1s', preAllocatedVUs: 50, maxVUs: 500, stages: [{ target: 5, duration: '10s' }, { target: 50, duration: '0s' }, { target: 50, duration: '10s' }, "
            + "{ target: 0, duration: '0s' }, { target: 0, duration: '10s' }, { target: 5, duration: '0s' }, { target: 5, duration: '30s' }]",
            burst.Content);
        Assert.Contains(burst.Plan.Notes, n => n.Contains("does not reproduce the random gaps"));
    }

    [Fact]
    public void A_rate_that_k6_cannot_run_gets_no_script_and_is_never_raised_to_another_rate()
    {
        // Below one iteration in two hours: the rate rounds to no iteration in an hour.
        var none = Refusal(Input(arrivals: Constant(rate: 0.0001, duration: 3600)));
        Assert.Equal("rate-too-low", none.Code);
        Assert.Contains("below one iteration in two hours", none.Message);
        // One iteration in an hour, for a minute: the run would start no iteration.
        var short_ = Refusal(Input(arrivals: Constant(rate: 0.0003, duration: 60)));
        Assert.Equal("rate-too-low", short_.Code);
        Assert.Contains("fewer than one iteration", short_.Message);
        // A burst window with a rate that is not 0 and rounds to 0.
        Assert.Equal("rate-too-low", Refusal(Input(arrivals: new Arrivals(ArrivalKind.Burst, 5, null, [new(10, 20, 0.0001)], 60, 0))).Code);

        // A rate of 0 inside a ramp or a burst is a rate that k6 runs.
        Assert.Contains("stages: [{ target: 0, duration: '120s' }]", Script(Input(arrivals: new Arrivals(ArrivalKind.Ramp, 10, 0, null, 120, 0))).Content);
        // One iteration in an hour stays one iteration in an hour.
        var slow = Script(Input(arrivals: Constant(rate: 1.0 / 3600, duration: 7200, warmUp: 0)));
        Assert.Contains("executor: 'constant-arrival-rate', rate: 1, timeUnit: '1h', duration: '7200s'", slow.Content);
        Assert.Equal(2, slow.Plan.PlannedIterations);
    }

    [Fact]
    public void The_plan_states_the_load_that_a_result_is_held_to()
    {
        var constant = Script(Input()).Plan;
        Assert.Equal(("constant-arrival-rate rate=40 unit=1000 duration=60000 pre=40 max=400", 2400d, 57_000L), (constant.Load, constant.PlannedIterations, constant.MinElapsedMs));
        Assert.Contains("const LOAD = 'constant-arrival-rate rate=40 unit=1000 duration=60000 pre=40 max=400';", Script(Input()).Content);

        // A ramp from 10 to 100 in 120 seconds starts 6600 iterations; the last 330 may be missing, and the rate reaches 6270 after about 116.6 seconds.
        var ramp = Script(Input(arrivals: new Arrivals(ArrivalKind.Ramp, 10, 100, null, 120, 0))).Plan;
        Assert.Equal(("ramping-arrival-rate start=10 unit=1000 pre=100 max=1000 stages=100:120000", 6600d), (ramp.Load, ramp.PlannedIterations));
        Assert.InRange(ramp.MinElapsedMs!.Value, 116_500, 116_700);
        Assert.Equal(ramp.LoadNotes, ramp.Notes);
        Assert.Contains(ramp.LoadNotes!, n => n.StartsWith("The profile has a ramp of Poisson arrivals"));
    }

    [Fact]
    public void A_k6_test_is_skipped_only_on_a_machine_without_k6_that_is_not_ci()
    {
        Assert.NotNull(K6FactAttribute.SkipReason(null, null));
        Assert.NotNull(K6FactAttribute.SkipReason(null, ""));
        Assert.Null(K6FactAttribute.SkipReason(null, "true"));
        Assert.Null(K6FactAttribute.SkipReason("/usr/bin/k6", null));
    }

    [Fact]
    public void A_measured_service_time_earns_medium_confidence_only_when_its_measurement_is_not_in_doubt()
    {
        var scenario = new Scenario(G(99), "Place", [new CallStep("place", Customer, PlaceOrder, Json("""{"sku":"apple"}"""), null, [], [])], [], []);
        var profile = new StressProfile(
            "Measured", G(99), "1", new Arrivals(ArrivalKind.Constant, 5, null, null, 10, 1),
            [new ResourceAssumption(Api, 4, null)],
            [new ServiceTimeAssumption(PlaceOrder, new Distribution(DistributionKind.Fixed, Ms: 6), AssumptionLabel.Measured, $"k6:{G(500)}")],
            [], [], [], []);

        var counted = StressEngine.Run(Model(), scenario, profile).Result!;
        Assert.Equal("medium", counted.Confidence.Level);
        Assert.All(counted.Assumptions.Where(a => a.Group == "service-time"), a => Assert.False(a.MeasurementStale));
        Assert.All(counted.Assumptions.Where(a => a.Group != "service-time"), a => Assert.Null(a.MeasurementStale));

        var stale = StressEngine.Run(Model(), scenario, profile, doubts: new Dictionary<Guid, MeasurementDoubt> { [PlaceOrder.Value] = new(true, "the measured result is stale.") }).Result!;
        Assert.Equal(("low", "The measurement behind the service time of 'POST /orders' does not count: the measured result is stale."), (stale.Confidence.Level, Assert.Single(stale.Confidence.Reasons)));
        Assert.All(stale.Assumptions.Where(a => a.Group == "service-time"), a => Assert.True(a.MeasurementStale));
        // The doubt is an input: the two results differ, and each one repeats.
        Assert.NotEqual(counted.Hash, stale.Hash);
        Assert.Equal(counted.Hash, StressEngine.Run(Model(), scenario, profile).Result!.Hash);
    }

    private const string HostileName = "\"; throw new Error('x'); //\n</script>\u2028*/ `${__ENV.SECRET}` \\";
    private const string HostilePath = "/orders/\"+__ENV.SECRET+\"/`$x`/*/</script>\\";
    private const string HostileStep = "place\";})();fetch('http://evil');//\r\n\u2029'";

    private static K6Input Hostile()
    {
        var payload = JsonSerializer.SerializeToElement(new Dictionary<string, object>
        {
            ["sku"] = "\"}); throw 1; ({\"", ["note"] = "line\nbreak \u2028 </script> ' ` ${x} \\", ["\"key\\"] = "\ud83d\ude00",
        });
        var place = new CallStep(HostileStep, Customer, PlaceOrder, payload, null, [new WriteStep("w\n*/", Api, Db, "orders", null)], []);
        return Input(new Scenario(G(99), HostileName, [place], [], []), Model(placeName: "POST " + HostilePath),
            Target(name: HostileName, environment: TargetEnvironment.Production, set: new TargetOverride(true, false, HostileName, HostileName, At)));
    }

    [Fact]
    public void Hostile_names_paths_and_payloads_stay_data_in_the_script()
    {
        var script = Script(Hostile());

        // Printable ASCII and line feeds only: no raw quote, line terminator or template can come from the model.
        Assert.All(script.Content, c => Assert.True(c is '\n' or (>= ' ' and <= '~'), $"U+{(int)c:X4}"));
        foreach (var raw in new[] { HostileName, HostilePath, HostileStep, "Error('x')", "fetch('http://evil')", "</script>", "${__ENV.SECRET}", "\"+__ENV.SECRET+\"" })
            Assert.DoesNotContain(raw, script.Content);

        // The code of the script is the code of a script with plain names, apart from the string literals and the numbers.
        static string Shape(string content) => Regex.Replace(Literal().Replace(string.Join('\n', CodeLines(content)), "\"\""), @"[0-9a-f]{64}", "#");
        Assert.Equal(Shape(Script(Input(target: Target(TargetEnvironment.Production, set: new TargetOverride(true, false, "r", "u", At)))).Content), Shape(script.Content));

        // Read back as a JavaScript engine reads them, the literals are the model text, character for character.
        var steps = CodeLines(script.Content).Single(line => line.StartsWith("  { id: ", StringComparison.Ordinal) && line.Contains("method:"));
        var literals = Literal().Matches(steps).Select(m => Decode(m.Value)).ToList();
        Assert.Equal(HostileStep, literals[0]);
        Assert.Equal(HostilePath, literals[1]);
        var body = JsonDocument.Parse(literals[2]).RootElement;
        Assert.Equal("\"}); throw 1; ({\"", body.GetProperty("sku").GetString());
        Assert.Equal("line\nbreak \u2028 </script> ' ` ${x} \\", body.GetProperty("note").GetString());
        Assert.Equal("\ud83d\ude00", body.GetProperty("\"key\\").GetString());
        var operation = CodeLines(script.Content).Single(line => line.Contains("name: \""));
        Assert.Equal("POST " + HostilePath, Decode(Literal().Match(operation).Value));

        // A comment line holds the text of one step only: a line break in a name starts no new line.
        Assert.Contains(script.Content.Split('\n'), line => line.StartsWith("//   - step \"w\\u000a*/\" (write):", StringComparison.Ordinal));

        // What the script prints at the end of a run is printable ASCII: a name of the model reaches no terminal as an escape sequence.
        Assert.Contains("const output = { stdout: lines.map(printable).join('\\n') + '\\n' };", script.Content);
        Assert.Contains("return String(text).replace(/[^\\x20-\\x7e]/g, '?');", script.Content);

        // Half of a surrogate pair has a literal too.
        Assert.Equal("\"\\ud83d\"", JsText.Literal("\ud83d"));
        Assert.Equal("// a\\u2028b", JsText.Comment("a\u2028b"));
    }

    [K6Fact]
    public async Task K6_reads_a_generated_script_and_a_hostile_one_as_valid_scripts_with_the_declared_options()
    {
        var directory = Directory.CreateTempSubdirectory("vellum-k6-").FullName;
        try
        {
            foreach (var (name, input) in new[] { ("orders.js", Input(Orders("Place and read", Place(), Read(), Publish()))), ("hostile.js", Hostile()) })
            {
                var script = Script(input);
                await File.WriteAllTextAsync(Path.Combine(directory, name), script.Content);

                // `k6 inspect` compiles the script and runs its init code. A syntax error, or model text that became code, fails here.
                var (exitCode, output, error) = await K6FactAttribute.RunAsync(directory, "inspect", name);
                Assert.True(exitCode == 0, output + error);
                var options = JsonDocument.Parse(output[output.IndexOf('{')..]).RootElement;
                var executor = options.GetProperty("scenarios").GetProperty("vellum");
                Assert.Equal("constant-arrival-rate", executor.GetProperty("executor").GetString());
                Assert.Equal(40, executor.GetProperty("rate").GetInt32());
                Assert.Equal("1m0s", executor.GetProperty("duration").GetString());
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
