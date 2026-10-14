using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Vellum.Tests.Shared.Outbound;

namespace Vellum.Tests.Modules.Costs;

/// <summary>
/// The AWS Price List Bulk API adapter through the real Refit client and handler pipeline. The
/// transport serves fixtures under <c>Fixtures/Costs</c> in the documented shapes of the service
/// index, the region index and an offer file (SQS in eu-west-1). No test opens a socket.
/// </summary>
public class AwsPriceListTests(SharedOutboundHost shared) : IClassFixture<SharedOutboundHost>
{
    private const string Host = "pricing.us-east-1.amazonaws.com";
    private const string OfferPath = "/offers/v1.0/aws/AWSQueueService/20260911124607/eu-west-1/index.json";
    private const string Fair = "YZJ6HUUABH4W64YV.JRTCKXETXF.6YS6EN2CT7";
    private const string StandardTier1 = "SSZJAF3K6XY544ES.JRTCKXETXF.VXGXCWQKTY";

    private readonly OutboundHost _host = shared.Host;

    private async Task<(HttpClient Owner, Guid WorkspaceId, StubTransport Stub)> CreateAsync()
    {
        var (owner, workspaceId) = await _host.NewWorkspaceAsync();
        // The host is fixed, so each test starts with a fresh stub for it.
        _host.Transport.Forget(Host);
        return (owner, workspaceId, _host.Transport.For(Host));
    }

    private static string Fixture(string name) => Fixtures.Read($"Costs/{name}");

