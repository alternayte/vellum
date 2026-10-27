using System.Text.Json;
using Vellum.Modules.Modelling;
using Vellum.Modules.Stress.Engine;
using Vellum.Modules.Trace.Engine;

namespace Vellum.Tests.Modules.Stress;

/// <summary>
/// The stress engine without a host. A result is a pure function of its inputs, so a test
/// that passes once passes on every machine: a tolerance here bounds the distance between
/// one seeded run and a closed-form answer, not a flaky margin.
/// </summary>
public class StressEngineTests
{
    private static Guid G(int n) => new($"00000000-0000-0000-0000-{n:D12}");

    private static readonly ElementId Client = G(1), Server = G(2), Store = G(3), Other = G(4), Billing = G(5), Inventory = G(6);
    private static readonly OperationId Serve = G(10), Slow = G(11);
    private static readonly ChannelId Exchange = G(20), BillingQueue = G(21), DeadLetters = G(22);
    private static readonly SubscriptionId BillingSub = G(30), InventorySub = G(31);
    private static readonly BindingId ToBilling = G(35);
    private static readonly MessageId Created = G(40);

    private static TraceModel Model(int timeoutMs = 0, RetryPolicy? retry = null, bool reversed = false)
    {
        IReadOnlyDictionary<TKey, TValue> Map<TKey, TValue>(params (TKey Key, TValue Value)[] entries) where TKey : notnull =>
            (reversed ? entries.Reverse() : entries).ToDictionary(e => e.Key, e => e.Value);

        return new TraceModel(
            Map((Client, "Client"), (Server, "Server"), (Store, "Store"), (Other, "Other"), (Billing, "Billing"), (Inventory, "Inventory")),
            Map((Serve, new OperationContract(Serve, Server, "Serve", Transport.Http, null, null, timeoutMs, retry ?? RetryPolicy.None)),
                (Slow, new OperationContract(Slow, Other, "Slow", Transport.Http, null, null, 0, RetryPolicy.None))),
            Map((Exchange, new ChannelContract(Exchange, "orders", ChannelKind.Exchange)),
                (BillingQueue, new ChannelContract(BillingQueue, "billing", ChannelKind.Queue)),
                (DeadLetters, new ChannelContract(DeadLetters, "billing.dlq", ChannelKind.Queue))),
            Map((BillingSub, new SubscriptionContract(BillingSub, BillingQueue, Billing, new RetryPolicy(2, 50), DeadLetters)),
                (InventorySub, new SubscriptionContract(InventorySub, Exchange, Inventory, RetryPolicy.None, null))),
            Map((Created, new MessageContract(Created, "OrderCreated", null))),
            new Dictionary<SchemaId, string>())
        {
            Bindings = Map((ToBilling, new BindingContract(ToBilling, Exchange, BillingQueue, null))),
        };
    }

    private static Scenario Flow(params Step[] steps) => new(G(99), "Flow", steps, [], []);

    private static CallStep CallServe(params Step[] then) => new("serve", Client, Serve, null, null, then, []);

    private static Arrivals Poisson(double rate, double duration, double warmUp = 0) =>
        new(ArrivalKind.Poisson, rate, null, null, duration, warmUp);

    private static Arrivals Constant(double rate, double duration) => new(ArrivalKind.Constant, rate, null, null, duration, 0);

    private static Distribution Exponential(double meanMs) => new(DistributionKind.Exponential, MeanMs: meanMs);
    private static Distribution Fixed(double ms) => new(DistributionKind.Fixed, Ms: ms);

    private static StressProfile Profile(
        Arrivals arrivals, Distribution serve, int workers = 1, int? queue = null, string seed = "1",
        IReadOnlyList<Outage>? outages = null) => new(
        "Profile", G(99), seed, arrivals,
        [new ResourceAssumption(Server, workers, queue)],
        [new ServiceTimeAssumption(Serve, serve)],
        [], [], [], outages ?? []);

    private static StressResult Run(TraceModel model, Scenario scenario, StressProfile profile, StressLimits? limits = null)
    {
        var outcome = StressEngine.Run(model, scenario, profile, limits);
        Assert.True(outcome.Result is not null, string.Join("; ", outcome.Issues.Select(i => $"{i.Code} {i.Target}: {i.Message}")));
        return outcome.Result!;
    }

    private static StressIssue Refused(TraceModel model, Scenario scenario, StressProfile profile, StressLimits? limits = null, CancellationToken ct = default)
    {
        var outcome = StressEngine.Run(model, scenario, profile, limits, ct);
        Assert.Null(outcome.Result);
        return Assert.Single(outcome.Issues);
    }

    private static void Near(double expected, double? actual, double tolerance) =>
        Assert.True(actual is { } value && Math.Abs(value - expected) <= tolerance, $"expected {expected} ± {tolerance}, got {actual}");

    // ---- Random source ----

    [Fact]
    public void SplitMix64_gives_the_reference_values()
    {
        // The first five outputs of the reference splitmix64.c for the seed 1234567.
        var mix = new SplitMix64(1234567);
        Assert.Equal(
            [6457827717110365317UL, 3203168211198807973UL, 9817491932198370423UL, 4593380528125082431UL, 16408922859458223821UL],
            Enumerable.Range(0, 5).Select(_ => mix.Next()).ToArray());
    }

    [Fact]
    public void Xoshiro256_star_star_gives_the_reference_values()
    {
        // The outputs of the reference xoshiro256starstar.c for the state { 1, 2, 3, 4 }.
        var random = new Xoshiro256(1, 2, 3, 4);
        Assert.Equal(
            [
                11520UL, 0UL, 1509978240UL, 1215971899390074240UL, 1216172134540287360UL,
                607988272756665600UL, 16172922978634559625UL, 8476171486693032832UL,
                10595114339597558777UL, 2904607092377533576UL,
            ],
            Enumerable.Range(0, 10).Select(_ => random.Next()).ToArray());
    }

    [Fact]
    public void The_logarithm_and_the_exponential_agree_with_the_platform_to_a_few_units_in_the_last_place()
    {
        // The engine's own functions exist so that every platform gives the same bits. They
        // must still be the functions they claim to be: 4 parts in 10^16 is two units in the last place.
        foreach (var x in new[] { 1e-300, 1e-9, 0.1, 0.5, 0.999999, 1.0, 1.0000001, 1.5, 2.0, Math.E, 10.0, 12345.678, 1e300 })
            Assert.True(Math.Abs(StressMath.Log(x) - Math.Log(x)) <= 4e-16 * Math.Max(1, Math.Abs(Math.Log(x))), $"ln({x})");
        foreach (var x in new[] { -700.0, -20.0, -1.0, -1e-9, 0.0, 1e-9, 0.5, 1.0, 10.0, 700.0 })
            Assert.True(Math.Abs(StressMath.Exp(x) - Math.Exp(x)) <= 4e-16 * Math.Exp(x), $"exp({x})");
    }

    // ---- Reference cases with closed-form answers ----

