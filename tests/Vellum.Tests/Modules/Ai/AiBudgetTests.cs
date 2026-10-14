using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vellum.Modules.Ai;
using Vellum.Modules.Ai.Usage;
using Vellum.Tests.Shared.Outbound;

namespace Vellum.Tests.Modules.Ai;

/// <summary>What a call costs the budget on every way it can end, and what the price and usage endpoints accept.</summary>
[Collection("Integration")]
public class AiBudgetTests(SharedOutboundHost shared) : IClassFixture<SharedOutboundHost>
{
    private readonly OutboundHost _host = shared.Host;

    private static AiFailure FailureOf(AiResult<Summary> result) => Assert.IsType<AiResult<Summary>.Failed>(result).Failure;

    // The login endpoint limits attempts per minute, so the class signs the admin in once.
    private static readonly SemaphoreSlim AdminGate = new(1, 1);
    private static (OutboundHost Host, HttpClient Client)? _admin;

    private async Task<HttpClient> AdminAsync()
    {
        await AdminGate.WaitAsync();
        try
        {
            if (_admin?.Host != _host)
                _admin = (_host, await _host.SignInAsAdminAsync(shared.Fixture));
            return _admin.Value.Client;
        }
        finally
        {
            AdminGate.Release();
        }
    }

    private async Task<HttpResponseMessage> PostPriceAsync(object price) =>
        await (await AdminAsync()).PostAsJsonAsync("/api/instance/ai/prices", price);

    private async Task SetPriceAsync(AiScenario ai, decimal inputPerMillion, decimal outputPerMillion)
    {
        var response = await PostPriceAsync(new
        {
            providerKind = ai.Kind,
            model = ai.Model,
            inputUsdPerMillionTokens = inputPerMillion,
            outputUsdPerMillionTokens = outputPerMillion,
            effectiveFrom = DateTimeOffset.UtcNow.AddDays(-1),
        });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
    }

    /// <summary>A budget for one call in flight: each call reserves about 0.26 USD of output.</summary>
    private async Task<AiScenario> BudgetForOneAsync(string kind = "openai-compatible", Action<JsonObject>? provider = null)
    {
        var ai = await AiScenario.CreateAsync(_host, kind, p =>
        {
            p["monthlyBudgetUsd"] = 0.30;
            provider?.Invoke(p);
        });
        await SetPriceAsync(ai, inputPerMillion: 1, outputPerMillion: 1000);
        return ai;
    }

    [Fact]
    public async Task A_call_that_the_provider_refused_before_it_generated_costs_nothing_and_holds_no_reservation()
    {
        var ai = await BudgetForOneAsync();
        ai.Stub
            .Json(HttpStatusCode.InternalServerError, ai.Fixture("error-503.json"))
            .Json(HttpStatusCode.InternalServerError, ai.Fixture("error-503.json"))
            .Json(HttpStatusCode.Unauthorized, ai.Fixture("error-401.json"))
            .Throw(() => new HttpRequestException(HttpRequestError.ResponseEnded, "reset"))
            .Json(ai.Completion(AiScenario.ValidAnswer));

        for (var i = 0; i < 4; i++)
            Assert.IsType<AiFailure.ProviderError>(FailureOf(await ai.StructuredAsync()));
        // Four failed calls later the budget still has room for a call.
        Assert.IsType<AiResult<Summary>.Succeeded>(await ai.StructuredAsync());

        var rows = await ai.RowsAsync();
        Assert.Equal(5, rows.Count);
        Assert.All(rows, row => Assert.Null(row.ReservedUsd));
        Assert.All(rows.Take(4), row => Assert.Equal(0m, row.CostUsd));
    }

    [Fact]
    public async Task A_timed_out_and_a_cancelled_call_release_their_reservation()
    {
        var ai = await BudgetForOneAsync(provider: p => p["timeoutSeconds"] = 1);
        using var cancel = new CancellationTokenSource();
        ai.Stub
            .Hang()
            .Then(async sent =>
            {
                await cancel.CancelAsync();
                await Task.Delay(Timeout.Infinite, sent.Cancellation);
                throw new InvalidOperationException("unreachable");
            })
            .Json(ai.Completion(AiScenario.ValidAnswer));

        Assert.IsType<AiFailure.TimedOut>(FailureOf(await ai.StructuredAsync()));
        Assert.IsType<AiFailure.Cancelled>(FailureOf(await ai.StructuredAsync(ct: cancel.Token)));
        Assert.IsType<AiResult<Summary>.Succeeded>(await ai.StructuredAsync());

        Assert.All(await ai.RowsAsync(), row => Assert.Null(row.ReservedUsd));
    }

