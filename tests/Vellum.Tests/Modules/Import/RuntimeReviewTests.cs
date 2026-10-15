using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vellum.Modules.Import.Runtime;

namespace Vellum.Tests.Modules.Import;

/// <summary>The findings of the step 21 review: bounds that hold for every span, paging that ends, names that leak nothing, labels on every fact, and keys that go with their project.</summary>
public class RuntimeReviewTests(ImportHost import) : IClassFixture<ImportHost>
{
    private static readonly TraceLimits Three = new(MaxBytes: 64L * 1024 * 1024, MaxSpans: 3, MaxServices: 100, MaxEdges: 100);

    private static Task<RuntimeGraph> ReadAsync(string json, TraceLimits? limits = null) =>
        ReadAsync(Encoding.UTF8.GetBytes(json), limits);

    private static Task<RuntimeGraph> ReadAsync(byte[] bytes, TraceLimits? limits = null) =>
        TraceExport.ReadAsync(new MemoryStream(bytes), "f.json", limits ?? TraceLimits.Default, CancellationToken.None);

    [Theory]
    [InlineData("otlp")]
    [InlineData("jaeger")]
    public async Task Every_span_counts_toward_the_bound_while_it_is_read_with_or_without_an_ID(string format)
    {
        var spans = string.Join(",", Enumerable.Repeat("{}", 100_000));
        var json = format == "otlp"
            ? $$"""{"resourceSpans":[{"scopeSpans":[{"spans":[{{spans}}]}]}]}"""
            : $$"""{"data":[{"spans":[{{spans}}]}]}""";
        var refusal = await Assert.ThrowsAsync<TraceRefusal>(() => ReadAsync(json, Three));
        Assert.Equal("trace_too_many_spans", refusal.Code);
    }

    [Fact]
    public async Task Links_of_one_span_are_bounded()
    {
        var links = string.Join(",", Enumerable.Range(0, 5000).Select(i => $$"""{"traceId":"t","spanId":"l{{i}}"}"""));
        var json = """{"resourceSpans":[{"resource":{"attributes":[{"key":"service.name","value":{"stringValue":"a"}}]},"scopeSpans":[{"spans":[{"traceId":"t","spanId":"s","kind":5,"links":[""" + links + "]}]}]}]}";
        var refusal = await Assert.ThrowsAsync<TraceRefusal>(() => ReadAsync(json));
        Assert.Equal("trace_too_many_links", refusal.Code);
    }

    [Fact]
    public async Task Invalid_UTF8_and_a_lone_surrogate_are_malformed_not_a_server_error()
    {
        var prefix = """{"resourceSpans":[{"resource":{"attributes":[{"key":"service.name","value":{"stringValue":" """u8.ToArray();
        var suffix = "\"}}]}}]}"u8.ToArray();
        byte[] invalid = [.. prefix[..^1], 0xFF, 0xFE, .. suffix];
        Assert.Equal("trace_malformed", (await Assert.ThrowsAsync<TraceRefusal>(() => ReadAsync(invalid))).Code);
        var surrogate = """{"resourceSpans":[{"resource":{"attributes":[{"key":"service.name","value":{"stringValue":"\ud800"}}]}}]}""";
        Assert.Equal("trace_malformed", (await Assert.ThrowsAsync<TraceRefusal>(() => ReadAsync(surrogate))).Code);
    }

    [Fact]
    public void A_secret_past_the_name_length_is_still_found()
    {
        var name = new string('a', 190) + " ghp_" + new string('Z', 36);
        Assert.Null(RuntimeGraph.CleanName(name));
        Assert.Null(RuntimeSources.CleanFileName("alice@example.com.json"));
        Assert.Null(RuntimeSources.CleanFileName("token=ghp_" + new string('Z', 36) + ".json"));
        Assert.Equal("export.json", RuntimeSources.CleanFileName("C:\\exports\\export.json"));
    }