    [Theory]
    // Mean wait in queue of M/M/1 is rho / (mu - lambda); mu is 100 per second.
    // The standard error of the mean wait over n customers is sqrt(v / n) with
    // v = rho (2 + 5 rho - 4 rho^2 + rho^3) / (mu^2 (1 - rho)^4) (Daley 1968). For rho 0.5 and
    // 95 000 customers that is 0.18 ms; for rho 0.8 and 152 000 customers it is 1.1 ms. The
    // tolerances are about three standard errors.
    [InlineData(50, 10.0, 0.5, 0.6)]
    [InlineData(80, 40.0, 0.8, 3.5)]
    public void M_M_1_has_the_mean_wait_and_the_utilisation_of_the_formula(double lambda, double waitMs, double rho, double toleranceMs)
    {
        var result = Run(Model(), Flow(CallServe()), Profile(Poisson(lambda, 2000, 100), Exponential(10)));
        var server = Assert.Single(result.Elements);

        Near(waitMs, server.Wait.MeanMs, toleranceMs);
        // Utilisation is a time average over 1 900 s of a process that changes every 10 ms: 0.01 is far outside its error.
        Near(rho, server.Utilisation, 0.01);
        Near(lambda, result.Flow.ThroughputPerSecond, lambda * 0.02);
        // Sojourn time is wait plus one service time.
        Near(waitMs + 10, result.Flow.Latency.MeanMs, toleranceMs + 0.2);
        Assert.False(result.Bottleneck.Found);
    }

    [Fact]
    public void M_M_c_has_the_waiting_probability_of_Erlang_C()
    {
        // c = 3, mu = 100 per second for each worker, lambda = 240: offered load a = 2.4, rho = 0.8.
        // Erlang C: P(wait) = (a^3/3! / (1 - rho)) / (1 + a + a^2/2 + a^3/3! / (1 - rho)) = 11.52 / 17.80 = 0.6472.
        // The share of 137 000 customers that waited has a standard error near 0.006 (the indicator is
        // correlated over a busy period), so 0.02 is three standard errors.
        var result = Run(Model(), Flow(CallServe()), Profile(Poisson(240, 600, 30), Exponential(10), workers: 3));
        var server = Assert.Single(result.Elements);

        Near(0.6472, (double)server.Waited / server.Arrived, 0.02);
        // Mean wait is P(wait) / (c mu - lambda) = 10.79 ms.
        Near(10.79, server.Wait.MeanMs, 1.0);
        Near(0.8, server.Utilisation, 0.01);
    }

    [Fact]
    public void M_D_1_has_the_mean_wait_of_Pollaczek_Khinchine()
    {
        // Fixed service of 10 ms, lambda = 80: rho = 0.8. Mean wait = rho / (2 mu (1 - rho)) = 20 ms,
        // half of M/M/1. The waits vary less than in M/M/1, so 2 ms (10 %) is more than three standard errors.
        var result = Run(Model(), Flow(CallServe()), Profile(Poisson(80, 2000, 100), Fixed(10)));
        var server = Assert.Single(result.Elements);

        Near(20.0, server.Wait.MeanMs, 2.0);
        Near(0.8, server.Utilisation, 0.01);
    }

    [Fact]
    public void D_D_1_gives_exact_numbers()
    {
        // One arrival every 250 ms for 5 s (20 arrivals, at 0 ... 4750 ms), one worker, 300 ms of service.
        // Arrival k starts at 300 k ms and ends at 300 (k + 1) ms: it waits 50 k ms. Nothing is random.
        var result = Run(Model(), Flow(CallServe()), Profile(Constant(4, 5), Fixed(300)));
        var server = Assert.Single(result.Elements);

        Assert.Equal(20, result.Flow.Arrivals);
        Assert.Equal(new Counts(20, 0, 0, 0, 0, 0), result.Flow.Counts);
        // Sorted latencies are 300, 350, ..., 1250. Nearest rank: p50 is rank 10, p90 rank 18, p95 rank 19, p99 rank 20.
        Assert.Equal((300.0, 775.0, 750.0, 1150.0, 1200.0, 1250.0, 1250.0),
            (result.Flow.Latency.MinMs!.Value, result.Flow.Latency.MeanMs!.Value, result.Flow.Latency.P50Ms!.Value,
             result.Flow.Latency.P90Ms!.Value, result.Flow.Latency.P95Ms!.Value, result.Flow.Latency.P99Ms!.Value, result.Flow.Latency.MaxMs!.Value));
        Assert.Equal((0.0, 475.0, 950.0), (server.Wait.MinMs!.Value, server.Wait.MeanMs!.Value, server.Wait.MaxMs!.Value));
        Assert.Equal(19, server.Waited);
        // The worker is busy from 0 to 6 000 ms: utilisation over [0, 5 000) is 1.
        Assert.Equal(1.0, server.Utilisation);
        // Completions before 5 000 ms: 300 (k + 1) < 5 000 gives 16. 16 / 5 s.
        Assert.Equal(3.2, result.Flow.ThroughputPerSecond);
        // At 5 000 ms 20 have arrived and 17 have started: 3 wait.
        Assert.Equal(3, server.Queue.DepthAtEndOfArrivals);
        Assert.True(result.Drain.Drained);
        Assert.Equal(1000.0, result.Drain.TimeToDrainMs);
        Assert.Equal(0, result.Drain.LeftInSystem);
        Assert.True(result.Bottleneck.Found);
        Assert.Equal(Server.Value, result.Bottleneck.Id);
    }

    [Fact]
    public void M_M_1_K_rejects_the_share_of_the_formula()
    {
        // One worker and 4 waiting places: K = 5 in the system. lambda = 90, mu = 100, rho = 0.9.
        // P(blocked) = (1 - rho) rho^K / (1 - rho^(K + 1)) = 0.1 * 0.59049 / 0.468559 = 0.1260.
        // Over 90 000 arrivals the standard error is near 0.002; 0.01 is far outside it.
        var result = Run(Model(), Flow(CallServe()), Profile(Poisson(90, 1000), Exponential(10), queue: 4));
        var server = Assert.Single(result.Elements);

        Near(0.1260, (double)server.Counts.Rejected / server.Arrived, 0.01);
        Assert.Equal(server.Counts.Rejected, result.Flow.Counts.Failed);
        Assert.True(server.Queue.MaxDepth <= 4);
        // The server turned work away, so it is saturated. Its queue is full and does not grow: the two statements are apart.
        Assert.True(result.Bottleneck.Found);
        Assert.Equal(Server.Value, result.Bottleneck.Id);
        Assert.True(server.Saturation!.TurnedAway >= server.Counts.Rejected && server.Counts.Rejected > 0);
        Assert.False(server.Saturation.QueueGrows);
    }