    [Fact]
    public async Task A_row_left_started_by_a_crash_is_released_by_the_next_admission()
    {
        var ai = await BudgetForOneAsync();
        ai.Stub.Json(ai.Completion(AiScenario.ValidAnswer));
        var now = DateTimeOffset.UtcNow;
        var monthStart = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero);
        var crashed = Guid.NewGuid();
        var recent = Guid.NewGuid();
        using (var scope = _host.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AiUsageDbContext>();
            UsageRecordEntity Row(Guid id, DateTimeOffset startedAt, decimal reserved) => new()
            {
                Id = id, WorkspaceId = ai.WorkspaceId, ProviderId = ai.ProviderId, ProviderKind = ai.Kind, Model = ai.Model,
                Task = "proposeFlow", Purpose = "summarise", PromptTemplateVersion = "summarise/1",
                ReservedUsd = reserved, Outcome = "started", StartedAt = startedAt,
            };
            // Older than any call can run. In the first hours of a month it lies in the month before, which the budget does not count.
            db.UsageRecords.Add(Row(crashed, now.AddHours(-3), 100m));
            // A call that can still be running keeps its reservation.
            db.UsageRecords.Add(Row(recent, now.AddMinutes(-1), 0.01m));
            await db.SaveChangesAsync();
        }

        var result = await ai.StructuredAsync();

