using System.Net;
using System.Net.Http.Json;
using System.Text;
using Npgsql;
using Vellum.Modules.Modelling.Elements;
using Vellum.Modules.Schemas;
using Vellum.Shared;

namespace Vellum.Tests.Modules.Modelling;

/// <summary>
/// An import writes the model stream and one stream per schema in one transaction. It must
/// not lose a lock cycle against a write to one of those streams, it must be all or nothing,
/// and its size has a limit.
/// </summary>
public class ImportConcurrencyTests(IntegrationFixture fixture) : IClassFixture<IntegrationFixture>
{
    private const int Schemas = 120;

    private static string Url(Guid projectId, string path) => $"/api/projects/{projectId}/{path}";

    /// <summary>One element and every schema, all stamped with <paramref name="marker"/>.</summary>
    private static object Document(Guid element, IReadOnlyList<Guid> schemas, string marker) => new
    {
        version = "1.0",
        project = new { name = "Import" },
        elements = new[] { new { id = element, kind = "system", name = marker, status = "current", tags = Array.Empty<string>() } },
        relationships = Array.Empty<object>(),
        messages = Array.Empty<object>(),
        schemas = schemas.Select((id, i) => new
        {
            id, name = $"Schema {i:D3}", description = marker,
            content = $$"""{"type":"object","title":"{{marker}}"}""", version = 1,
        }),
    };

    // 12 imports of 120 schemas against 720 patches: the load is what gives a lock cycle its chance.
    [Fact]
    [Trait("Category", "Slow")]
    public async Task Concurrent_imports_and_schema_patches_do_not_deadlock_or_leave_partial_state()
    {
        var factory = fixture.Factory;
        using var client = await TestHost.SignInAsync(factory);
        var (_, projectId) = await TestHost.CreateProjectAsync(client);

        var element = Guid.NewGuid();
        var schemas = Enumerable.Range(0, Schemas).Select(_ => Guid.NewGuid()).ToList();

        // The first import creates everything. One patch stores the schema update event type,
        // so no later write is the first to record it.
        var seeded = await client.PostAsJsonAsync(Url(projectId, "import"), Document(element, schemas, "seed"));
        Assert.True(seeded.StatusCode == HttpStatusCode.OK, await seeded.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, (await client.PatchAsJsonAsync(Url(projectId, $"schemas/{schemas[0]}"), new { name = "Schema 000" })).StatusCode);

        var deadlocksBefore = await DeadlocksAsync();

        const int importers = 4, importsEach = 3, patchers = 6;
        var imports = Enumerable.Range(0, importers).Select(i => Task.Run(async () =>
        {
            var statuses = new List<HttpStatusCode>();
            for (var n = 0; n < importsEach; n++)
                statuses.Add((await client.PostAsJsonAsync(Url(projectId, "import"), Document(element, schemas, $"import-{i}-{n}"))).StatusCode);
            return statuses;
        })).ToList();

        // Half of the patchers walk the schemas in the import's order, half against it.
        var patches = Enumerable.Range(0, patchers).Select(p => Task.Run(async () =>
        {
            var statuses = new List<HttpStatusCode>();
            var order = p % 2 == 0 ? schemas : Enumerable.Reverse(schemas).ToList();
            foreach (var schema in order)
                statuses.Add((await client.PatchAsJsonAsync(Url(projectId, $"schemas/{schema}"), new { name = $"Patched by {p}" })).StatusCode);
            return statuses;
        })).ToList();

        var importStatuses = (await Task.WhenAll(imports)).SelectMany(s => s).ToList();
        var patchStatuses = (await Task.WhenAll(patches)).SelectMany(s => s).ToList();

        Assert.All(importStatuses, status => Assert.Equal(HttpStatusCode.OK, status));
        Assert.All(patchStatuses, status => Assert.Equal(HttpStatusCode.OK, status));
        Assert.Equal((importers * importsEach, patchers * Schemas), (importStatuses.Count, patchStatuses.Count));

        // Every schema and the element carry the stamp of the same import: the last one.
        var page = (await client.GetFromJsonAsync<Page<SchemaDto>>(Url(projectId, "schemas?limit=200")))!;
        Assert.Equal(Schemas, page.Items.Count);
        var stamp = Assert.Single(page.Items.Select(s => s.Description).Distinct());
        Assert.StartsWith("import-", stamp);
        Assert.All(page.Items, s => Assert.Contains($"\"title\":\"{stamp}\"", s.Content));
        Assert.Equal(stamp, (await client.GetFromJsonAsync<ElementDto>(Url(projectId, $"elements/{element}")))!.Name);

        // The database aborted no transaction. A retry would hide a deadlock from the statuses above.
        Assert.Equal(deadlocksBefore, await DeadlocksAsync());
    }