    [Fact]
    public void A_saturated_server_has_a_backlog_that_grows_in_a_line_and_is_the_bottleneck()
    {
        // lambda = 120, mu = 100: the queue grows by 20 per second, to about 2 000 after 100 s.
        // The depth at the end is a difference of two Poisson-like counts with variance near
        // (lambda + mu) t = 22 000, a deviation of 150. 15 % of 2 000 is two deviations.
        var result = Run(Model(), Flow(CallServe()), Profile(Poisson(120, 100), Exponential(10)));
        var server = Assert.Single(result.Elements);

        Near(2000, server.Queue.DepthAtEndOfArrivals, 300);
        Assert.True(server.Utilisation > 0.99, $"utilisation {server.Utilisation}");
        Assert.True(result.Bottleneck.Found);
        Assert.Equal(Server.Value, result.Bottleneck.Id);
        Assert.Equal("element", result.Bottleneck.Kind);
        Assert.Equal([Server.Value], result.Bottleneck.Saturated);
        // The growth statement: 1 000 more wait at 100 s than at 50 s. The difference of the two depths has
        // a deviation near sqrt(220 * 50) = 105, so the rate has one near 2 per second.
        var saturation = server.Saturation!;
        Assert.True(saturation.QueueGrows);
        Near(20, saturation.GrowthPerSecond, 6);
        Near(1000, saturation.DepthAtMidWindow, 300);
        // One backlog window: work waits from the first moments to the end of the drain, and the peak is at the end of the arrivals.
        var window = Assert.Single(saturation.Windows);
        Assert.True(window.FromMs < 5_000 && window.ToMs > 100_000, $"window {window}");
        Near(2000, window.PeakDepth, 300);
        Assert.Equal(0, saturation.TurnedAway);
        // The quarters of a line through zero are in the ratio 1 : 3 : 5 : 7.
        var quarters = server.Queue.MeanDepthByQuarter;
        Near(7, quarters[3] / quarters[0], 2.5);
        // 2 000 waiting calls at 100 per second take about 20 s to drain.
        Assert.True(result.Drain.Drained);
        Near(20_000, result.Drain.TimeToDrainMs, 3_500);
        Assert.Equal("estimated", result.Bottleneck.Label);
    }

    // ---- The saturation rule ----

    [Fact]
    public void A_burst_that_ends_inside_the_run_is_reported_as_saturation_with_its_window()
    {
        // 50 per second against 100 per second of capacity, and 300 per second from 100 s to 200 s.
        // The queue grows by 200 per second for 100 s, to 20 000 (deviation sqrt(400 * 100) = 200). Then it
        // falls by 50 per second: 10 000 are left when the arrivals stop at 400 s, and the server needs
        // 100 s more for them. The mean utilisation of the window is 0.875: the old rule saw no bottleneck.
        var arrivals = new Arrivals(ArrivalKind.Burst, 50, null, [new BurstWindow(100, 200, 300)], 400, 0);
        var result = Run(Model(), Flow(CallServe()), Profile(arrivals, Exponential(10)));
        var server = Assert.Single(result.Elements);
        var saturation = server.Saturation!;

        Assert.True(result.Bottleneck.Found);
        Assert.Equal(Server.Value, result.Bottleneck.Id);
        Near(0.875, server.Utilisation, 0.02);
        Assert.True(saturation.Saturated);
        var window = Assert.Single(saturation.Windows);
        Near(100_000, window.FromMs, 1_000);
        Near(500_000, window.ToMs, 20_000);
        Near(20_000, window.PeakDepth, 1_000);
        Near(200_000, window.PeakAtMs, 2_000);
        Assert.True(window.PeakDepth >= saturation.NoiseBound && saturation.NoiseBound < 2_000, $"bound {saturation.NoiseBound}");
        Near(10_000, server.Queue.DepthAtEndOfArrivals, 1_000);
        // The backlog shrinks at the end of the arrivals: saturated, and the queue does not grow.
        Assert.False(saturation.QueueGrows);
        Assert.Null(saturation.GrowthPerSecond);
        Assert.Equal(0, saturation.TurnedAway);
    }

    [Fact]
    public void A_full_bounded_queue_that_rejects_half_of_the_work_is_saturated_and_does_not_grow()
    {
        // 200 per second against 100 per second and 10 waiting places: about half of the calls find the queue full.
        var result = Run(Model(), Flow(CallServe()), Profile(Poisson(200, 300, 10), Exponential(10), queue: 10));
        var server = Assert.Single(result.Elements);
        var saturation = server.Saturation!;

        Assert.True(server.Utilisation > 0.99, $"utilisation {server.Utilisation}");
        Near(0.5, (double)server.Counts.Rejected / server.Arrived, 0.02);
        Assert.True(result.Bottleneck.Found);
        Assert.Equal(Server.Value, result.Bottleneck.Id);
        Assert.True(saturation.Saturated);
        Assert.True(saturation.TurnedAway >= server.Counts.Rejected);
        Assert.True(saturation.FirstTurnedAwayMs >= 10_000 && saturation.LastTurnedAwayMs > 299_000, $"{saturation.FirstTurnedAwayMs} to {saturation.LastTurnedAwayMs}");
        Assert.True(server.Queue.MaxDepth <= 10);
        Assert.False(saturation.QueueGrows);
    }

    [Theory]
    // A steady M/M/1 queue has spare capacity: the rule must not name it. The rule has three parts with a
    // chance of at most 1 % each (the upper point of the utilisation, the depth of a steady queue, the depth at
    // full load), so a run names the server with a chance of at most 3 %: 6 of 200 runs on average. The test
    // allows 10 of 200, which is 5 %. The unions and the ratio s are on the safe side, so the measured count is
    // far below it: 0 of 200 in each of the three cases.
    [InlineData(80, 60)]
    [InlineData(90, 60)]
    public void A_steady_M_M_1_queue_is_named_in_at_most_10_of_200_seeds(double lambda, double duration) =>
        SteadyQueueIsRarelyNamed(lambda, duration);

    // The case above near full load, over 600 s: 200 runs of 57 000 arrivals each.
    [Theory]
    [Trait("Category", "Slow")]
    [InlineData(95, 600)]
    public void A_steady_M_M_1_queue_near_full_load_is_named_in_at_most_10_of_200_seeds(double lambda, double duration) =>
        SteadyQueueIsRarelyNamed(lambda, duration);

    private static void SteadyQueueIsRarelyNamed(double lambda, double duration)
    {
        // Each seed is an independent pure run, so the runs can go side by side.
        var named = 0;
        var growing = 0;
        Parallel.For(1, 201, seed =>
        {
            var result = Run(Model(), Flow(CallServe()), Profile(Poisson(lambda, duration), Exponential(10), seed: seed.ToString()));
            if (result.Bottleneck.Found) Interlocked.Increment(ref named);
            if (result.Elements[0].Saturation!.QueueGrows) Interlocked.Increment(ref growing);
        });

        Assert.True(named <= 10, $"named in {named} of 200 seeds");
        // The growth statement needs the same bound, so it is not made more often.
        Assert.True(growing <= named, $"growing in {growing} of 200 seeds");
    }

