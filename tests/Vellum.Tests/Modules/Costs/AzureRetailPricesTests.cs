using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Vellum.Shared;
using Vellum.Shared.Outbound;
using Vellum.Tests.Shared.Outbound;

namespace Vellum.Tests.Modules.Costs;

/// <summary>
/// The Azure Retail Prices adapter through the real Refit client and handler pipeline. The
/// transport serves the fixtures under <c>Fixtures/Costs</c>: answers of
/// <c>prices.azure.com</c> recorded on 2026-10-13. No test opens a socket.
/// </summary>
[Collection("Integration")]
public class AzureRetailPricesTests(SharedOutboundHost shared) : IClassFixture<SharedOutboundHost>
{
    private const string StorageMeter = "10e5ee74-8fd6-4972-909f-65a83f92f0ce";
    private const string StorageSku = "DZH318Z0BPH7/00BJ";

    private readonly OutboundHost _host = shared.Host;

    private sealed record Scenario(HttpClient Owner, Guid WorkspaceId, string HostName, StubTransport Stub)
    {
        public string Url(string path = "") => $"/api/workspaces/{WorkspaceId}/prices{path}";

        public Task<HttpResponseMessage> SearchAsync(string query = "serviceName=Virtual%20Machines") =>
            Owner.GetAsync(Url($"/azure/meters?{query}"));

        public Task<HttpResponseMessage> ImportAsync(string currency = "EUR", string? skuId = StorageSku) =>
            Owner.PostAsJsonAsync(Url("/azure"), new { meterId = StorageMeter, skuId, armRegionName = "westeurope", currency });

        public async Task<JsonArray> BookAsync() =>
            (await Owner.GetFromJsonAsync<JsonObject>(Url()))!["items"]!.AsArray();
    }

    private async Task<Scenario> CreateAsync()
    {
        var (owner, workspaceId) = await _host.NewWorkspaceAsync();
        var hostName = $"{Guid.NewGuid():N}.prices.test";
        await OutboundHost.PutSettingsAsync(owner, workspaceId,
            new JsonObject { ["cost"] = new JsonObject { ["azureRetailPricesBaseUrl"] = $"https://{hostName}" } });
        return new Scenario(owner, workspaceId, hostName, _host.Transport.For(hostName));
    }

    private static string Fixture(string name) => Fixtures.Read($"Costs/{name}");

    private static string WithNext(string name, string next)
    {
        var page = JsonNode.Parse(Fixture(name))!;
        page["NextPageLink"] = next;
        return page.ToJsonString();
    }