    [Fact]
    public void New_Relic_type_values_outside_the_known_shape_are_not_kept()
    {
        var entity = new NewRelicEntity("GUID1", "orders", "alice@example.com", "token=ghp_" + new string('Z', 36), "APM_APPLICATION_ENTITY");
        var other = new NewRelicEntity("GUID2", "payments", "APM", "APPLICATION", "APM_APPLICATION_ENTITY");
        var graph = RuntimeFetch.Graph(new NewRelicAccountRead([entity, other], [], true, []), 1, "us");
        var document = Encoding.UTF8.GetString(graph.Write());
        Assert.DoesNotContain("alice@example.com", document, StringComparison.Ordinal);
        Assert.DoesNotContain("ghp_", document, StringComparison.Ordinal);
        Assert.Contains("APM_APPLICATION_ENTITY", document, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_count_of_operations_left_out_is_a_sample_value()
    {
        var spans = string.Join(",", Enumerable.Range(0, TraceLimits.MaxOperationsPerEdge + 3).Select(i =>
            "{\"traceId\":\"t\",\"spanId\":\"s" + i + "\",\"name\":\"op " + i + "\",\"kind\":3,\"attributes\":[{\"key\":\"peer.service\",\"value\":{\"stringValue\":\"b\"}}]}"));
        var json = """{"resourceSpans":[{"resource":{"attributes":[{"key":"service.name","value":{"stringValue":"a"}}]},"scopeSpans":[{"spans":[""" + spans + "]}]}]}";
        var edge = Assert.Single((await ReadAsync(json)).Edges);
        Assert.Equal("3", edge.Attributes["sample.operationsLeftOut"]);
        Assert.DoesNotContain("operationsLeftOut", edge.Attributes.Keys);
    }

    [Fact]
    public async Task A_cursor_that_repeats_ends_the_read_as_partial_with_a_bounded_number_of_requests()
    {
        var s = await ImportScenario.CreateAsync(import, fixture: null);
        import.Host.Transport.Forget("api.newrelic.com");
        import.Host.Transport.For("api.newrelic.com").Then(sent =>
        {
            var query = JsonNode.Parse(sent.Body)!["query"]!.GetValue<string>();
            var body = query.Contains("entitySearch", StringComparison.Ordinal)
                ? """{"data":{"actor":{"entitySearch":{"results":{"nextCursor":"same","entities":[{"guid":"G1","name":"orders","domain":"APM","type":"APPLICATION","entityType":"APM_APPLICATION_ENTITY"}]}}}}}"""
                : query.Contains("entities(guids", StringComparison.Ordinal)
                    ? """{"data":{"actor":{"entities":[{"guid":"G1","relatedEntities":{"nextCursor":"again","results":[]}}]}}}"""
                    : """{"data":{"actor":{"entity":{"relatedEntities":{"nextCursor":"again","results":[]}}}}}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        });
        var source = await CreateNewRelicAsync(s);
        import.Time.Advance(TimeSpan.FromSeconds(1));
        var started = await s.Owner.PostAsJsonAsync($"/api/projects/{s.ProjectId}/sources/{source}/scans", new { });
        Assert.Equal(HttpStatusCode.Accepted, started.StatusCode);
        var scanId = (await started.Content.ReadFromJsonAsync<JsonObject>())!.Id();

        var run = s.DrainAsync();
        Assert.Same(run, await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(30))));
        var scan = await s.GetAsync(scanId);
        Assert.Equal(("completed", false), (scan.Text("status"), scan["isComplete"]!.GetValue<bool>()));
        Assert.Contains(scan["notes"]!.AsArray(), n => n!["code"]!.GetValue<string>() == "limit:pages");
        Assert.True(import.Host.Transport.For("api.newrelic.com").Requests.Count < 10);
    }

    [Fact]
    public async Task Deleting_a_project_deletes_the_New_Relic_keys_of_its_Sources()
    {
        var s = await ImportScenario.CreateAsync(import, fixture: null);
        var source = await CreateNewRelicAsync(s);
        using (var scope = import.Host.Factory.Services.CreateScope())
            Assert.True(await scope.ServiceProvider.GetRequiredService<Vellum.Shared.AppDbContext>().Secrets.AnyAsync(x => x.Name == $"newrelic:{source}:us:apiKey"));

        var deleted = await s.Owner.DeleteAsync($"/api/projects/{s.ProjectId}");
        Assert.True(deleted.IsSuccessStatusCode, await deleted.Content.ReadAsStringAsync());
        using (var scope = import.Host.Factory.Services.CreateScope())
            Assert.False(await scope.ServiceProvider.GetRequiredService<Vellum.Shared.AppDbContext>().Secrets.AnyAsync(x => x.Name.StartsWith($"newrelic:{source}:")));
    }

    private static async Task<Guid> CreateNewRelicAsync(ImportScenario s)
    {
        var response = await s.Owner.PostAsJsonAsync($"/api/projects/{s.ProjectId}/sources",
            new { kind = "newrelic", accountId = 7654321, region = "us", apiKey = "test-newrelic-user-key-0002" });
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<JsonObject>())!.Id();
    }
}