    [Fact]
    public void The_noise_bound_follows_its_formula()
    {
        // v = 1, u = 0.9, n = 5 400: w = 0.9 (1 + 2.326 sqrt(2 / 5 400)) = 0.940294, s = exp(-0.059706) = 0.942041,
        // n (1 - s) + 1 / (1 - s) = 313.0 + 17.3 = 330.2, allowed = 0.01 / 330.2 = 3.028e-5.
        // (1 - s) s^(d - 1) / (1 - s^d) is 3.134e-5 at d = 127 and 2.953e-5 at d = 128. At full load: 2.576 sqrt(10 800) = 268.
        Assert.Equal(128, StressEngine.NoiseBound(5_400, 0.9, 1, 1_000_000));
        // At a utilisation of 1 only the depth at full load has a value: 2.576 sqrt(40 000) = 515.2.
        Assert.Equal(516, StressEngine.NoiseBound(20_000, 1, 1, 1_000_000));
        // Few arrivals at a low utilisation: w = 0.5 (1 + 2.326 sqrt(2 / 50)) = 0.7326, and the depth of the steady queue, 24,
        // is below the depth at full load, 2.576 sqrt(100) = 25.8.
        Assert.Equal(24, StressEngine.NoiseBound(50, 0.5, 1, 1_000_000));
        // No variation: a queue with spare capacity is empty, and the bound is its least value.
        Assert.Equal(2, StressEngine.NoiseBound(20, 1, 0, 1_000_000));
        // More variation gives a higher bound, and no bound is above the cap.
        Assert.Equal(273, StressEngine.NoiseBound(5_400, 0.9, 2, 1_000_000));
        Assert.Equal(50, StressEngine.NoiseBound(5_400, 0.9, 1, 50));
    }

    // ---- Determinism ----

    private static (TraceModel Model, Scenario Scenario, StressProfile Profile) FanOut(bool reversed = false, string seed = "7")
    {
        var scenario = Flow(
            CallServe(new WriteStep("save", Server, Store, "orders", null)),
            new PublishStep("publish", Server, Exchange, Created, null, new Dictionary<string, string>(),
                new Dictionary<SubscriptionId, IReadOnlyList<Step>>()));
        ResourceAssumption[] resources = [new(Server, 4, 50), new(Store, 2, null)];
        ServiceTimeAssumption[] times =
        [
            new(Serve, new Distribution(DistributionKind.Lognormal, MedianMs: 20, P95Ms: 80)),
            new(Store, new Distribution(DistributionKind.Uniform, MinMs: 2, MaxMs: 12)),
            new(BillingSub, Exponential(30)),
            new(InventorySub, Fixed(5)),
        ];
        SubscriptionAssumption[] subscriptions = [new(BillingSub, 1, null), new(InventorySub, 2, null)];
        if (reversed)
        {
            Array.Reverse(resources);
            Array.Reverse(times);
            Array.Reverse(subscriptions);
        }
        return (Model(timeoutMs: 500, retry: new RetryPolicy(2, 20), reversed), scenario,
            new StressProfile("Fan-out", G(99), seed, Poisson(60, 30, 5), resources, times, subscriptions, [], [], []));
    }

    [Fact]
    public void The_same_inputs_give_the_same_bytes_and_the_same_hash()
    {
        var (model, scenario, profile) = FanOut();
        var first = Run(model, scenario, profile);
        var second = Run(model, scenario, profile);

        Assert.Equal(JsonSerializer.SerializeToUtf8Bytes(first, StressJson.Options), JsonSerializer.SerializeToUtf8Bytes(second, StressJson.Options));
        Assert.Equal(first.Hash, second.Hash);
        Assert.Equal(64, first.Hash.Length);
        Assert.Equal(StressEngine.Version, first.EngineVersion);
        Assert.Equal(first.Hash, StressEngine.Hash(first));
    }

    [Fact]
    public void The_traffic_between_elements_is_the_same_for_the_same_seed_and_names_each_pair_once()
    {
        var (model, scenario, profile) = FanOut();
        var first = Run(model, scenario, profile);
        var second = Run(model, scenario, profile);

        Assert.NotNull(first.Traffic);
        Assert.Equal(
            JsonSerializer.Serialize(first.Traffic, StressJson.Options),
            JsonSerializer.Serialize(second.Traffic, StressJson.Options));
        // The client calls the server, the server writes the store and publishes to the exchange; the exchange and
        // the binding reach Inventory and Billing. Each pair is listed once, in the order of the IDs.
        Assert.Equal(
            [(Client.Value, Server.Value, "call"), (Server.Value, Store.Value, "write"), (Server.Value, Billing.Value, "message"), (Server.Value, Inventory.Value, "message")],
            first.Traffic!.Select(t => (t.FromId, t.ToId, t.Kind)).OrderBy(t => t.Item1).ThenBy(t => t.Item2).ToList());
        Assert.All(first.Traffic!, t => Assert.Equal(StressResult.Estimated, t.Label));
        // Each flow sends one of each, so every pair carries about the 60 flows a second of the profile.
        Assert.All(first.Traffic!, t => Assert.InRange(t.PerSecond, 50, 70));
        Assert.Equal(first.Traffic!.Single(t => t.Kind == "call").Sent / 25.0, first.Traffic!.Single(t => t.Kind == "call").PerSecond, 12);
        // A subscription names the element that consumes it.
        Assert.Equal([Billing.Value, Inventory.Value], first.Subscriptions.Select(s => s.ConsumerId!.Value).Order());
        Assert.All(first.Elements, e => Assert.Null(e.ConsumerId));
    }

    [Fact]
    public void Traffic_counts_every_attempt_and_the_capacity_is_the_rate_at_which_the_first_resource_is_busy_all_the_time()
    {
        // One call each second for 10 s, 300 ms each on one worker: 10 calls, and the worker is busy all the time at 1 / 0.3 flows a second.
        var result = Run(Model(), Flow(CallServe()), Profile(Constant(1, 10), Fixed(300)));
        var call = Assert.Single(result.Traffic!);
        Assert.Equal((Client.Value, Server.Value, "call", 10L, 1.0), (call.FromId, call.ToId, call.Kind, call.Sent, call.PerSecond));
        Assert.Equal((Server.Value, "Server"), (result.Capacity!.Id, result.Capacity.Name));
        Assert.Equal(1 / 0.3, result.Capacity.FlowsPerSecond!.Value, 9);
        Assert.Equal(StressEngine.CapacityRule, result.Capacity.Rule);

        // Down for the whole run: the attempts still leave the caller, and a resource that served nothing has no rate.
        var down = Run(Model(), Flow(CallServe()), Profile(Constant(1, 10), Fixed(300), outages: [new Outage(Server, 0, 10 + StressLimits.Default.MaxDrainSeconds)]));
        Assert.Equal(10, Assert.Single(down.Traffic!).Sent);
        Assert.Equal(10, down.Flow.Counts.Failed);
        Assert.Null(down.Capacity!.FlowsPerSecond);
        Assert.Null(down.Capacity.Id);
    }

