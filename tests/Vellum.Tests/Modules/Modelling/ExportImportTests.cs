using Vellum.Modules.Modelling.Export;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vellum.Modules.Modelling.Elements;
using Vellum.Modules.Modelling.Messages;
using Vellum.Modules.Modelling.Relationships;
using Vellum.Modules.Schemas;
using Vellum.Shared;

namespace Vellum.Tests.Modules.Modelling;

public class ExportImportTests : IClassFixture<IntegrationFixture>
{
    private readonly IntegrationFixture _fixture;

    public ExportImportTests(IntegrationFixture fixture) => _fixture = fixture;

    private static HttpClient CreateClient(WebApplicationFactory<Program> factory)
    {
        return factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            HandleCookies = true,
            AllowAutoRedirect = false
        });
    }

    private static async Task<HttpClient> CreateAuthenticatedClientAsync(WebApplicationFactory<Program> factory)
    {
        var client = CreateClient(factory);
        var email = $"test-{Guid.NewGuid():N}@vellum.local";
        await client.PostAsJsonAsync("/api/auth/register",
            new { email, password = "Test12345!", displayName = "Test User" });
        return client;
    }

    private static async Task<Guid> SetupProjectAsync(HttpClient client)
    {
        var workspaceId = Guid.NewGuid();
        await client.PostAsJsonAsync("/api/workspaces", new { id = workspaceId, name = "Test WS" });
        var projectId = Guid.NewGuid();
        await client.PostAsJsonAsync($"/api/workspaces/{workspaceId}/projects",
            new { id = projectId, name = "Test Project" });
        return projectId;
    }

    [Fact]
    public async Task Export_json_returns_ok()
    {
        var factory = _fixture.Factory;
        using var client = await CreateAuthenticatedClientAsync(factory);
        var projectId = await SetupProjectAsync(client);

        await client.PostAsJsonAsync($"/api/projects/{projectId}/elements",
            new { id = Guid.NewGuid(), kind = "system", name = "Orders" });

        var response = await client.GetAsync($"/api/projects/{projectId}/export?format=json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var content = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"version\"", content);
        Assert.Contains("1.0", content);
        Assert.Contains("Orders", content);
    }

    /// <summary>A document that leaves out what the importer reads is refused or read with defaults, never a 500.</summary>
    [Theory]
    [InlineData("""{ "elements": [ null ] }""", HttpStatusCode.BadRequest)]
    [InlineData("""{ "channels": [ null ] }""", HttpStatusCode.BadRequest)]
    [InlineData("""{ "elements": [ { "id": "ID1" } ] }""", HttpStatusCode.BadRequest)]
    [InlineData("""{ "elements": [ { "id": "ID1", "kind": "system", "name": "No status" } ] }""", HttpStatusCode.BadRequest)]
    [InlineData("""{ "elements": [ { "id": "ID1", "kind": "system", "name": "No tags", "status": "current" } ] }""", HttpStatusCode.OK)]
    [InlineData("""{ "elements": [ { "id": "ID1", "kind": "system", "name": "A", "status": "current" } ], "messages": [ { "id": "ID2", "name": "No lists", "producerId": "ID1" } ] }""", HttpStatusCode.OK)]
    [InlineData("""{ "schemas": [ { "id": "ID1", "name": "No content" } ] }""", HttpStatusCode.BadRequest)]
    [InlineData("""{ "channels": [ { "id": "ID1" } ] }""", HttpStatusCode.BadRequest)]
    [InlineData("""{ "elements": [ { "id": "ID1", "kind": "system", "name": "A", "status": "current" } ], "operations": [ { "id": "ID2", "ownerId": "ID1" } ] }""", HttpStatusCode.BadRequest)]
    public async Task An_incomplete_import_document_is_refused_or_read_with_defaults(string document, HttpStatusCode expected)
    {
        var factory = _fixture.Factory;
        using var client = await CreateAuthenticatedClientAsync(factory);
        var projectId = await SetupProjectAsync(client);

        var json = document.Replace("ID1", Guid.NewGuid().ToString()).Replace("ID2", Guid.NewGuid().ToString());
        var response = await client.PostAsync($"/api/projects/{projectId}/import",
            new StringContent(json, System.Text.Encoding.UTF8, "application/json"));

        Assert.True(response.StatusCode == expected, $"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
    }

    [Fact]
    public async Task Export_yaml_returns_ok()
    {
        var factory = _fixture.Factory;
        using var client = await CreateAuthenticatedClientAsync(factory);
        var projectId = await SetupProjectAsync(client);

        var response = await client.GetAsync($"/api/projects/{projectId}/export?format=yaml");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var content = await response.Content.ReadAsStringAsync();
        Assert.Contains("version:", content);
    }

    [Fact]
    public async Task Export_then_import_round_trips()
    {
        var factory = _fixture.Factory;
        using var client = await CreateAuthenticatedClientAsync(factory);
        var projectId = await SetupProjectAsync(client);

        await client.PostAsJsonAsync($"/api/projects/{projectId}/elements",
            new { id = Guid.NewGuid(), kind = "system", name = "Orders" });

        var export = await client.GetAsync($"/api/projects/{projectId}/export?format=json");
        Assert.Equal(HttpStatusCode.OK, export.StatusCode);
        var json = await export.Content.ReadAsStringAsync();

        // Import is idempotent — reimporting back to same project should succeed
        var import = await client.PostAsync(
            $"/api/projects/{projectId}/import",
            new StringContent(json, System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, import.StatusCode);
    }

    [Fact]
    public async Task Facts_and_kinds_survive_export_and_import_between_projects()
    {
        var factory = _fixture.Factory;
        using var client = await CreateAuthenticatedClientAsync(factory);
        var sourceProjectId = await SetupProjectAsync(client);

        var ordersId = Guid.NewGuid();
        await client.PostAsJsonAsync($"/api/projects/{sourceProjectId}/elements",
            new { id = ordersId, kind = "system", name = "Orders" });
        var paymentsId = Guid.NewGuid();
        await client.PostAsJsonAsync($"/api/projects/{sourceProjectId}/elements",
            new { id = paymentsId, kind = "system", name = "Payments" });

        var patch = await client.PatchAsJsonAsync($"/api/projects/{sourceProjectId}/elements/{ordersId}",
            new
            {
                factStatus = "observed-in-source",
                provenance = new { source = "github.com/acme/orders@abc123:src/Orders.cs:1-20", revision = "abc123" },
                setProvenance = true,
            });
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);

        var relId = Guid.NewGuid();
        await client.PostAsJsonAsync($"/api/projects/{sourceProjectId}/relationships",
            new { id = relId, fromId = ordersId, toId = paymentsId, label = "calls", kind = "behavioural" });

        var messageId = Guid.NewGuid();
        await client.PostAsJsonAsync($"/api/projects/{sourceProjectId}/messages",
            new
            {
                id = messageId,
                name = "OrderPlaced",
                producerId = ordersId,
                consumerIds = new[] { paymentsId },
                tags = Array.Empty<string>(),
            });
        var messageFacts = await client.PatchAsJsonAsync(
            $"/api/projects/{sourceProjectId}/messages/{messageId}",
            new
            {
                factStatus = "inferred",
                provenance = new
                {
                    source = "github.com/acme/orders@abc123:src/Events.cs:8-14",
                    revision = "abc123",
                    confidence = 0.7,
                },
                setProvenance = true,
            });
        Assert.Equal(HttpStatusCode.OK, messageFacts.StatusCode);

        var export = await client.GetStringAsync($"/api/projects/{sourceProjectId}/export?format=json");
        using (var document = JsonDocument.Parse(export))
        {
            var exportedOrders = document.RootElement.GetProperty("elements").EnumerateArray()
                .Single(e => e.GetProperty("id").GetString() == ordersId.ToString());
            Assert.Equal("observed-in-source", exportedOrders.GetProperty("factStatus").GetString());
            Assert.Equal("github.com/acme/orders@abc123:src/Orders.cs:1-20",
                exportedOrders.GetProperty("provenance").GetProperty("source").GetString());

            var exportedRelationship = Assert.Single(
                document.RootElement.GetProperty("relationships").EnumerateArray());
            Assert.Equal("behavioural", exportedRelationship.GetProperty("kind").GetString());

            var exportedMessage = Assert.Single(
                document.RootElement.GetProperty("messages").EnumerateArray());
            Assert.Equal("inferred", exportedMessage.GetProperty("factStatus").GetString());
            Assert.Equal(0.7, exportedMessage.GetProperty("provenance").GetProperty("confidence").GetDouble());
        }

        // The same pinned model exports identical content.
        var exportAgain = await client.GetStringAsync($"/api/projects/{sourceProjectId}/export?format=json");
        Assert.Equal(export, exportAgain);

        var targetProjectId = await SetupProjectAsync(client);
        var import = await client.PostAsync($"/api/projects/{targetProjectId}/import",
            new StringContent(export, System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, import.StatusCode);

        var elements = await client.GetFromJsonAsync<Page<ElementDto>>(
            $"/api/projects/{targetProjectId}/elements");
        var imported = Assert.Single(elements!.Items, e => e.Id == ordersId);
        Assert.Equal("observed-in-source", imported.FactStatus);
        Assert.Equal("github.com/acme/orders@abc123:src/Orders.cs:1-20", imported.Provenance!.Source);
        Assert.Equal("abc123", imported.Provenance.Revision);

        var relationships = await client.GetFromJsonAsync<Page<RelationshipDto>>(
            $"/api/projects/{targetProjectId}/relationships");
        Assert.Equal("behavioural", Assert.Single(relationships!.Items).Kind);

        var messages = await client.GetFromJsonAsync<Page<MessageDto>>(
            $"/api/projects/{targetProjectId}/messages");
        var importedMessage = Assert.Single(messages!.Items);
        Assert.Equal("inferred", importedMessage.FactStatus);
        Assert.Equal("github.com/acme/orders@abc123:src/Events.cs:8-14", importedMessage.Provenance!.Source);
        Assert.Equal(0.7, importedMessage.Provenance.Confidence);
    }

    [Fact]
    public async Task Import_rejects_invalid_document_with_400()
    {
        var factory = _fixture.Factory;
        using var client = await CreateAuthenticatedClientAsync(factory);
        var projectId = await SetupProjectAsync(client);

        var document = new
        {
            version = "1.0",
            project = new { name = "Invalid" },
            elements = new[]
            {
                new
                {
                    id = Guid.NewGuid(),
                    kind = "app",
                    name = "API",
                    status = "current",
                    parentId = (Guid?)null,
                    tags = Array.Empty<string>(),
                    icon = "box",
                },
            },
            relationships = Array.Empty<object>(),
            messages = Array.Empty<object>(),
            schemas = Array.Empty<object>(),
        };

        var response = await client.PostAsJsonAsync($"/api/projects/{projectId}/import", document);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Import_with_document_schema_and_message_reference_succeeds()
    {
        var factory = _fixture.Factory;
        using var client = await CreateAuthenticatedClientAsync(factory);
        var projectId = await SetupProjectAsync(client);

        var producerId = Guid.NewGuid();
        await client.PostAsJsonAsync($"/api/projects/{projectId}/elements",
            new { id = producerId, kind = "system", name = "Orders" });

        var schemaId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var document = new
        {
            version = "1.0",
            project = new { name = "WithSchema" },
            elements = Array.Empty<object>(),
            relationships = Array.Empty<object>(),
            messages = new[]
            {
                new
                {
                    id = messageId,
                    name = "OrderPlaced",
                    producerId,
                    consumerIds = Array.Empty<Guid>(),
                    schemaId,
                    tags = Array.Empty<string>(),
                },
            },
            schemas = new[]
            {
                new
                {
                    id = schemaId,
                    name = "OrderPlaced",
                    description = (string?)null,
                    content = "{\"type\":\"object\"}",
                    version = 1,
                },
            },
        };

        var response = await client.PostAsJsonAsync($"/api/projects/{projectId}/import", document);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var messages = await client.GetFromJsonAsync<Page<MessageDto>>($"/api/projects/{projectId}/messages");
        var importedMessage = Assert.Single(messages!.Items, m => m.Id == messageId);
        Assert.Equal(schemaId, importedMessage.SchemaId);

        var schema = await client.GetAsync($"/api/projects/{projectId}/schemas/{schemaId}");
        Assert.Equal(HttpStatusCode.OK, schema.StatusCode);
    }

    [Fact]
    public async Task Import_rejects_invalid_enum_label_with_400()
    {
        var factory = _fixture.Factory;
        using var client = await CreateAuthenticatedClientAsync(factory);
        var projectId = await SetupProjectAsync(client);

        var document = new
        {
            version = "1.0",
            project = new { name = "Invalid" },
            elements = new[]
            {
                new
                {
                    id = Guid.NewGuid(),
                    kind = "system",
                    name = "Orders",
                    status = "9",
                    parentId = (Guid?)null,
                    tags = Array.Empty<string>(),
                },
            },
            relationships = Array.Empty<object>(),
            messages = Array.Empty<object>(),
            schemas = Array.Empty<object>(),
        };

        var response = await client.PostAsJsonAsync($"/api/projects/{projectId}/import", document);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Import_rejects_message_with_unknown_schema()
    {
        var factory = _fixture.Factory;
        using var client = await CreateAuthenticatedClientAsync(factory);
        var projectId = await SetupProjectAsync(client);

        var producerId = Guid.NewGuid();
        await client.PostAsJsonAsync($"/api/projects/{projectId}/elements",
            new { id = producerId, kind = "system", name = "Orders" });

        var document = new
        {
            version = "1.0",
            project = new { name = "Dangling" },
            elements = Array.Empty<object>(),
            relationships = Array.Empty<object>(),
            messages = new[]
            {
                new
                {
                    id = Guid.NewGuid(),
                    name = "OrderPlaced",
                    producerId,
                    consumerIds = Array.Empty<Guid>(),
                    schemaId = Guid.NewGuid(),
                    tags = Array.Empty<string>(),
                },
            },
            schemas = Array.Empty<object>(),
        };

        var response = await client.PostAsJsonAsync($"/api/projects/{projectId}/import", document);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Unauthenticated_export_returns_401()
    {
        var factory = _fixture.Factory;
        using var client = CreateClient(factory);
        var response = await client.GetAsync($"/api/projects/{Guid.NewGuid()}/export");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_document_with_schemas_imports_into_a_second_project_with_schema_IDs_of_that_project()
    {
        var factory = _fixture.Factory;
        using var client = await CreateAuthenticatedClientAsync(factory);
        var first = await SetupProjectAsync(client);
        var second = await SetupProjectAsync(client);

        // The child stands before its parent, as an export that orders by ID can write it.
        var (systemId, appId) = (Guid.Parse("ffffffff-0000-4000-8000-000000000001"), Guid.Parse("00000000-0000-4000-8000-000000000002"));
        var (schemaId, messageId, operationId) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var document = new
        {
            version = "1.0",
            project = new { name = "Shop" },
            elements = new object[]
            {
                new { id = appId, kind = "app", name = "Orders API", status = "current", parentId = (Guid?)systemId, tags = Array.Empty<string>() },
                new { id = systemId, kind = "system", name = "Shop", status = "current", parentId = (Guid?)null, tags = Array.Empty<string>() },
            },
            relationships = Array.Empty<object>(),
            messages = new[]
            {
                new { id = messageId, name = "OrderPlaced", producerId = appId, consumerIds = Array.Empty<Guid>(), schemaId, tags = Array.Empty<string>() },
            },
            schemas = new[]
            {
                new { id = schemaId, name = "OrderPlaced", description = (string?)null, content = "{\"type\":\"object\"}", version = 1 },
            },
            operations = new[]
            {
                new
                {
                    id = operationId, ownerId = appId, name = "POST /orders", transport = "http",
                    requestSchemaId = (Guid?)schemaId, responseSchemaId = (Guid?)null, timeoutMs = 2000,
                    retry = new { maxAttempts = 1, backoffMs = 0 },
                },
            },
        };

        async Task<ImportResult> ImportAsync(Guid projectId, string query = "")
        {
            var response = await client.PostAsJsonAsync($"/api/projects/{projectId}/import{query}", document);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return (await response.Content.ReadFromJsonAsync<ImportResult>())!;
        }
        async Task<IReadOnlyList<Guid>> SchemaIdsAsync(Guid projectId) =>
            [.. (await client.GetFromJsonAsync<Page<SchemaDto>>($"/api/projects/{projectId}/schemas"))!.Items.Select(s => s.Id)];

        // The project that owns the schema ID keeps it.
        Assert.True((await ImportAsync(first)).Applied);
        Assert.Equal([schemaId], await SchemaIdsAsync(first));

        // A preview writes nothing and names the ID the schema gets in the second project.
        var mapped = ImportModel.SchemaIdIn(second, schemaId);
        Assert.NotEqual(schemaId, mapped);
        var preview = await ImportAsync(second, "?dryRun=true");
        Assert.False(preview.Applied);
        Assert.Equal(0, preview.Refused);
        Assert.Equal(mapped, Assert.Single(preview.Items, i => i.Kind == "schema").ImportedAs);
        Assert.Empty(await SchemaIdsAsync(second));
        Assert.Empty((await client.GetFromJsonAsync<Page<ElementDto>>($"/api/projects/{second}/elements"))!.Items);

        // The import gives the schema that ID and writes every reference with it.
        var imported = await ImportAsync(second, $"?expectedVersion={preview.ModelVersion}");
        Assert.True(imported.Applied);
        Assert.Equal(5, imported.Added);
        Assert.Equal([mapped], await SchemaIdsAsync(second));
        var messages = await client.GetFromJsonAsync<Page<MessageDto>>($"/api/projects/{second}/messages");
        Assert.Equal(mapped, Assert.Single(messages!.Items).SchemaId);
        var operations = await client.GetFromJsonAsync<Page<Vellum.Modules.Modelling.Contracts.OperationDto>>($"/api/projects/{second}/operations");
        Assert.Equal(mapped, Assert.Single(operations!.Items).RequestSchemaId);
        Assert.Equal(2, (await client.GetFromJsonAsync<Page<ElementDto>>($"/api/projects/{second}/elements"))!.Items.Count);

        // An import that names the version of the preview is refused once the model has moved.
        var late = await client.PostAsJsonAsync($"/api/projects/{second}/import?expectedVersion={preview.ModelVersion}", document);
        Assert.Equal(HttpStatusCode.Conflict, late.StatusCode);

        // The same document again changes nothing and adds no second schema, in either project.
        var again = await ImportAsync(second);
        Assert.Equal((0, 0, 5), (again.Added, again.Changed, again.Unchanged));
        Assert.Equal([mapped], await SchemaIdsAsync(second));
        Assert.Equal((0, 5), ((await ImportAsync(first)).Added, (await ImportAsync(first)).Unchanged));
        Assert.Equal([schemaId], await SchemaIdsAsync(first));
    }

    [Fact]
    public async Task A_preview_does_not_tell_whether_an_ID_is_in_use_in_a_project_the_caller_cannot_read()
    {
        var factory = _fixture.Factory;

        // The owner's project: a schema, a deleted schema and the model stream.
        using var owner = await TestHost.SignInAsync(factory);
        var (_, theirs) = await TestHost.CreateProjectAsync(owner);
        var (live, deleted) = (Guid.NewGuid(), Guid.NewGuid());
        foreach (var id in new[] { live, deleted })
        {
            await TestHost.Created(owner.PostAsJsonAsync($"/api/projects/{theirs}/schemas",
                new { id, name = "Secret", content = "{\"type\":\"object\"}" }));
        }
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/api/projects/{theirs}/schemas/{deleted}")).StatusCode);
        var modelStream = (await owner.GetFromJsonAsync<JsonElement>($"/api/projects/{theirs}")).GetProperty("streamId").GetGuid();
        var exportBefore = await owner.GetStringAsync($"/api/projects/{theirs}/export");
        var liveBefore = await owner.GetStringAsync($"/api/projects/{theirs}/schemas/{live}");

        using var stranger = await TestHost.SignInAsync(factory);
        var (_, mine) = await TestHost.CreateProjectAsync(stranger);

        static object Document(params Guid[] schemaIds) => new
        {
            elements = Array.Empty<object>(),
            schemas = schemaIds.Select(id => new { id, name = "Probe", content = "{\"type\":\"object\"}" }).ToArray(),
        };
        async Task<ImportResult> Post(object document, string query)
        {
            var response = await stranger.PostAsJsonAsync($"/api/projects/{mine}/import{query}", document);
            Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
            return (await response.Content.ReadFromJsonAsync<ImportResult>())!;
        }
        // The answer with the probed IDs left out.
        static string Shape(ImportResult result) => JsonSerializer.Serialize(result with
        {
            Items = [.. result.Items.Select(i => i with { Id = Guid.Empty, ImportedAs = i.ImportedAs is null ? null : Guid.Empty })],
        });

        // Each ID in use, alone and next to the ID this project would give it, previews as an unused ID.
        var unused = Guid.NewGuid();
        var expected = Shape(await Post(Document(unused), "?dryRun=true"));
        var expectedPair = Shape(await Post(Document(unused, ImportModel.SchemaIdIn(mine, unused)), "?dryRun=true"));
        foreach (var probe in new[] { live, deleted, modelStream })
        {
            Assert.Equal(expected, Shape(await Post(Document(probe), "?dryRun=true")));
            Assert.Equal(expectedPair, Shape(await Post(Document(probe, ImportModel.SchemaIdIn(mine, probe)), "?dryRun=true")));
        }

        // The import writes into none of those streams: each schema takes the ID this project gives it.
        var imported = await Post(Document(live, deleted, modelStream), "");
        Assert.True(imported.Applied);
        Assert.All(imported.Items, i => Assert.Equal(ImportModel.SchemaIdIn(mine, i.Id), i.ImportedAs));

        Assert.Equal(exportBefore, await owner.GetStringAsync($"/api/projects/{theirs}/export"));
        Assert.Equal(liveBefore, await owner.GetStringAsync($"/api/projects/{theirs}/schemas/{live}"));
        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync($"/api/projects/{theirs}/schemas/{deleted}")).StatusCode);
    }

    [Fact]
    public async Task A_refused_import_names_every_refused_entry_and_writes_nothing()
    {
        var factory = _fixture.Factory;
        using var client = await CreateAuthenticatedClientAsync(factory);
        var projectId = await SetupProjectAsync(client);

        var (good, noParent, badStatus) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var document = new
        {
            elements = new object[]
            {
                new { id = good, kind = "system", name = "Shop", status = "current", tags = Array.Empty<string>() },
                new { id = noParent, kind = "app", name = "Orphan", status = "current", parentId = Guid.NewGuid(), tags = Array.Empty<string>() },
                new { id = badStatus, kind = "system", name = "Odd", status = "sideways", tags = Array.Empty<string>() },
            },
        };

        var preview = await (await client.PostAsJsonAsync($"/api/projects/{projectId}/import?dryRun=true", document))
            .Content.ReadFromJsonAsync<ImportResult>();
        Assert.Equal((1, 2), (preview!.Added, preview.Refused));
        Assert.All(preview.Items.Where(i => i.Change == ImportModel.Refused), i => Assert.False(string.IsNullOrEmpty(i.Error)));

        var response = await client.PostAsJsonAsync($"/api/projects/{projectId}/import", document);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ErrorResponse>();
        Assert.Equal("invalid_export", error!.Type);
        Assert.StartsWith($"Element {noParent}:", error.Title);
        Assert.Equal([$"element:{noParent}", $"element:{badStatus}"], error.Errors!.Select(e => e.Field));
        Assert.Empty((await client.GetFromJsonAsync<Page<ElementDto>>($"/api/projects/{projectId}/elements"))!.Items);
    }
}