    private static async Task<JsonObject> Body(Task<HttpResponseMessage> request, HttpStatusCode expected)
    {
        var response = await request;
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode}: {text}");
        return JsonNode.Parse(text)!.AsObject();
    }

    [Fact]
    public async Task A_search_sends_the_documented_request_and_stores_nothing()
    {
        var azure = await CreateAsync();
        azure.Stub.Json(Fixture("azure-vm-d2s-v5.json"));

        var page = await Body(azure.SearchAsync("serviceName=Virtual%20Machines&region=westeurope&meterName=D2s%20v5"), HttpStatusCode.OK);

        var sent = Assert.Single(azure.Stub.Requests);
        Assert.Equal(HttpMethod.Get, sent.Method);
        Assert.Equal("/api/retail/prices", sent.Uri.AbsolutePath);
        Assert.Equal(
            "?api-version=2023-01-01-preview&currencyCode='USD'&$filter=serviceName eq 'Virtual Machines' and armRegionName eq 'westeurope' and contains(meterName, 'D2s v5') and priceType eq 'Consumption'",
            Uri.UnescapeDataString(sent.Uri.Query));
        // The API is public. The request carries no credential and no cookie of the caller.
        Assert.Null(sent.Header("Authorization"));
        Assert.Null(sent.Header("Cookie"));

        var items = page["items"]!.AsArray();
        Assert.Equal(6, items.Count);
        Assert.False(page["more"]!.GetValue<bool>());
        Assert.StartsWith($"https://{azure.HostName}/api/retail/prices?", page["sourceUrl"]!.GetValue<string>());
        var first = items[0]!;
        Assert.Equal(
            ("0561adcb-1c48-542e-9e0c-10dea22fc392", "D2s v5", "Virtual Machines Dsv5 Series", "DZH318Z08M9W/0092", "westeurope", "1 Hour", 0.115m, "USD", "2021-11-01", "Consumption"),
            (first["meterId"]!.GetValue<string>(), first["meterName"]!.GetValue<string>(), first["productName"]!.GetValue<string>(),
                first["skuId"]!.GetValue<string>(), first["armRegionName"]!.GetValue<string>(), first["unitOfMeasure"]!.GetValue<string>(),
                first["retailPrice"]!.GetValue<decimal>(), first["currencyCode"]!.GetValue<string>(),
                first["effectiveStartDate"]!.GetValue<string>(), first["type"]!.GetValue<string>()));
        Assert.All(items, item => Assert.Null(item!["notImportable"]));

        Assert.Empty(await azure.BookAsync());
    }

    [Fact]
    public async Task The_next_page_is_read_from_the_configured_origin_only_and_the_pages_are_bounded()
    {
        var azure = await CreateAsync();
        var next = $"https://{azure.HostName}:443/api/retail/prices?api-version=2023-01-01-preview&$skip=1000";
        azure.Stub
            .Json(Fixture("azure-page-1.json").Replace("__NEXT__", next))
            .Json(WithNext("azure-page-2.json", next));

        var page = await Body(azure.SearchAsync(), HttpStatusCode.OK);

        // Two pages are read. The second one names a third, so the answer says that there is more.
        Assert.Equal(["/api/retail/prices", "/api/retail/prices"], azure.Stub.Requests.Select(r => r.Uri.AbsolutePath));
        Assert.EndsWith("$skip=1000", Uri.UnescapeDataString(azure.Stub.Requests[1].Uri.Query));
        Assert.Equal(5, page["items"]!.AsArray().Count);
        Assert.True(page["more"]!.GetValue<bool>());

        // The price of a reservation is the price of a whole term. It is shown and cannot be stored.
        var reservation = page["items"]!.AsArray().Single(i => i!["type"]!.GetValue<string>() == "Reservation")!;
        Assert.Equal((622.0m, "1 Year"), (reservation["retailPrice"]!.GetValue<decimal>(), reservation["reservationTerm"]!.GetValue<string>()));
        Assert.Equal("Vellum stores consumption prices only.", reservation["notImportable"]!.GetValue<string>());

        // A link to another server is not followed.
        var other = await CreateAsync();
        var elsewhere = $"{Guid.NewGuid():N}.elsewhere.test";
        other.Stub.Json(Fixture("azure-page-1.json").Replace("__NEXT__", $"https://{elsewhere}/api/retail/prices?$skip=1000"));
        var refused = await Body(other.SearchAsync(), HttpStatusCode.BadGateway);
        Assert.Equal("price_source_unavailable", refused["type"]!.GetValue<string>());
        Assert.Contains("another server", refused["detail"]!.GetValue<string>());
        Assert.Empty(_host.Transport.For(elsewhere).Requests);
    }

    [Fact]
    public async Task An_import_reads_the_row_again_and_stores_what_the_api_holds()
    {
        var azure = await CreateAsync();
        azure.Stub.Json(Fixture("azure-storage-hot-lrs-meter.json"));
        var before = DateTimeOffset.UtcNow;

        var entry = await Body(azure.ImportAsync(), HttpStatusCode.Created);

        Assert.Equal(
            $"?api-version=2023-01-01-preview&currencyCode='EUR'&$filter=armRegionName eq 'westeurope' and meterId eq '{StorageMeter}' and skuId eq '{StorageSku}' and priceType eq 'Consumption'",
            Uri.UnescapeDataString(Assert.Single(azure.Stub.Requests).Uri.Query));

        // Three rows share the meter: one price from zero and two for higher tiers. Only the first is a price Vellum stores.
        Assert.Equal(
            ("azure", "westeurope", "Hot LRS", "General Block Blob v2 · Hot LRS Data Stored", "1 GB/Month", 0.017247m, "EUR", "2017-02-03", "fetched", true),
            (entry["provider"]!.GetValue<string>(), entry["region"]!.GetValue<string>(), entry["sku"]!.GetValue<string>(),
                entry["description"]!.GetValue<string>(), entry["unit"]!.GetValue<string>(), entry["unitPrice"]!.GetValue<decimal>(),
                entry["currency"]!.GetValue<string>(), entry["effectiveDate"]!.GetValue<string>(), entry["source"]!.GetValue<string>(),
                entry["unitReadable"]!.GetValue<bool>()));
        var fetched = entry["fetched"]!;
        Assert.Equal((StorageMeter, "2017-02-03"), (fetched["meterId"]!.GetValue<string>(), fetched["providerEffectiveDate"]!.GetValue<string>()));
        Assert.StartsWith($"https://{azure.HostName}/api/retail/prices?", fetched["url"]!.GetValue<string>());
        Assert.InRange(fetched["fetchedAt"]!.GetValue<DateTimeOffset>(), before.AddSeconds(-1), DateTimeOffset.UtcNow.AddSeconds(1));

        var stored = Assert.Single(await azure.BookAsync())!;
        Assert.Equal(entry["id"]!.GetValue<Guid>(), stored["id"]!.GetValue<Guid>());
        Assert.Equal(0.017247m, stored["unitPrice"]!.GetValue<decimal>());
    }

    [Fact]
    public async Task An_import_that_does_not_find_exactly_one_row_stores_nothing()
    {
        // Two products share the meter. Without the SKU the meter names two rows.
        var azure = await CreateAsync();
        var answer = JsonNode.Parse(Fixture("azure-storage-hot-lrs-meter.json"))!.AsObject();
        var second = answer["Items"]![0]!.DeepClone();
        second["skuId"] = "DZH318Z0BNZJ/008R";
        second["productName"] = "Blob Storage";
        answer["Items"]!.AsArray().Add(second);
        azure.Stub.Json(answer.ToJsonString());
        var conflict = await Body(azure.ImportAsync(skuId: null), HttpStatusCode.Conflict);
        Assert.Contains("more than one row", conflict["title"]!.GetValue<string>());

        // The API answers in another currency than the one asked for: no row is taken.
        var wrongCurrency = await CreateAsync();
        wrongCurrency.Stub.Json(Fixture("azure-storage-hot-lrs-meter.json"));
        var none = await Body(wrongCurrency.ImportAsync(currency: "USD"), HttpStatusCode.Conflict);
        Assert.Contains("no row", none["title"]!.GetValue<string>());

        Assert.Empty(await azure.BookAsync());
        Assert.Empty(await wrongCurrency.BookAsync());
    }

    [Fact]
    public async Task A_failure_of_the_api_is_a_fixed_sentence_with_no_remote_text()
    {
        const string remote = "REMOTE-TEXT-9f3a";

        async Task<JsonObject> SearchAsync(Action<StubTransport> arrange, HttpStatusCode expected)
        {
            var azure = await CreateAsync();
            arrange(azure.Stub);
            var response = await azure.SearchAsync();
            var text = await response.Content.ReadAsStringAsync();
            Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode}: {text}");
            Assert.DoesNotContain(remote, text);
            Assert.Empty(await azure.BookAsync());
            return JsonNode.Parse(text)!.AsObject();
        }

        var badFilter = await SearchAsync(
            stub => stub.Json(HttpStatusCode.BadRequest, """{"Error":{"Code":"BadRequest","Message":"Invalid OData parameters supplied REMOTE"}}""".Replace("REMOTE", remote)),
            HttpStatusCode.BadRequest);
        Assert.Equal("price_source_refused", badFilter["type"]!.GetValue<string>());

        // The pipeline tries again after a 429; when the limit stays, the caller is told to wait.
        _host.Waits.Clear();
        var limited = await SearchAsync(stub => stub.Json(HttpStatusCode.TooManyRequests, remote, ("Retry-After", "2")), HttpStatusCode.ServiceUnavailable);
        Assert.Equal("price_source_rate_limited", limited["type"]!.GetValue<string>());
        Assert.Equal([TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2)], _host.Waits);

        var failures = new (Action<StubTransport> Arrange, string Detail)[]
        {
            (stub => stub.Json(HttpStatusCode.InternalServerError, remote), "The price API reported an error on its side."),
            (stub => stub.Json(remote), "The answer of the price API is not the documented format."),
            (stub => stub.Json("""{"Count":0}"""), "The answer of the price API is not the documented format."),
            (stub => stub.Throw(() => new HttpRequestException(HttpRequestError.ConnectionError, remote)), "No connection to the price API."),
            (stub => stub.Throw(() => new IOException(remote)), "No connection to the price API."),
            (stub => stub.Body(() => new MemoryStream(new byte[5 * 1024 * 1024]), "application/json"), "The answer of the price API is too large. Narrow the search."),
        };
        foreach (var (arrange, detail) in failures)
        {
            var failure = await SearchAsync(arrange, HttpStatusCode.BadGateway);
            Assert.Equal(("price_source_unavailable", detail), (failure["type"]!.GetValue<string>(), failure["detail"]!.GetValue<string>()));
        }
    }

    [Fact]
    public async Task A_search_value_that_could_change_the_filter_is_refused_before_any_request()
    {
        var azure = await CreateAsync();

        foreach (var query in new[]
                 {
                     "", "region=westeurope", "serviceName=Virtual%20Machines'%20or%20true", "serviceName=Storage&skuName=Hot'LRS",
                     "serviceName=Storage&currency=usd", $"serviceName={new string('a', 101)}", "serviceName=Storage&meterName=%20Data",
                 })
            await Body(azure.SearchAsync(query), HttpStatusCode.BadRequest);

        Assert.Empty(azure.Stub.Requests);
    }

    [Fact]
    public async Task Without_an_address_the_search_is_off()
    {
        var azure = await CreateAsync();
        await OutboundHost.PutSettingsAsync(azure.Owner, azure.WorkspaceId,
            new JsonObject { ["cost"] = new JsonObject { ["azureRetailPricesBaseUrl"] = "" } });

        var off = await Body(azure.SearchAsync(), HttpStatusCode.Conflict);

        Assert.Equal("price_source_not_configured", off["type"]!.GetValue<string>());
        Assert.Empty(azure.Stub.Requests);
    }

    /// <summary>
    /// A hosted instance starts closed to private addresses. The address of the price API is a
    /// setting, so it passes the same checks as every other outbound address.
    /// </summary>
    [Fact]
    public async Task A_hosted_instance_does_not_call_a_price_api_on_a_private_address()
    {
        var transport = new RoutedTransport();
        var addresses = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["mirror.internal.test"] = "10.0.0.5" };
        var database = await TestHost.CreateDatabaseAsync(shared.Fixture);
        await using var factory = TestHost.Create(database, TestHost.Hosted(TestHost.NewMasterKey()), services =>
        {
            TestHost.UseResolver(services, addresses);
            services.AddSingleton<OutboundPrimaryHandlerFactory>(_ => () => transport);
            services.AddSingleton(OutboundLimits.Default with { Delay = (_, _) => Task.CompletedTask, Jitter = () => 0 });
        });
        var (owner, created, _) = await TestHost.SetUpAsync(factory, TestHost.SetupToken, https: true);
        var settings = $"/api/workspaces/{created.WorkspaceId}/settings";
        Task<HttpResponseMessage> Save(string url) => owner.PutSettingsAsync(settings, new StringContent(
            new JsonObject { ["cost"] = new JsonObject { ["azureRetailPricesBaseUrl"] = url } }.ToJsonString(), Encoding.UTF8, "application/json"));

        // A literal private address, this machine and plain http are refused when the setting is saved.
        foreach (var url in new[] { "https://10.0.0.5", "https://localhost:8443", "http://prices.example.test", "https://user:pw@prices.example.test" })
            Assert.Equal(HttpStatusCode.BadRequest, (await Save(url)).StatusCode);

        // A name passes the save. It resolves to a private address when the request is made: nothing is sent.
        Assert.Equal(HttpStatusCode.OK, (await Save("https://mirror.internal.test")).StatusCode);
        var refused = await Body(owner.GetAsync($"/api/workspaces/{created.WorkspaceId}/prices/azure/meters?serviceName=Storage"), HttpStatusCode.BadGateway);

        Assert.Equal("The instance policy refuses the address of the price API.", refused["detail"]!.GetValue<string>());
        Assert.Empty(transport.For("mirror.internal.test").Requests);
    }
}