    [Fact]
    public void The_order_in_which_the_model_and_the_profile_were_built_does_not_change_the_result()
    {
        var (model, scenario, profile) = FanOut();
        var (reversedModel, _, reversedProfile) = FanOut(reversed: true);

        Assert.Equal(Run(model, scenario, profile).Hash, Run(reversedModel, scenario, reversedProfile).Hash);
    }

    [Fact]
    public void Another_seed_gives_other_samples()
    {
        var (model, scenario, profile) = FanOut();
        var first = Run(model, scenario, profile);
        var second = Run(model, scenario, profile with { Seed = "8" });

        Assert.NotEqual(first.Hash, second.Hash);
        Assert.NotEqual(first.Flow.Latency.MeanMs, second.Flow.Latency.MeanMs);
        Assert.NotEqual(first.Flow.Arrivals, second.Flow.Arrivals);
    }

    // ---- Semantics ----

    [Fact]
    public void A_call_that_passes_its_timeout_fails_at_the_caller_and_the_callee_finishes_wasted_work()
    {
        // One call each second; 300 ms of service against a timeout of 200 ms.
        var result = Run(Model(timeoutMs: 200), Flow(CallServe()), Profile(Constant(1, 10), Fixed(300)));
        var operation = Assert.Single(result.Operations);
        var server = Assert.Single(result.Elements);

        Assert.Equal(new Counts(0, 10, 0, 10, 0, 0), result.Flow.Counts);
        Assert.Equal(new Counts(0, 0, 0, 10, 0, 0), operation.Counts);
        Assert.Equal(10, operation.Wasted);
        Assert.Equal(10, server.WastedCount);
        Assert.Equal(3000.0, server.WastedMs);
        // The server finished ten calls that nobody waited for. None of them is completed work or throughput.
        Assert.Equal(0, server.Counts.Completed);
        Assert.Equal(0.0, server.ThroughputPerSecond);
        Assert.Equal(0, server.FailedInside);
        // The worker did the work: 300 ms of each second.
        Assert.Equal(0.3, server.Utilisation, 12);
        Assert.Equal(200, operation.TimeoutMs);
    }

    [Fact]
    public void A_call_that_fails_inside_holds_a_worker_and_is_not_completed_work()
    {
        // Each call does 50 ms of service and then writes to a store that is down for the whole run.
        var scenario = Flow(CallServe(new WriteStep("save", Server, Store, "orders", null)));
        var profile = new StressProfile("Nested", G(99), "1", Constant(1, 10),
            [new(Server, 1, null), new(Store, 1, null)],
            [new(Serve, Fixed(50)), new(Store, Fixed(150))], [], [], [], [new Outage(Store, 0, 100)]);
        var result = Run(Model(), scenario, profile);
        var server = result.Elements.Single(e => e.Id == Server.Value);

        Assert.Equal(new Counts(0, 10, 0, 0, 0, 0), result.Flow.Counts);
        Assert.Equal(0, server.Counts.Completed);
        Assert.Equal(0.0, server.ThroughputPerSecond);
        Assert.Equal(10, server.FailedInside);
        Assert.Equal(0, server.WastedCount);
        // The worker was busy all the same.
        Assert.Equal(0.05, server.Utilisation, 12);
    }

    [Fact]
    public void The_run_ends_at_the_last_instant_with_open_work_not_at_a_timer_that_found_nothing_to_do()
    {
        // Two calls of 100 ms, at 0 s and at 1 s, with a timeout of one minute. Both timers fire long after the calls ended.
        var result = Run(Model(timeoutMs: 60_000), Flow(CallServe()), Profile(Constant(1, 2), Fixed(100)));

        Assert.Equal(1_100.0, result.Window.EndedAtMs);
        Assert.True(result.Drain.Drained);
        Assert.Equal(0.0, result.Drain.TimeToDrainMs);

        // Work that is left at the drain limit: the run ends at that limit.
        var cut = Run(Model(), Flow(CallServe()), Profile(Constant(10, 5), Fixed(1000)), StressLimits.Default with { MaxDrainSeconds = 1 });
        Assert.Equal(6_000.0, cut.Window.EndedAtMs);
    }

    [Fact]
    public void An_outage_window_turns_retries_into_a_storm()
    {
        // Ten calls each second for 6 s; three attempts 100 ms apart; the server is down from 2 s to 4 s.
        // The 20 flows that start in the window: those at 2.0 ... 3.7 s spend all three attempts inside it
        // and fail (18). The flow at 3.8 s succeeds at its third attempt (4.0 s), the one at 3.9 s at its second.
        var result = Run(
            Model(retry: new RetryPolicy(3, 100)), Flow(CallServe()),
            Profile(Constant(10, 6), Fixed(10), workers: 4, outages: [new Outage(Server, 2, 4)]));
        var operation = Assert.Single(result.Operations);

        Assert.Equal(60, result.Flow.Arrivals);
        Assert.Equal(42, result.Flow.Counts.Completed);
        Assert.Equal(18, result.Flow.Counts.Failed);
        Assert.Equal(18 * 3 + 2 + 1, operation.Counts.Failed);
        Assert.Equal(18 * 2 + 2 + 1, operation.Retries);
        Assert.Equal(60 + 39, operation.Attempts);
        Assert.Equal(18, operation.Exhausted);
        Assert.Contains(result.Assumptions, a => a.Group == "outage" && a.Target == Server.Value);
    }

    [Fact]
    public void Every_assumption_row_has_a_key_of_its_own_also_with_two_outage_windows_on_one_target()
    {
        var (model, scenario, profile) = FanOut();
        var result = Run(model, scenario, profile with
        {
            Outages = [new Outage(Server, 20, 21), new Outage(Server, 2, 4), new Outage(BillingSub, 2, 4), new Outage(Server, 2, 3)],
        });

        Assert.Equal(result.Assumptions.Count, result.Assumptions.Select(a => (a.Group, a.Target, a.Name, a.Field)).Distinct().Count());
        Assert.Equal(
            ["window[0]: from 2 s to 3 s", "window[1]: from 2 s to 4 s", "window[2]: from 20 s to 21 s"],
            result.Assumptions.Where(a => a.Group == "outage" && a.Target == Server.Value).Select(a => $"{a.Field}: {a.Value}"));
    }

    [Fact]
    public void A_full_queue_rejects_a_call_and_the_retry_policy_of_the_caller_applies()
    {
        // One worker, no waiting place, 150 ms of service, a call every 100 ms for 2 s, two attempts 100 ms apart.
        // Call 0 is served. After it every odd call is served at its second attempt and every even call is
        // rejected twice: at each instant the retry of the call before was scheduled first and takes the worker.
        var result = Run(
            Model(retry: new RetryPolicy(2, 100)), Flow(CallServe()),
            Profile(Constant(10, 2), Fixed(150), workers: 1, queue: 0));
        var operation = Assert.Single(result.Operations);

        Assert.Equal(20, result.Flow.Arrivals);
        Assert.Equal(11, result.Flow.Counts.Completed);
        Assert.Equal(9, result.Flow.Counts.Failed);
        Assert.Equal(1 + 9 * 2 + 9, operation.Counts.Rejected);
        Assert.Equal(19, operation.Retries);
        Assert.Equal(9, operation.Exhausted);
        Assert.Equal(28, result.Flow.Counts.Rejected);
    }