    private static async Task<JsonObject> Body(Task<HttpResponseMessage> request, HttpStatusCode expected)
    {
        var response = await request;
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode}: {text}");
        return JsonNode.Parse(text)!.AsObject();
    }

    private static string Url(Guid workspaceId, string path) => $"/api/workspaces/{workspaceId}/prices{path}";

    [Fact]
    public async Task The_service_and_region_lists_come_from_the_documented_indexes_without_a_credential()
    {
        var (owner, workspaceId, stub) = await CreateAsync();
        stub.Json(Fixture("aws-offer-index.json")).Json(Fixture("aws-sqs-region-index.json"));

        var services = await Body(owner.GetAsync(Url(workspaceId, "/aws/services")), HttpStatusCode.OK);
        var regions = await Body(owner.GetAsync(Url(workspaceId, "/aws/regions?service=AWSQueueService")), HttpStatusCode.OK);

        Assert.Equal(["AmazonEC2", "AWSQueueService", "comprehend"], services["services"]!.AsArray().Select(s => s!.GetValue<string>()));
        Assert.Equal(["eu-west-1", "il-central-1"], regions["regions"]!.AsArray().Select(s => s!.GetValue<string>()));
        Assert.Equal(["/offers/v1.0/aws/index.json", "/offers/v1.0/aws/AWSQueueService/current/region_index.json"],
            stub.Requests.Select(r => r.Uri.AbsolutePath));
        Assert.All(stub.Requests, r => Assert.Equal("https", r.Uri.Scheme));
        Assert.All(stub.Requests, r => Assert.Null(r.Header("Authorization")));
    }

    [Fact]
    public async Task A_search_reads_the_offer_file_of_the_region_and_lists_its_on_demand_rows()
    {
        var (owner, workspaceId, stub) = await CreateAsync();
        stub.Json(Fixture("aws-sqs-region-index.json")).Json(Fixture("aws-sqs-eu-west-1.json"));

        var page = await Body(owner.GetAsync(Url(workspaceId, "/aws/prices?service=AWSQueueService&region=eu-west-1")), HttpStatusCode.OK);

        Assert.Equal(["/offers/v1.0/aws/AWSQueueService/current/region_index.json", OfferPath], stub.Requests.Select(r => r.Uri.AbsolutePath));
        Assert.Equal($"https://{Host}{OfferPath}", page["sourceUrl"]!.GetValue<string>());
        Assert.False(page["more"]!.GetValue<bool>());

        // Five on-demand rows: the free tier, three tiers of standard requests and the fair queue.
        // The reserved term is not a price per unit and is not listed.
        var items = page["items"]!.AsArray();
        Assert.Equal(5, items.Count);
        Assert.DoesNotContain(items, i => i!["description"]!.GetValue<string>() == "Upfront Fee");
        var fair = items.Single(i => i!["rateCode"]!.GetValue<string>() == Fair)!;
        Assert.Equal(("YZJ6HUUABH4W64YV", "API Request", "Requests", 0.0000001m, "USD", "2025-08-01", "EU-Requests-Fair-Tier1"),
            (fair["sku"]!.GetValue<string>(), fair["productFamily"]!.GetValue<string>(), fair["unit"]!.GetValue<string>(),
                fair["pricePerUnit"]!.GetValue<decimal>(), fair["currency"]!.GetValue<string>(), fair["effectiveDate"]!.GetValue<string>(),
                fair["attributes"]!["usagetype"]!.GetValue<string>()));
        Assert.Null(fair["notImportable"]);
        // A row of a term with tiers, the free tier included, cannot be stored.
        Assert.All(items.Where(i => i!["rateCode"]!.GetValue<string>() != Fair), i => Assert.Contains("tiers", i!["notImportable"]!.GetValue<string>()));

        // Every word must be in the product: the SKU, the family or an attribute.
        stub.Json(Fixture("aws-sqs-region-index.json")).Json(Fixture("aws-sqs-eu-west-1.json"));
        var narrowed = await Body(owner.GetAsync(Url(workspaceId, "/aws/prices?service=AWSQueueService&region=eu-west-1&search=fair%20ireland")), HttpStatusCode.OK);
        Assert.Equal([Fair], narrowed["items"]!.AsArray().Select(i => i!["rateCode"]!.GetValue<string>()));
    }

    [Fact]
    public async Task An_offer_file_larger_than_the_read_buffer_is_read_as_a_stream_and_the_read_stops_after_the_on_demand_terms()
    {
        var (owner, workspaceId, stub) = await CreateAsync();
        // Thousands of other products before the three of the fixture, so a product and a term
        // cross the boundary of a read. Bytes after the on-demand terms are never needed.
        var fixture = Fixture("aws-sqs-eu-west-1.json");
        var filler = new StringBuilder();
        for (var i = 0; i < 20_000; i++)
            filler.Append($"\"FILL{i:D12}\":{{\"sku\":\"FILL{i:D12}\",\"productFamily\":\"Other\",\"attributes\":{{\"usagetype\":\"Filler-{i}\",\"note\":\"{new string('x', 40)}\"}}}},");
        var large = fixture.Replace("\"products\" : {", "\"products\" : {" + filler, StringComparison.Ordinal);
        var cut = large.IndexOf("\"Reserved\"", StringComparison.Ordinal);
        // The file ends inside the reserved terms: a reader that went on would fail.
        var truncated = large[..(cut + 30)];
        stub.Json(Fixture("aws-sqs-region-index.json"))
            .Body(() => new MemoryStream(Encoding.UTF8.GetBytes(truncated)), "application/octet-stream");

        var page = await Body(owner.GetAsync(Url(workspaceId, "/aws/prices?service=AWSQueueService&region=eu-west-1&search=requests")), HttpStatusCode.OK);

        Assert.True(Encoding.UTF8.GetByteCount(truncated) > 2 * 1024 * 1024);
        Assert.Equal(5, page["items"]!.AsArray().Count);
    }

    [Fact]
    public async Task An_import_reads_the_offer_file_again_stores_the_picked_rows_with_their_source_and_refuses_a_row_with_tiers()
    {
        var (owner, workspaceId, stub) = await CreateAsync();
        stub.Json(Fixture("aws-sqs-region-index.json")).Json(Fixture("aws-sqs-eu-west-1.json"));
        var before = DateTimeOffset.UtcNow;

        var stored = (await owner.PostAsJsonAsync(Url(workspaceId, "/aws"),
            new { service = "AWSQueueService", region = "eu-west-1", rateCodes = new[] { Fair } })).EnsureSuccessStatusCode();
        var entry = Assert.Single((await stored.Content.ReadFromJsonAsync<JsonArray>())!)!;

        Assert.Equal(("aws", "eu-west-1", "EU-Requests-Fair-Tier1", "Requests", 0.0000001m, "USD", "2025-08-01", "fetched"),
            (entry["provider"]!.GetValue<string>(), entry["region"]!.GetValue<string>(), entry["sku"]!.GetValue<string>(),
                entry["unit"]!.GetValue<string>(), entry["unitPrice"]!.GetValue<decimal>(), entry["currency"]!.GetValue<string>(),
                entry["effectiveDate"]!.GetValue<string>(), entry["source"]!.GetValue<string>()));
        var fetched = entry["fetched"]!;
        Assert.Equal((Fair, "2025-08-01", $"https://{Host}{OfferPath}"),
            (fetched["meterId"]!.GetValue<string>(), fetched["providerEffectiveDate"]!.GetValue<string>(), fetched["url"]!.GetValue<string>()));
        Assert.InRange(fetched["fetchedAt"]!.GetValue<DateTimeOffset>(), before.AddSeconds(-1), DateTimeOffset.UtcNow.AddSeconds(1));
        Assert.True(Guid.TryParse(entry["acceptedBy"]!.GetValue<string>(), out _));

        var audit = (await owner.GetFromJsonAsync<JsonArray>($"/api/workspaces/{workspaceId}/audit"))!;
        Assert.Contains(audit, a => a!["action"]!.GetValue<string>() == "price.imported" && a["target"]!.GetValue<string>().EndsWith($"meter:{Fair}", StringComparison.Ordinal));

        // A pick with one row of tiers stores nothing, the good row neither.
        stub.Json(Fixture("aws-sqs-region-index.json")).Json(Fixture("aws-sqs-eu-west-1.json"));
        var refused = await Body(owner.PostAsJsonAsync(Url(workspaceId, "/aws"),
            new { service = "AWSQueueService", region = "eu-west-1", rateCodes = new[] { Fair, StandardTier1 } }), HttpStatusCode.Conflict);
        Assert.Contains("tiers", refused["detail"]!.GetValue<string>());
        Assert.Single((await owner.GetFromJsonAsync<JsonObject>(Url(workspaceId, "")))!["items"]!.AsArray());
    }

    [Fact]
    public async Task An_offer_path_outside_the_service_is_not_followed_and_bad_codes_are_refused_before_any_request()
    {
        var (owner, workspaceId, stub) = await CreateAsync();
        var index = JsonNode.Parse(Fixture("aws-sqs-region-index.json"))!;
        index["regions"]!["eu-west-1"]!["currentVersionUrl"] = "/offers/v1.0/aws/AmazonEC2/20260911124607/eu-west-1/index.json";
        index["regions"]!["il-central-1"]!["currentVersionUrl"] = "https://elsewhere.test/offers/v1.0/aws/AWSQueueService/x/il-central-1/index.json";
        stub.Json(index.ToJsonString());

        foreach (var region in new[] { "eu-west-1", "il-central-1" })
        {
            var refused = await Body(owner.GetAsync(Url(workspaceId, $"/aws/prices?service=AWSQueueService&region={region}")), HttpStatusCode.BadGateway);
            Assert.Equal("price_source_unavailable", refused["type"]!.GetValue<string>());
        }
        Assert.All(stub.Requests, r => Assert.EndsWith("/region_index.json", r.Uri.AbsolutePath));
        Assert.Empty(_host.Transport.For("elsewhere.test").Requests);

        var count = stub.Requests.Count;
        foreach (var query in new[] { "service=Amazon%2FEC2&region=eu-west-1", "service=AmazonEC2&region=EU_WEST", "service=AmazonEC2" })
            await Body(owner.GetAsync(Url(workspaceId, $"/aws/prices?{query}")), HttpStatusCode.BadRequest);
        await Body(owner.PostAsJsonAsync(Url(workspaceId, "/aws"), new { service = "AWSQueueService", region = "eu-west-1", rateCodes = new[] { "x'1" } }), HttpStatusCode.BadRequest);
        Assert.Equal(count, stub.Requests.Count);
    }
}
