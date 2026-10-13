using System.Net;
using System.Net.Http.Json;
using System.Text;
using Vellum.Shared;

namespace Vellum.Tests.Modules.Modelling;

/// <summary>
/// The YAML export is a document the import reads. Before, the import refused every YAML
/// export, the one of an empty project included.
/// </summary>
[Collection("Integration")]
public class YamlRoundTripTests(IntegrationFixture fixture)
{
    private static string Url(Guid projectId, string path) => $"/api/projects/{projectId}/{path}";

    private static StringContent Yaml(string body) => new(body, Encoding.UTF8, "application/yaml");

    private static async Task ImportedAsync(HttpClient client, Guid projectId, string yaml)
    {
        var response = await client.PostAsync(Url(projectId, "import"), Yaml(yaml));
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}\n{yaml}");
    }

    [Fact]
    public async Task The_yaml_export_of_an_empty_project_imports()
    {
        using var factory = TestHost.Create(fixture);
        using var client = await TestHost.SignInAsync(factory);
        var (_, projectId) = await TestHost.CreateProjectAsync(client);

        var yaml = await client.GetStringAsync(Url(projectId, "export?format=yaml"));

        await ImportedAsync(client, projectId, yaml);
    }

    [Fact]
    public async Task A_yaml_export_restores_elements_messages_schemas_contracts_and_facts()
    {
        using var factory = TestHost.Create(fixture);
        using var client = await TestHost.SignInAsync(factory);
        var (_, projectId) = await TestHost.CreateProjectAsync(client);

        Guid orders = Guid.NewGuid(), billing = Guid.NewGuid(), relationship = Guid.NewGuid(), message = Guid.NewGuid();
        Guid schema = Guid.NewGuid(), operation = Guid.NewGuid(), events = Guid.NewGuid(), deadLetters = Guid.NewGuid(), subscription = Guid.NewGuid();

        // Values that YAML could misread: a name that looks like a number, one that looks like
        // a boolean, text of several lines, a colon, quotes, and JSON inside a string.
        await TestHost.Created(client.PostAsJsonAsync(Url(projectId, "schemas"), new
        {
            id = schema, name = "OrderRequest", description = "Request: \"place an order\"",
            content = """{ "type": "object", "required": ["sku"], "properties": { "sku": { "type": "string", "pattern": "^[a-z]+: #\\d+$" } } }""",
        }));
        await TestHost.Created(client.PostAsJsonAsync(Url(projectId, "elements"), new
        {
            id = orders, kind = "system", name = "123", description = "First line\nSecond line: with a colon\n\tand a tab",
            technology = "true", tags = new[] { "core", "null", "007" },
        }));
        await TestHost.Created(client.PostAsJsonAsync(Url(projectId, "elements"), new { id = billing, kind = "system", name = "Billing # not a comment" }));
        var fact = await client.PatchAsJsonAsync(Url(projectId, $"elements/{billing}"), new
        {
            icon = "credit-card", factStatus = "inferred", setProvenance = true,
            provenance = new { source = "github.com/acme/billing@abc123:src/Billing.cs:1-9", revision = "abc123", confidence = 0.5 },
        });
        Assert.Equal(HttpStatusCode.OK, fact.StatusCode);
        await TestHost.Created(client.PostAsJsonAsync(Url(projectId, "relationships"),
            new { id = relationship, fromId = orders, toId = billing, label = "charges", technology = "gRPC", kind = "behavioural" }));
        await TestHost.Created(client.PostAsJsonAsync(Url(projectId, "messages"),
            new { id = message, name = "OrderPlaced", producerId = orders, consumerIds = new[] { billing }, schemaId = schema, tags = new[] { "event" } }));
        await TestHost.Created(client.PostAsJsonAsync(Url(projectId, "operations"), new
        {
            id = operation, ownerId = orders, name = "POST /orders", transport = "http",
            requestSchemaId = schema, timeoutMs = 2000, retry = new { maxAttempts = 3, backoffMs = 250 },
        }));
        await TestHost.Created(client.PostAsJsonAsync(Url(projectId, "channels"), new { id = events, name = "order-events", kind = "topic" }));
        await TestHost.Created(client.PostAsJsonAsync(Url(projectId, "channels"), new { id = deadLetters, name = "order-events.dlq", kind = "queue" }));
        await TestHost.Created(client.PostAsJsonAsync(Url(projectId, "subscriptions"),
            new { id = subscription, channelId = events, consumerId = billing, retry = new { maxAttempts = 2, backoffMs = 100 }, deadLetterChannelId = deadLetters }));
        var observed = await client.PatchAsJsonAsync(Url(projectId, $"channels/{events}"), new
        {
            factStatus = "observed-in-source", setProvenance = true, provenance = new { source = "repo@abc:infra/topics.tf:4" },
        });
        Assert.Equal(HttpStatusCode.OK, observed.StatusCode);

        var before = await client.GetStringAsync(Url(projectId, "export?format=json"));
        var yaml = await client.GetStringAsync(Url(projectId, "export?format=yaml"));

        // Change what the document holds, so that the import has something to restore.
        foreach (var (path, change) in new (string, object)[]
        {
            ($"elements/{orders}", new { name = "Changed", description = "Changed", technology = "Changed", tags = Array.Empty<string>() }),
            ($"elements/{billing}", new { icon = "x", factStatus = "declared", setProvenance = true }),
            ($"relationships/{relationship}", new { label = "Changed", kind = "structural" }),
            ($"messages/{message}", new { name = "Changed", setSchemaId = true }),
            ($"operations/{operation}", new { name = "Changed", timeoutMs = 1, setRequestSchemaId = true, retry = new { maxAttempts = 1, backoffMs = 0 } }),
            ($"channels/{events}", new { name = "changed", kind = "queue" }),
            ($"subscriptions/{subscription}", new { setDeadLetterChannelId = true, retry = new { maxAttempts = 1, backoffMs = 0 } }),
            ($"schemas/{schema}", new { name = "Changed", description = "Changed" }),
        })
        {
            var patched = await client.PatchAsJsonAsync(Url(projectId, path), change);
            Assert.True(patched.StatusCode == HttpStatusCode.OK, $"{path}: {await patched.Content.ReadAsStringAsync()}");
        }
        Assert.NotEqual(before, await client.GetStringAsync(Url(projectId, "export?format=json")));

        await ImportedAsync(client, projectId, yaml);

        // The acceptance stamp of a fact that the changes above reset is the stamp of the document.
        Assert.Equal(before, await client.GetStringAsync(Url(projectId, "export?format=json")));
        Assert.Equal(yaml, await client.GetStringAsync(Url(projectId, "export?format=yaml")));
    }

    [Theory]
    [InlineData("a: &x [1, 2]\nb: *x\n")]
    [InlineData("- just\n- a list\n")]
    [InlineData("elements: [ { id: not-a-guid } ]\n")]
    [InlineData("version: '1.0'\nelements: [[[[[[[[[[[[[[[[[[[[1]]]]]]]]]]]]]]]]]]]]\n")]
    [InlineData("version: \"unterminated\nelements: [\n")]
    public async Task Yaml_that_is_not_an_export_document_is_a_400(string yaml)
    {
        using var factory = TestHost.Create(fixture);
        using var client = await TestHost.SignInAsync(factory);
        var (_, projectId) = await TestHost.CreateProjectAsync(client);

        var response = await client.PostAsync(Url(projectId, "import"), Yaml(yaml));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_export", (await response.Content.ReadFromJsonAsync<ErrorResponse>())!.Type);
    }
}
