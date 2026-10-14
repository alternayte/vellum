using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Vellum.Modules.Drafts;
using Vellum.Modules.Modelling;
using Vellum.Shared;

namespace Vellum.Tests.Modules.Schemas;

/// <summary>
/// A schema that a live branch or a stored revision names cannot be deleted, and a delete
/// cannot fall between the check that a schema exists and the write that names it.
/// </summary>
public class SchemaReferenceGuardTests(IntegrationFixture fixture) : IClassFixture<IntegrationFixture>
{
    private static string Url(Guid projectId, string path, Guid? branchId = null) =>
        $"/api/projects/{projectId}/{path}" + (branchId is null ? "" : $"?branchId={branchId}");

    private static async Task<(Guid Owner, Guid Schema)> SeedAsync(HttpClient client, Guid projectId)
    {
        Guid owner = Guid.NewGuid(), schema = Guid.NewGuid();
        await TestHost.Created(client.PostAsJsonAsync(Url(projectId, "elements"), new { id = owner, kind = "system", name = "Orders" }));
        await TestHost.Created(client.PostAsJsonAsync(Url(projectId, "schemas"), new { id = schema, name = "OrderRequest", content = """{"type":"object"}""" }));
        return (owner, schema);
    }

    private static async Task<ErrorResponse> ConflictAsync(Task<HttpResponseMessage> request)
    {
        var response = await request;
        Assert.True(response.StatusCode == HttpStatusCode.Conflict, $"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<ErrorResponse>())!;
    }

    [Fact]
    public async Task A_schema_that_a_revision_names_stays_so_a_variant_of_the_revision_has_it()
    {
        var factory = fixture.Factory;
        using var client = await TestHost.SignInAsync(factory);
        var (_, projectId) = await TestHost.CreateProjectAsync(client);
        var (owner, schema) = await SeedAsync(client, projectId);

        var operation = Guid.NewGuid();
        await TestHost.Created(client.PostAsJsonAsync(Url(projectId, "operations"),
            new { id = operation, ownerId = owner, name = "POST /orders", transport = "http", requestSchemaId = schema }));
        var revision = Guid.NewGuid();
        await TestHost.Created(client.PostAsJsonAsync(Url(projectId, "revisions"), new { id = revision, name = "v1" }));

        // Main lets go of the schema. The revision still names it.
        var cleared = await client.PatchAsJsonAsync(Url(projectId, $"operations/{operation}"), new { setRequestSchemaId = true });
        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);

        var refusal = await ConflictAsync(client.DeleteAsync(Url(projectId, $"schemas/{schema}")));
        Assert.Contains("revision 'v1'", refusal.Title);
        Assert.Contains("POST /orders", refusal.Title);

        // A variant forked from the revision names a schema that is still there.
        var variant = (await (await TestHost.Created(client.PostAsJsonAsync(Url(projectId, "variants"),
            new { id = Guid.NewGuid(), name = "From v1", baseRevisionId = revision }))).Content.ReadFromJsonAsync<VariantDto>())!;
        var operations = (await client.GetFromJsonAsync<JsonObject>(Url(projectId, "operations", variant.StreamId)))!;
        Assert.Equal(schema, (Guid)operations["items"]![0]!["requestSchemaId"]!);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(Url(projectId, $"schemas/{schema}"))).StatusCode);
    }

    [Fact]
    public async Task A_schema_that_a_message_or_an_open_branch_names_stays_until_the_reference_goes()
    {
        var factory = fixture.Factory;
        using var client = await TestHost.SignInAsync(factory);
        var (_, projectId) = await TestHost.CreateProjectAsync(client);
        var (owner, schema) = await SeedAsync(client, projectId);

        // A message on a draft is the only thing that names the schema.
        var draft = (await (await TestHost.Created(client.PostAsJsonAsync(Url(projectId, "drafts"), new { id = Guid.NewGuid(), name = "Events" })))
            .Content.ReadFromJsonAsync<DraftDto>())!;
        var message = Guid.NewGuid();
        await TestHost.Created(client.PostAsJsonAsync(Url(projectId, "messages", draft.StreamId),
            new { id = message, name = "OrderPlaced", producerId = owner, consumerIds = Array.Empty<Guid>(), schemaId = schema }));

        var refusal = await ConflictAsync(client.DeleteAsync(Url(projectId, $"schemas/{schema}")));
        Assert.Contains("message 'OrderPlaced'", refusal.Title);
        Assert.Contains("draft 'Events'", refusal.Title);

        var removed = await client.DeleteAsync(Url(projectId, $"messages/{message}", draft.StreamId));
        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync(Url(projectId, $"schemas/{schema}"))).StatusCode);

        // The schema is gone, so nothing new can name it.
        var late = await client.PostAsJsonAsync(Url(projectId, "operations"),
            new { id = Guid.NewGuid(), ownerId = owner, name = "Late", transport = "http", responseSchemaId = schema });
        Assert.Equal(HttpStatusCode.BadRequest, late.StatusCode);
        Assert.Equal("responseSchemaId", Assert.Single((await late.Content.ReadFromJsonAsync<ErrorResponse>())!.Errors!).Field);
    }

    [Fact]
    public async Task A_delete_that_races_a_new_reference_never_leaves_the_reference_dangling()
    {
        var factory = fixture.Factory;
        using var client = await TestHost.SignInAsync(factory);
        var (_, projectId) = await TestHost.CreateProjectAsync(client);
        var owner = Guid.NewGuid();
        await TestHost.Created(client.PostAsJsonAsync(Url(projectId, "elements"), new { id = owner, kind = "system", name = "Orders" }));

        // Each round: one schema, and at the same instant a delete of it, an operation and a
        // message that name it, and a draft that copies whatever main names.
        for (var round = 0; round < 12; round++)
        {
            var schema = Guid.NewGuid();
            await TestHost.Created(client.PostAsJsonAsync(Url(projectId, "schemas"), new { id = schema, name = $"S{round}", content = """{"type":"object"}""" }));

            Guid operation = Guid.NewGuid(), message = Guid.NewGuid();
            var requests = new[]
            {
                client.DeleteAsync(Url(projectId, $"schemas/{schema}")),
                client.PostAsJsonAsync(Url(projectId, "operations"),
                    new { id = operation, ownerId = owner, name = $"op {round}", transport = "http", requestSchemaId = schema }),
                client.PostAsJsonAsync(Url(projectId, "messages"),
                    new { id = message, name = $"msg {round}", producerId = owner, consumerIds = Array.Empty<Guid>(), schemaId = schema }),
                client.PostAsJsonAsync(Url(projectId, "drafts"), new { id = Guid.NewGuid(), name = $"draft {round}" }),
            };
            var responses = await Task.WhenAll(requests);

            foreach (var response in responses)
                Assert.True((int)response.StatusCode < 500, $"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}");

            var deleted = responses[0].StatusCode == HttpStatusCode.NoContent;
            var named = responses[1].StatusCode == HttpStatusCode.Created || responses[2].StatusCode == HttpStatusCode.Created;

            // Exactly one side wins: the schema is deleted, or something names it.
            Assert.True(deleted != named, $"round {round}: delete {responses[0].StatusCode}, operation {responses[1].StatusCode}, message {responses[2].StatusCode}");
            Assert.Equal(deleted ? HttpStatusCode.NotFound : HttpStatusCode.OK,
                (await client.GetAsync(Url(projectId, $"schemas/{schema}"))).StatusCode);
        }
    }
}