    [Fact]
    public async Task A_refused_import_leaves_no_model_and_no_schemas()
    {
        var factory = fixture.Factory;
        using var client = await TestHost.SignInAsync(factory);
        var (_, projectId) = await TestHost.CreateProjectAsync(client);

        var element = Guid.NewGuid();
        var schemas = Enumerable.Range(0, 3).Select(_ => Guid.NewGuid()).ToList();
        var document = new
        {
            version = "1.0",
            project = new { name = "Import" },
            elements = new[] { new { id = element, kind = "system", name = "Orders", status = "current", tags = Array.Empty<string>() } },
            relationships = Array.Empty<object>(),
            messages = Array.Empty<object>(),
            // The last schema is not a JSON Schema.
            schemas = schemas.Select((id, i) => new { id, name = $"Schema {i}", content = i == 2 ? "[1, 2, 3]" : """{"type":"object"}""", version = 1 }),
        };

        var response = await client.PostAsJsonAsync(Url(projectId, "import"), document);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty((await client.GetFromJsonAsync<Page<ElementDto>>(Url(projectId, "elements")))!.Items);
        Assert.Empty((await client.GetFromJsonAsync<Page<SchemaDto>>(Url(projectId, "schemas")))!.Items);
    }

    [Fact]
    public async Task An_import_over_a_size_limit_is_refused()
    {
        var factory = fixture.Factory;
        using var client = await TestHost.SignInAsync(factory);
        var (_, projectId) = await TestHost.CreateProjectAsync(client);

        var tooManySchemas = new
        {
            version = "1.0",
            project = new { name = "Import" },
            schemas = Enumerable.Range(0, 201).Select(i => new { id = Guid.NewGuid(), name = $"Schema {i}", content = """{"type":"object"}""", version = 1 }),
        };
        var counted = await client.PostAsJsonAsync(Url(projectId, "import"), tooManySchemas);
        Assert.Equal(HttpStatusCode.BadRequest, counted.StatusCode);
        var error = (await counted.Content.ReadFromJsonAsync<ErrorResponse>())!;
        Assert.Equal("import_too_large", error.Type);
        Assert.Contains("200 schemas", error.Title);

        // Valid JSON, one byte over the limit of 16 MiB.
        var body = """{"version":"1.0","project":{"name":"Import"}}""";
        var oversized = new StringContent(body + new string(' ', 16 * 1024 * 1024 + 1 - body.Length), Encoding.UTF8, "application/json");
        var weighed = await client.PostAsync(Url(projectId, "import"), oversized);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, weighed.StatusCode);
        Assert.Equal("import_too_large", (await weighed.Content.ReadFromJsonAsync<ErrorResponse>())!.Type);

        Assert.Empty((await client.GetFromJsonAsync<Page<SchemaDto>>(Url(projectId, "schemas")))!.Items);
    }

    /// <summary>
    /// The deadlocks Postgres detected in this database. A backend reports its count when it
    /// goes idle, at most one second after the abort, so the read waits for that first.
    /// </summary>
    private async Task<long> DeadlocksAsync()
    {
        await Task.Delay(TimeSpan.FromSeconds(2.5));
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT deadlocks FROM pg_stat_database WHERE datname = current_database()", connection);
        return (long)(await command.ExecuteScalarAsync())!;
    }
}
