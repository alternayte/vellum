using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Vellum.Modules.Identity;

namespace Vellum.Tests.Modules.Modelling;

/// <summary>
/// A person changes a truth label only by "Accept as declared" and "Mark as unknown"
///. The server stamps who and when and keeps the source of the fact.
/// </summary>
public class FactLabelEndpointTests(IntegrationFixture fixture) : IClassFixture<IntegrationFixture>
{
    [Fact]
    public async Task Accepting_an_inferred_fact_declares_it_with_the_acceptor_and_keeps_its_source()
    {
        var client = await TestHost.SignInAsync(fixture.Factory);
        var (_, projectId) = await TestHost.CreateProjectAsync(client);
        var me = (await client.GetFromJsonAsync<UserInfoResponse>("/api/auth/me"))!.Id;
        var url = (string path) => $"/api/projects/{projectId}/{path}";

        var billing = Guid.NewGuid();
        await TestHost.Created(client.PostAsJsonAsync(url("elements"), new { id = billing, kind = "system", name = "Billing" }));
        var inferred = await client.PatchAsJsonAsync(url($"elements/{billing}"), new
        {
            factStatus = "inferred", setProvenance = true,
            provenance = new { source = "github.com/acme/billing@abc:src/Billing.cs:1-9", revision = "abc", confidence = 0.4 },
        });
        Assert.Equal(HttpStatusCode.OK, inferred.StatusCode);

        // A revision names the version of main that the acceptance reads.
        var before = await client.PostAsJsonAsync(url("revisions"), new { id = Guid.NewGuid(), name = "before acceptance" });
        var modelVersion = (await before.Content.ReadFromJsonAsync<JsonObject>())!["version"]!.GetValue<long>();
        var mainStream = (await client.GetFromJsonAsync<JsonObject>($"/api/projects/{projectId}"))!["streamId"]!.GetValue<Guid>();

        // The request has no body: a person cannot name the label it gets.
        var accepted = await client.PostAsync(url($"facts/element/{billing}/accept"), null);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        var label = (await accepted.Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.Equal("declared", label["factStatus"]!.GetValue<string>());

        var element = (await client.GetFromJsonAsync<JsonObject>(url($"elements/{billing}")))!;
        var provenance = element["provenance"]!;
        Assert.Equal("declared", element["factStatus"]!.GetValue<string>());
        Assert.Equal(
            ("github.com/acme/billing@abc:src/Billing.cs:1-9", "abc", 0.4, me),
            (provenance["source"]!.GetValue<string>(), provenance["revision"]!.GetValue<string>(),
                provenance["confidence"]!.GetValue<double>(), provenance["acceptedBy"]!.GetValue<string>()));
        Assert.NotNull(provenance["acceptedAt"]);
        Assert.Equal((modelVersion, mainStream),
            (provenance["acceptedAtVersion"]!.GetValue<long>(), provenance["acceptedOnBranch"]!.GetValue<Guid>()));
        Assert.Equal(modelVersion, label["provenance"]!["acceptedAtVersion"]!.GetValue<long>());

        // A declared fact is not accepted again. It can be marked unknown, and back.
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync(url($"facts/element/{billing}/accept"), null)).StatusCode);
        var unknown = await client.PostAsync(url($"facts/element/{billing}/mark-unknown"), null);
        Assert.Equal("unknown", (await unknown.Content.ReadFromJsonAsync<JsonObject>())!["factStatus"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync(url($"facts/element/{billing}/mark-unknown"), null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync(url($"facts/element/{billing}/accept"), null)).StatusCode);
    }

    [Fact]
    public async Task Neither_action_gives_or_removes_a_label_that_claims_evidence()
    {
        var client = await TestHost.SignInAsync(fixture.Factory);
        var (_, projectId) = await TestHost.CreateProjectAsync(client);
        var url = (string path) => $"/api/projects/{projectId}/{path}";

        var api = Guid.NewGuid();
        var store = Guid.NewGuid();
        await TestHost.Created(client.PostAsJsonAsync(url("elements"), new { id = api, kind = "system", name = "API" }));
        await TestHost.Created(client.PostAsJsonAsync(url("elements"), new { id = store, kind = "system", name = "Store" }));
        await client.PatchAsJsonAsync(url($"elements/{api}"), new
        {
            factStatus = "measured", setProvenance = true, provenance = new { source = "k6-result:abc" },
        });

        // A measured fact is neither accepted nor marked unknown: its label comes from evidence.
        var refused = await client.PostAsync(url($"facts/element/{api}/accept"), null);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains("measured", await refused.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync(url($"facts/element/{api}/mark-unknown"), null)).StatusCode);
        Assert.Equal("measured", (await client.GetFromJsonAsync<JsonObject>(url($"elements/{api}")))!["factStatus"]!.GetValue<string>());

        // An observed-in-source contract is accepted as declared; the action works on every kind of fact.
        var operation = Guid.NewGuid();
        await TestHost.Created(client.PostAsJsonAsync(url("operations"), new
        {
            id = operation, ownerId = store, name = "GET /stock", transport = "http", timeoutMs = 500, retry = new { maxAttempts = 1, backoffMs = 0 },
        }));
        await client.PatchAsJsonAsync(url($"operations/{operation}"), new
        {
            factStatus = "observed-in-source", setProvenance = true, provenance = new { source = "github.com/acme/store@def:Stock.cs:4" },
        });
        var declared = await client.PostAsync(url($"facts/operation/{operation}/accept"), null);
        Assert.Equal(HttpStatusCode.OK, declared.StatusCode);
        var label = (await declared.Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.Equal(("declared", "github.com/acme/store@def:Stock.cs:4"),
            (label["factStatus"]!.GetValue<string>(), label["provenance"]!["source"]!.GetValue<string>()));

        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync(url($"facts/schema/{operation}/accept"), null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync(url($"facts/channel/{operation}/accept"), null)).StatusCode);
    }
}