    [Fact]
    public void A_message_fans_out_through_a_binding_a_slow_consumer_builds_a_backlog_and_failed_deliveries_are_dead_lettered()
    {
        // Five messages each second for 4 s. Inventory is on the exchange; Billing is on a queue bound to it.
        // Billing has one consumer that takes 400 ms, and its subscription is down for the first second:
        // the five messages of that second fail twice, 50 ms apart, and go to the dead-letter channel.
        // The other 15 are consumed one every 400 ms from 1.0 s: the last one ends at 7.0 s.
        var scenario = Flow(new PublishStep("publish", Server, Exchange, Created, null, new Dictionary<string, string>(),
            new Dictionary<SubscriptionId, IReadOnlyList<Step>>()));
        var profile = new StressProfile("Fan-out", G(99), "1", Constant(5, 4), [],
            [new(BillingSub, Fixed(400)), new(InventorySub, Fixed(100))],
            [new(BillingSub, 1, null), new(InventorySub, 2, null)], [], [], [new Outage(BillingSub, 0, 1)]);
        var result = Run(Model(), scenario, profile);
        var billing = result.Subscriptions.Single(s => s.Id == BillingSub.Value);
        var inventory = result.Subscriptions.Single(s => s.Id == InventorySub.Value);

        Assert.Equal(new Counts(20, 0, 0, 0, 0, 5), result.Flow.Counts);
        Assert.Equal(new Counts(15, 10, 0, 0, 0, 5), billing.Counts);
        Assert.Equal(5, billing.Retries);
        Assert.Equal(3200.0, billing.Latency.MaxMs);
        Assert.Equal(7, billing.Queue.DepthAtEndOfArrivals);
        Assert.Equal(new Counts(20, 0, 0, 0, 0, 0), inventory.Counts);
        Assert.Equal(100.0, inventory.Latency.MaxMs);
        Assert.Equal(3000.0, result.Drain.TimeToDrainMs);
        Assert.True(result.Bottleneck.Found);
        Assert.Equal((BillingSub.Value, "subscription"), (result.Bottleneck.Id!.Value, result.Bottleneck.Kind));
        Assert.Equal(20, Assert.Single(result.Channels, c => c.Id == Exchange.Value).Published);
    }

    [Fact]
    public void A_full_subscription_queue_drops_a_message_when_no_dead_letter_channel_is_declared()
    {
        // Inventory has no dead-letter channel. One consumer at 400 ms and one waiting place against 5 messages each second.
        var scenario = Flow(new PublishStep("publish", Server, Exchange, Created, null, new Dictionary<string, string>(),
            new Dictionary<SubscriptionId, IReadOnlyList<Step>>()));
        var profile = new StressProfile("Overflow", G(99), "1", Constant(5, 4), [],
            [new(BillingSub, Fixed(1)), new(InventorySub, Fixed(400))],
            [new(BillingSub, 1, null), new(InventorySub, 1, 1)], [], [], []);
        var inventory = Run(Model(), scenario, profile).Subscriptions.Single(s => s.Id == InventorySub.Value);

        Assert.Equal(20, inventory.Arrived);
        Assert.Equal(inventory.Overflowed, inventory.Counts.Dropped);
        Assert.Equal(20, inventory.Counts.Completed + inventory.Counts.Dropped);
        Assert.True(inventory.Counts.Dropped >= 8, $"dropped {inventory.Counts.Dropped}");
    }

    [Fact]
    public void A_branch_step_takes_its_then_path_with_the_declared_probability()
    {
        var scenario = Flow(
            new WriteStep("save", Server, Store, "orders", null),
            new BranchStep("check", "save", StepOutcome.Succeeded,
                [CallServe()],
                [new CallStep("slow", Client, Slow, null, null, [], [])]));
        StressProfile With(double probability) => new("Branch", G(99), "3", Constant(100, 40),
            [new(Server, 50, null), new(Store, 50, null), new(Other, 50, null)],
            [new(Serve, Fixed(1)), new(Slow, Fixed(1)), new(Store, Fixed(1))],
            [], [new BranchAssumption("check", probability)], [], []);
        long Served(double probability) => Run(Model(), scenario, With(probability)).Operations.Single(o => o.Id == Serve.Value).Attempts;

        Assert.Equal(0, Served(0));
        Assert.Equal(4000, Served(1));
        // 4 000 draws at 0.25: mean 1 000, standard deviation 27. 100 is more than three deviations.
        Assert.InRange(Served(0.25), 900, 1100);
    }

    [Fact]
    public void Parallel_branches_run_at_the_same_time_and_the_step_ends_with_the_slowest()
    {
        var scenario = Flow(new ParallelStep("both",
        [
            [CallServe()],
            [new CallStep("slow", Client, Slow, null, null, [], [])],
        ]));
        var profile = new StressProfile("Parallel", G(99), "1", Constant(1, 5),
            [new(Server, 1, null), new(Other, 1, null)],
            [new(Serve, Fixed(100)), new(Slow, Fixed(300))], [], [], [], []);
        var result = Run(Model(), scenario, profile);

        Assert.Equal((300.0, 300.0), (result.Flow.Latency.MinMs!.Value, result.Flow.Latency.MaxMs!.Value));
    }

    [Fact]
    public void A_worker_is_held_for_the_synchronous_steps_inside_the_callee()
    {
        // 50 ms of service, then a write of 150 ms inside the call: the worker is busy 200 ms of each second.
        var scenario = Flow(CallServe(new WriteStep("save", Server, Store, "orders", null)));
        var profile = new StressProfile("Nested", G(99), "1", Constant(1, 10),
            [new(Server, 1, null), new(Store, 1, null)],
            [new(Serve, Fixed(50)), new(Store, Fixed(150))], [], [], [], []);
        var result = Run(Model(), scenario, profile);

        Assert.Equal(0.2, result.Elements.Single(e => e.Id == Server.Value).Utilisation, 12);
        Assert.Equal(0.15, result.Elements.Single(e => e.Id == Store.Value).Utilisation, 12);
        Assert.Equal(200.0, result.Flow.Latency.MaxMs);
    }

    [Fact]
    public void An_override_of_the_model_is_applied_and_listed_next_to_the_value_of_the_model()
    {
        var profile = Profile(Constant(1, 10), Fixed(300)) with { Overrides = [new PolicyOverride(Serve, 1000, null, null)] };
        var result = Run(Model(timeoutMs: 200), Flow(CallServe()), profile);

        Assert.Equal(10, result.Flow.Counts.Completed);
        Assert.Equal(new OverrideRow(Serve.Value, "Serve", "timeoutMs", 200, 1000, AssumptionLabel.Declared, null), Assert.Single(result.Overrides));
        // The assumptions still state what the model declares.
        Assert.Contains(result.Assumptions, a => a.Group == "model-policy" && a.Field == "timeoutMs" && a.Value == "200");
    }

