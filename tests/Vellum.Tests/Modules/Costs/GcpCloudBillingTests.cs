using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Vellum.Tests.Shared.Outbound;

namespace Vellum.Tests.Modules.Costs;

/// <summary>
/// The Google Cloud Billing Catalog API adapter through the real Refit client and handler
/// pipeline. The transport serves fixtures under <c>Fixtures/Costs</c> in the documented shapes
/// of <c>services.list</c> and <c>services.skus.list</c>. No test opens a socket.
/// </summary>
public class GcpCloudBillingTests(SharedOutboundHost shared) : IClassFixture<SharedOutboundHost>
{
    private const string Host = "cloudbilling.googleapis.com";
    private const string Compute = "6F81-5844-456A";
    private const string Key = "AIzaSyTEST-key-0123456789abcdefghijklmn";

    private readonly OutboundHost _host = shared.Host;

    private async Task<(HttpClient Owner, Guid WorkspaceId, StubTransport Stub)> CreateAsync(bool withKey = true)
    {
        var (owner, workspaceId) = await _host.NewWorkspaceAsync();
        if (withKey)
            await OutboundHost.PutSecretAsync(owner, workspaceId, "gcp:apiKey", Key);
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
    public async Task Without_the_key_nothing_is_sent_and_the_answer_says_which_secret_to_save()
    {
        var (owner, workspaceId, stub) = await CreateAsync(withKey: false);

        var refused = await Body(owner.GetAsync(Url(workspaceId, "/gcp/services")), HttpStatusCode.Conflict);

        Assert.Equal("price_source_not_configured", refused["type"]!.GetValue<string>());
        Assert.Contains("gcp:apiKey", refused["detail"]!.GetValue<string>());
        Assert.Empty(stub.Requests);
    }

    [Fact]
    public async Task The_key_goes_to_the_cloud_billing_api_in_a_header_and_to_no_other_host()
    {
        var (owner, workspaceId, stub) = await CreateAsync();
        stub.Json(Fixture("gcp-services.json"));

        var services = await Body(owner.GetAsync(Url(workspaceId, "/gcp/services")), HttpStatusCode.OK);

        var sent = Assert.Single(stub.Requests);
        Assert.Equal(($"https://{Host}/v1/services", "?pageSize=5000"), (sent.Uri.GetLeftPart(UriPartial.Path), sent.Uri.Query));
        Assert.Equal(Key, sent.Header("X-Goog-Api-Key"));
        Assert.Null(sent.Header("Authorization"));
        Assert.Equal(["Cloud Storage", "Compute Engine"], services["services"]!.AsArray().Select(s => s!["displayName"]!.GetValue<string>()));

        // The secret is bound to the origin of the API.
        var status = (await owner.GetFromJsonAsync<JsonArray>($"/api/workspaces/{workspaceId}/secrets"))!
            .Single(s => s!["name"]!.GetValue<string>() == "gcp:apiKey")!;
        Assert.Equal(($"https://{Host}", true), (status["boundOrigin"]!.GetValue<string>(), status["matchesCurrentUrl"]!.GetValue<bool>()));

        // A redirect to another host is not followed, so the key does not go there.
        var elsewhere = $"{Guid.NewGuid():N}.elsewhere.test";
        _host.Transport.Forget(Host);
        _host.Transport.For(Host).Json(HttpStatusCode.Found, "{}", ("Location", $"https://{elsewhere}/v1/services"));
        await Body(owner.GetAsync(Url(workspaceId, "/gcp/services")), HttpStatusCode.BadGateway);
        Assert.Empty(_host.Transport.For(elsewhere).Requests);

        // The other price sources of the workspace never get it.
        var aws = _host.Transport.For("pricing.us-east-1.amazonaws.com");
        aws.Json(Fixture("aws-offer-index.json"));
        await Body(owner.GetAsync(Url(workspaceId, "/aws/services")), HttpStatusCode.OK);
        var azureHost = $"{Guid.NewGuid():N}.prices.test";
        await OutboundHost.PutSettingsAsync(owner, workspaceId,
            new JsonObject { ["cost"] = new JsonObject { ["azureRetailPricesBaseUrl"] = $"https://{azureHost}" } });
        _host.Transport.For(azureHost).Json(Fixture("azure-vm-d2s-v5.json"));
        await Body(owner.GetAsync(Url(workspaceId, "/azure/meters?serviceName=Virtual%20Machines")), HttpStatusCode.OK);
        foreach (var request in aws.Requests.Concat(_host.Transport.For(azureHost).Requests))
            Assert.DoesNotContain(request.Headers, h => h.Value.Contains(Key, StringComparison.Ordinal));
        Assert.DoesNotContain(_host.Logs.Lines, line => line.Contains(Key, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_search_reads_every_page_keeps_the_skus_of_the_region_and_an_import_stores_them_with_their_source()
    {
        var (owner, workspaceId, stub) = await CreateAsync();
        stub.Json(Fixture("gcp-compute-skus-page-1.json").Replace("__NEXT__", "page-2-token"))
            .Json(Fixture("gcp-compute-skus-page-2.json"));

        var page = await Body(owner.GetAsync(Url(workspaceId, $"/gcp/skus?serviceId={Compute}&region=europe-west1&currency=USD")), HttpStatusCode.OK);

        Assert.Equal(2, stub.Requests.Count);
        Assert.Equal($"/v1/services/{Compute}/skus", stub.Requests[0].Uri.AbsolutePath);
        Assert.Equal("?currencyCode=USD&pageSize=5000", stub.Requests[0].Uri.Query);
        Assert.Equal("?currencyCode=USD&pageSize=5000&pageToken=page-2-token", stub.Requests[1].Uri.Query);
        Assert.DoesNotContain(Key, page.ToJsonString());

        // The SKU of the Americas is not sold in europe-west1.
        var items = page["items"]!.AsArray();
        Assert.Equal(["0C5C-D8E4-38C1", "9DE9-9092-B3BC", "D973-5D65-BAB2"], items.Select(i => i!["skuId"]!.GetValue<string>()));
        var core = items[0]!;
        Assert.Equal(("hour", "h", 0.0348119m, "USD", "2026-10-13", "N2Standard"),
            (core["unit"]!.GetValue<string>(), core["usageUnit"]!.GetValue<string>(), core["unitPrice"]!.GetValue<decimal>(),
                core["currency"]!.GetValue<string>(), core["effectiveDate"]!.GetValue<string>(), core["resourceGroup"]!.GetValue<string>()));
        Assert.Null(core["notImportable"]);
        Assert.Contains("tiers", items[1]!["notImportable"]!.GetValue<string>());
        Assert.Equal("GiB/month", items[2]!["unit"]!.GetValue<string>());

        _host.Transport.Forget(Host);
        _host.Transport.For(Host).Json(Fixture("gcp-compute-skus-page-1.json").Replace("__NEXT__", "page-2-token"))
            .Json(Fixture("gcp-compute-skus-page-2.json"));
        var stored = (await owner.PostAsJsonAsync(Url(workspaceId, "/gcp"),
            new { serviceId = Compute, currency = "USD", skuIds = new[] { "0C5C-D8E4-38C1", "D973-5D65-BAB2" } })).EnsureSuccessStatusCode();
        var entries = (await stored.Content.ReadFromJsonAsync<JsonArray>())!;
        Assert.Equal(2, entries.Count);
        var entry = entries[0]!;
        Assert.Equal(("gcp", "europe-west1", "N2 Instance Core running in Belgium", "hour", 0.0348119m, "2026-10-13"),
            (entry["provider"]!.GetValue<string>(), entry["region"]!.GetValue<string>(), entry["sku"]!.GetValue<string>(),
                entry["unit"]!.GetValue<string>(), entry["unitPrice"]!.GetValue<decimal>(), entry["effectiveDate"]!.GetValue<string>()));
        Assert.Equal(("0C5C-D8E4-38C1", $"https://{Host}/v1/services/{Compute}/skus?currencyCode=USD&pageSize=5000"),
            (entry["fetched"]!["meterId"]!.GetValue<string>(), entry["fetched"]!["url"]!.GetValue<string>()));
        Assert.True(Guid.TryParse(entry["acceptedBy"]!.GetValue<string>(), out _));

        var audit = (await owner.GetFromJsonAsync<JsonArray>($"/api/workspaces/{workspaceId}/audit"))!;
        Assert.Equal(2, audit.Count(a => a!["action"]!.GetValue<string>() == "price.imported"));
        Assert.DoesNotContain(Key, audit.ToJsonString());

        // A SKU with tiers is refused, and nothing of the pick is stored.
        _host.Transport.Forget(Host);
        _host.Transport.For(Host).Json(Fixture("gcp-compute-skus-page-1.json").Replace("__NEXT__", "page-2-token"))
            .Json(Fixture("gcp-compute-skus-page-2.json"));
        await Body(owner.PostAsJsonAsync(Url(workspaceId, "/gcp"),
            new { serviceId = Compute, currency = "USD", skuIds = new[] { "0D6A-5B42-9C2E", "9DE9-9092-B3BC" } }), HttpStatusCode.Conflict);
        Assert.Equal(2, (await owner.GetFromJsonAsync<JsonObject>(Url(workspaceId, "")))!["items"]!.AsArray().Count);
    }

    [Fact]
    public async Task A_key_that_google_refuses_is_a_fixed_sentence_with_no_remote_text()
    {
        const string remote = "REMOTE-TEXT-77ab";
        var (owner, workspaceId, stub) = await CreateAsync();
        stub.Json(HttpStatusCode.BadRequest, $$$"""{"error":{"code":400,"message":"API key not valid. {{{remote}}}","status":"INVALID_ARGUMENT"}}""");

        var response = await owner.GetAsync(Url(workspaceId, $"/gcp/skus?serviceId={Compute}"));
        var text = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.DoesNotContain(remote, text);
        Assert.Contains("gcp:apiKey", JsonNode.Parse(text)!["detail"]!.GetValue<string>());
    }
}