        var rows = await ai.RowsAsync();
        var reaped = rows.Single(r => r.Id == crashed);
        Assert.Equal(("abandoned", null), (reaped.Outcome, reaped.ReservedUsd));
        Assert.Equal(("started", 0.01m), (rows.Single(r => r.Id == recent).Outcome, rows.Single(r => r.Id == recent).ReservedUsd));
        if (now.AddHours(-3) >= monthStart)
            Assert.IsType<AiResult<Summary>.Succeeded>(result);
    }

    [Theory]
    [InlineData(-2_000_000, 21)]
    [InlineData(9, -5)]
    [InlineData(2_000_000_000, 21)]
    public async Task A_token_count_outside_the_sane_range_is_unknown_and_never_lowers_the_cost(int completionTokens, int promptTokens)
    {
        var ai = await BudgetForOneAsync();
        var body = JsonNode.Parse(ai.Completion(AiScenario.ValidAnswer))!.AsObject();
        body["usage"] = new JsonObject { ["prompt_tokens"] = promptTokens, ["completion_tokens"] = completionTokens };
        ai.Stub.Json(body.ToJsonString());

        var first = Assert.IsType<AiResult<Summary>.Succeeded>(await ai.StructuredAsync());

        var row = Assert.Single(await ai.RowsAsync());
        Assert.True(row.CostUsd > 0m, $"cost {row.CostUsd}");
        Assert.Null(row.ReservedUsd);
        Assert.True(row.InputTokens is null or >= 0);
        Assert.True(row.OutputTokens is null or (>= 0 and <= 100_000_000));
        Assert.True(first.Usage.CostUsd > 0m);
        // An invalid output count is charged as the worst case, so the budget for one call is used up.
        if (completionTokens is < 0 or > 100_000_000)
            Assert.IsType<AiFailure.BudgetExceeded>(FailureOf(await ai.StructuredAsync()));
    }

    [Theory]
    [InlineData("openai")]
    [InlineData("anthropic")]
    [InlineData("gemini")]
    [InlineData("openai-compatible")]
    public async Task A_stream_that_gave_output_and_then_failed_is_charged_an_estimate_and_one_refused_before_output_is_free(string kind)
    {
        var ai = await AiScenario.CreateAsync(_host, kind);
        await SetPriceAsync(ai, inputPerMillion: 1, outputPerMillion: 1000);
        ai.Stub
            .Sse(ai.Fixture("stream-error.sse"))
            .Json(HttpStatusCode.Unauthorized, ai.Fixture("error-401.json"));

        var (chunks, failure) = await ai.StreamAsync();
        var (none, refused) = await ai.StreamAsync();

        Assert.NotEmpty(chunks);
        Assert.IsType<AiFailure.ProviderError>(failure);
        Assert.Empty(none);
        Assert.IsType<AiFailure.ProviderError>(refused);

        var rows = await ai.RowsAsync();
        // The provider generated text and may bill it. It reported no usable counts, so the
        // call costs the reservation's own figure: the input estimate and the whole output ceiling.
        var ceiling = kind == "anthropic" ? 256 + 8192 : 256;
        Assert.Equal(("provider_error", "cost_estimated", null), (rows[0].Outcome, rows[0].UsageNote, rows[0].ReservedUsd));
        Assert.InRange(rows[0].CostUsd!.Value, ceiling * 1000m / 1_000_000m, ceiling * 1000m / 1_000_000m + 0.01m);
        Assert.Equal(("provider_error", "invalid_api_key", 0m, null, null), (rows[1].Outcome, rows[1].ErrorCode, rows[1].CostUsd, rows[1].UsageNote, rows[1].ReservedUsd));

        var total = Assert.Single((await ai.Owner.GetFromJsonAsync<JsonObject>($"/api/workspaces/{ai.WorkspaceId}/ai/usage"))!["totals"]!.AsArray())!;
        Assert.True(total["costKnown"]!.GetValue<bool>());
        Assert.True(total["costEstimated"]!.GetValue<bool>());
        Assert.Equal(rows[0].CostUsd, total["costUsd"]!.GetValue<decimal>());
    }

    [Fact]
    public async Task A_reader_that_stops_a_stream_releases_the_reservation_and_is_charged_an_estimate()
    {
        var ai = await BudgetForOneAsync();
        ai.Stub.Sse(ai.Fixture("stream.sse"));

        using (var scope = _host.Factory.Services.CreateScope())
        {
            await foreach (var chunk in scope.ServiceProvider.GetRequiredService<AiClient>().StreamTextAsync(ai.Request()))
            {
                Assert.Equal("Hello", chunk);
                break;
            }
        }

        var row = Assert.Single(await ai.RowsAsync());
        Assert.Equal(("cancelled", "cost_estimated", null), (row.Outcome, row.UsageNote, row.ReservedUsd));
        Assert.True(row.CostUsd > 0.25m);
    }

    [Fact]
    public async Task The_usage_endpoint_refuses_a_range_the_wrong_way_round_and_a_range_over_one_year()
    {
        var ai = await AiScenario.CreateAsync(_host, "openai");
        var root = $"/api/workspaces/{ai.WorkspaceId}/ai/usage";

        Assert.Equal(HttpStatusCode.OK, (await ai.Owner.GetAsync($"{root}?from=2025-01-01T00:00:00Z&to=2026-01-01T00:00:00Z")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await ai.Owner.GetAsync($"{root}?from=2025-01-01T00:00:00Z&to=2026-01-03T00:00:00Z")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await ai.Owner.GetAsync($"{root}?from=1990-01-01T00:00:00Z")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await ai.Owner.GetAsync($"{root}?from=2026-01-01T00:00:00Z&to=2026-01-01T00:00:00Z")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await ai.Owner.GetAsync($"{root}?from=2026-02-01T00:00:00Z&to=2026-01-01T00:00:00Z")).StatusCode);
    }

    [Theory]
    [InlineData("0", "1")]
    [InlineData("1", "0")]
    [InlineData("0.000000001", "1")]
    [InlineData("1", "0.000000004")]
    [InlineData("-0.5", "1")]
    [InlineData("100001", "1")]
    [InlineData("1e30", "1")]
    [InlineData("1e400", "1")]
    [InlineData("NaN", "1")]
    [InlineData("\"Infinity\"", "1")]
    public async Task The_price_endpoint_refuses_a_price_that_stores_as_zero_or_is_not_a_sane_number(string input, string output)
    {
        var admin = await AdminAsync();
        var json = $$"""{"providerKind":"openai","model":"price-{{Guid.NewGuid():N}}","inputUsdPerMillionTokens":{{input}},"outputUsdPerMillionTokens":{{output}}}""";

        var response = await admin.PostAsync("/api/instance/ai/prices", new StringContent(json, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task The_price_endpoint_stores_the_smallest_price_it_can_hold()
    {
        var model = $"price-{Guid.NewGuid():N}";
        var response = await PostPriceAsync(new { providerKind = "openai", model, inputUsdPerMillionTokens = 0.00000001m, outputUsdPerMillionTokens = 0.000000014m });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var stored = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.Equal((0.00000001m, 0.00000001m),
            (stored["inputUsdPerMillionTokens"]!.GetValue<decimal>(), stored["outputUsdPerMillionTokens"]!.GetValue<decimal>()));
    }
}