    [Fact]
    public void Ramp_and_burst_arrivals_have_the_declared_area()
    {
        // A ramp from 0 to 200 per second over 100 s has 10 000 arrivals on average; the count is Poisson, deviation 100.
        var ramp = Run(Model(), Flow(CallServe()),
            Profile(new Arrivals(ArrivalKind.Ramp, 0, 200, null, 100, 0), Fixed(0.01), workers: 100));
        Assert.InRange(ramp.Flow.Arrivals, 9_700, 10_300);
        // The second half of a ramp from zero holds three quarters of the arrivals.
        var points = ramp.Series.ArrivalsPerSecond;
        Near(0.75, points.Skip(points.Count / 2).Sum() / points.Sum(), 0.02);

        // 10 per second for 100 s, and 500 per second from 40 s to 50 s: 900 + 5 000 arrivals on average, deviation 77.
        var burst = Run(Model(), Flow(CallServe()),
            Profile(new Arrivals(ArrivalKind.Burst, 10, null, [new BurstWindow(40, 50, 500)], 100, 0), Fixed(0.01), workers: 100));
        Assert.InRange(burst.Flow.Arrivals, 5_650, 6_150);
    }

    // ---- Honesty ----

    [Fact]
    public void Every_result_is_estimated_lists_every_assumption_and_ships_the_fixed_unmodelled_effects()
    {
        var (model, scenario, profile) = FanOut();
        var result = Run(model, scenario with { Faults = [new Fault(Serve, FaultKind.Error, 1)] }, profile);

        Assert.Equal("estimated", result.Label);
        Assert.All(new[] { result.Flow.Label, result.Flow.Latency.Label, result.Series.Label, result.Drain.Label, result.Bottleneck.Label }, l => Assert.Equal("estimated", l));
        Assert.All(result.Elements.Concat(result.Subscriptions), r => Assert.Equal("estimated", r.Label));
        Assert.All(result.Operations, o => Assert.Equal("estimated", o.Label));

        Assert.Equal(StressEngine.AlwaysUnmodelled, result.Unmodelled.Where(u => u.Kind == "fixed"));
        Assert.Contains(result.Unmodelled, u => u.Code == "scenario-faults" && u.Kind == "run");
        foreach (var code in new[] { "network", "runtime", "autoscaling", "locks", "pools", "caches", "payload-size", "back-pressure", "correlated-failures", "clocks", "cancellation" })
            Assert.Contains(result.Unmodelled, u => u.Code == code);

        // Every number of the profile is a row with a label.
        Assert.All(result.Assumptions, a => Assert.Equal(AssumptionLabel.Declared, a.Label));
        foreach (var (group, field) in new[] { ("arrivals", "ratePerSecond"), ("arrivals", "durationSeconds"), ("arrivals", "warmUpSeconds"), ("seed", "seed"), ("resource", "concurrency"), ("resource", "queueCapacity"), ("service-time", "medianMs"), ("service-time", "p95Ms"), ("subscription", "consumers"), ("model-policy", "timeoutMs") })
            Assert.Contains(result.Assumptions, a => a.Group == group && a.Field == field);

        Assert.Equal("low", result.Confidence.Level);
        Assert.Equal(StressEngine.ConfidenceRule, result.Confidence.Rule);
        Assert.Equal(4, result.Confidence.Reasons.Count);
    }

    [Fact]
    public void Confidence_is_medium_only_when_every_service_time_on_the_path_is_measured_and_never_high()
    {
        var measured = Profile(Poisson(10, 10), Exponential(10)) with
        {
            ServiceTimes = [new ServiceTimeAssumption(Serve, Exponential(10), AssumptionLabel.Measured, "k6:run-17")],
        };
        var result = Run(Model(), Flow(CallServe()), measured);

        Assert.Equal("medium", result.Confidence.Level);
        Assert.Contains(result.Assumptions, a => a.Group == "service-time" && a.Label == AssumptionLabel.Measured && a.MeasurementRef == "k6:run-17");

        // A measured label without the measurement it names is refused.
        var unnamed = measured with { ServiceTimes = [new ServiceTimeAssumption(Serve, Exponential(10), AssumptionLabel.Measured)] };
        Assert.Equal("invalid-profile", Refused(Model(), Flow(CallServe()), unnamed).Code);
    }

    // ---- Refusals and limits ----

    [Fact]
    public void A_run_without_a_service_time_or_workers_is_refused_and_nothing_is_defaulted()
    {
        var profile = Profile(Poisson(10, 10), Exponential(10));

        var noTime = Refused(Model(), Flow(CallServe()), profile with { ServiceTimes = [] });
        Assert.Equal(("missing-service-time", Serve.Value.ToString()), (noTime.Code, noTime.Target));
        Assert.Contains("'Serve'", noTime.Message);

        var noWorkers = Refused(Model(), Flow(CallServe()), profile with { Resources = [] });
        Assert.Equal(("missing-concurrency", Server.Value.ToString()), (noWorkers.Code, noWorkers.Target));

        // A write and a reached subscription need theirs too.
        var scenario = Flow(CallServe(new WriteStep("save", Server, Store, "orders", null)),
            new PublishStep("publish", Server, BillingQueue, Created, null, new Dictionary<string, string>(), new Dictionary<SubscriptionId, IReadOnlyList<Step>>()));
        var issues = StressEngine.Run(Model(), scenario, profile).Issues;
        Assert.Equal(
            ["missing-concurrency", "missing-service-time", "missing-consumers", "missing-service-time"],
            issues.Select(i => i.Code));
        Assert.Equal([Store.Value.ToString(), Store.Value.ToString(), BillingSub.Value.ToString(), BillingSub.Value.ToString()], issues.Select(i => i.Target));
    }

    [Fact]
    public void A_branch_step_without_a_probability_is_refused_and_the_issue_names_the_step()
    {
        var scenario = Flow(CallServe(), new BranchStep("retry-or-not", "serve", StepOutcome.Succeeded, [], []));
        var issue = Refused(Model(), scenario, Profile(Poisson(10, 10), Exponential(10)));

        Assert.Equal(("missing-branch-probability", "retry-or-not"), (issue.Code, issue.Target));
        Assert.Contains("'retry-or-not'", issue.Message);
    }

    [Fact]
    public void A_scenario_that_the_trace_engine_refuses_is_refused()
    {
        var scenario = Flow(new CallStep("ghost", Client, G(777), null, null, [], []));
        var issue = Refused(Model(), scenario, Profile(Poisson(10, 10), Exponential(10)));

        Assert.Equal(("invalid-scenario", "ghost"), (issue.Code, issue.Target));
        Assert.StartsWith("unknown-operation", issue.Message);
    }

    [Theory]
    [InlineData("duration")]
    [InlineData("arrivals")]
    [InlineData("arrivals-during-the-run")]
    [InlineData("events")]
    [InlineData("queued")]
    [InlineData("pending")]
    [InlineData("targets")]
    [InlineData("wall-clock")]
    public void A_run_over_a_limit_stops_with_stress_too_large_and_names_the_limit(string which)
    {
        var limits = StressLimits.Default;
        var model = Model();
        var profile = Profile(Poisson(100, 60), Exponential(1));
        using var cancelled = new CancellationTokenSource();
        var expected = which;
        switch (which)
        {
            case "duration": limits = limits with { MaxDurationSeconds = 59 }; break;
            case "arrivals": limits = limits with { MaxArrivals = 5_999 }; break;
            // The expected 6 000 arrivals pass the check before the run; the run counts them.
            case "arrivals-during-the-run": limits = limits with { MaxArrivals = 6_000 }; profile = profile with { Seed = "5" }; expected = "arrivals"; break;
            case "events": limits = limits with { MaxEvents = 10_000 }; break;
            case "queued": limits = limits with { MaxQueued = 50 }; profile = Profile(Poisson(100, 60), Fixed(1000)); break;
            // Each call leaves a timer for ten seconds: a thousand are scheduled at the same instant, with no work in a queue.
            case "pending": limits = limits with { MaxPendingEvents = 500 }; model = Model(timeoutMs: 10_000); break;
            case "targets": limits = limits with { MaxTargets = 1 }; break;
            default: cancelled.Cancel(); break;
        }

        var issue = Refused(model, Flow(CallServe()), profile, limits, cancelled.Token);

        Assert.Equal(("stress-too-large", expected), (issue.Code, issue.Limit));
    }

    [Fact]
    public void The_limits_of_a_run_are_in_its_result_and_in_its_hash()
    {
        var profile = Profile(Poisson(20, 20), Exponential(10));
        var result = Run(Model(), Flow(CallServe()), profile);
        var defaults = StressLimits.Default;

        Assert.Equal(
            new EffectiveLimits(defaults.MaxDurationSeconds, defaults.MaxDrainSeconds, defaults.MaxArrivals, defaults.MaxEvents,
                defaults.MaxQueued, defaults.MaxPendingEvents, defaults.MaxSamples, defaults.MaxSeriesPoints, defaults.MaxTargets),
            result.Limits);

        // A limit that changes the numbers and a limit that only decides whether there is a result: both change the hash.
        var fewerPoints = Run(Model(), Flow(CallServe()), profile, defaults with { MaxSeriesPoints = 60 });
        var moreEvents = Run(Model(), Flow(CallServe()), profile, defaults with { MaxEvents = defaults.MaxEvents + 1 });
        Assert.NotEqual(result.Hash, fewerPoints.Hash);
        Assert.NotEqual(result.Hash, moreEvents.Hash);
        Assert.Equal(result.Flow.Latency, moreEvents.Flow.Latency);
        // Real time decides whether there is a result and is no part of it.
        Assert.Equal(result.Hash, Run(Model(), Flow(CallServe()), profile, defaults with { WallClock = TimeSpan.FromSeconds(5) }).Hash);
    }

    [Fact]
    public void Samples_and_series_points_are_bounded_and_the_result_says_when_a_bound_applied()
    {
        var limits = StressLimits.Default with { MaxSamples = 100, MaxSeriesPoints = 20 };
        // Saturated on purpose: the drain is longer than the series may cover.
        var result = Run(Model(), Flow(CallServe()), Profile(Poisson(300, 20), Exponential(10)), limits);

        Assert.True(result.Flow.Latency.Reservoir);
        Assert.Equal(100, result.Flow.Latency.Kept);
        Assert.Equal(result.Flow.Counts.Completed, result.Flow.Latency.Count);
        Assert.Equal(20, result.Series.Points);
        Assert.True(result.Series.Truncated);
        Assert.All(result.Series.Resources, r => Assert.Equal(20, r.QueueDepth.Count));
        Assert.Contains("100", result.Methods.Sampling);

        var whole = Run(Model(), Flow(CallServe()), Profile(Poisson(20, 20), Exponential(10)));
        Assert.False(whole.Flow.Latency.Reservoir);
        Assert.False(whole.Series.Truncated);
        Assert.InRange(whole.Series.Points, 100, StressLimits.Default.MaxSeriesPoints);
    }

    [Fact]
    public void Work_that_is_left_at_the_drain_limit_is_reported_as_not_drained()
    {
        var limits = StressLimits.Default with { MaxDrainSeconds = 1 };
        var result = Run(Model(), Flow(CallServe()), Profile(Constant(10, 5), Fixed(1000)), limits);

        Assert.False(result.Drain.Drained);
        Assert.Null(result.Drain.TimeToDrainMs);
        Assert.True(result.Drain.LeftInSystem > 0);
    }

    [Fact]
    public void A_profile_outside_its_ranges_is_refused_with_the_field()
    {
        var good = Profile(Poisson(10, 10), Exponential(10));
        (StressProfile Profile, string Field)[] cases =
        [
            (good with { Seed = "-1" }, "seed"),
            (good with { Seed = "18446744073709551616" }, "seed"),
            (good with { Arrivals = Poisson(0, 10) }, "arrivals.ratePerSecond"),
            (good with { Arrivals = Poisson(10, 10, 10) }, "arrivals.warmUpSeconds"),
            (good with { Arrivals = new Arrivals(ArrivalKind.Ramp, 10, null, null, 10, 0) }, "arrivals.toRatePerSecond"),
            (good with { Arrivals = new Arrivals(ArrivalKind.Burst, 10, null, [new BurstWindow(5, 4, 1)], 10, 0) }, "arrivals.bursts[0]"),
            (good with { Resources = [new ResourceAssumption(Server, 0, null)] }, "resources[0].concurrency"),
            (good with { ServiceTimes = [new ServiceTimeAssumption(Serve, new Distribution(DistributionKind.Lognormal, MedianMs: 10, P95Ms: 5))] }, "serviceTimes[0].time.p95Ms"),
            (good with { ServiceTimes = [new ServiceTimeAssumption(Serve, new Distribution(DistributionKind.Exponential))] }, "serviceTimes[0].time.meanMs"),
            (good with { Branches = [new BranchAssumption("b", 1.5)] }, "branches[0].probability"),
            (good with { Overrides = [new PolicyOverride(Serve, null, 11, null)] }, "overrides[0].maxAttempts"),
            (good with { Outages = [new Outage(Server, 5, 5)] }, "outages[0]"),
            // A measured window that rounds to less than a millisecond has no rate and no utilisation.
            (good with { Arrivals = Poisson(10, 1e-10) }, "arrivals.durationSeconds"),
            (good with { Arrivals = Poisson(10, 0.0004) }, "arrivals.durationSeconds"),
            (good with { Arrivals = Poisson(10, 60, 59.9999999999) }, "arrivals.warmUpSeconds"),
            (good with { Arrivals = Poisson(10, 60, 59.9995) }, "arrivals.warmUpSeconds"),
        ];

        foreach (var (profile, field) in cases)
        {
            var issue = Refused(Model(), Flow(CallServe()), profile);
            Assert.Equal(("invalid-profile", field), (issue.Code, issue.Target));
        }
    }
}
