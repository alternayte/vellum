using System.Net;
using System.Net.Http.Json;
using System.Text;
using Vellum.Modules.Drafts;
using Vellum.Shared;

namespace Vellum.Tests.Shared;

public class TextRuleTests
{
    [Fact]
    public void A_line_is_bounded_and_holds_no_control_character()
    {
        Assert.Null(TextRule.Line("name", null));
        Assert.Null(TextRule.Line("name", new string('a', TextRule.MaxName)));
        Assert.Null(TextRule.Line("name", "Ünïcödé 名前 😀"));

        Assert.Equal("name", TextRule.Line("name", new string('a', TextRule.MaxName + 1))!.Field);
        foreach (var control in new[] { "\0", "\t", "\n", "\r", "\u001b", "\u007f", "\u0085" })
            Assert.NotNull(TextRule.Line("name", $"a{control}b"));
    }

    [Fact]
    public void Text_of_several_lines_may_hold_tab_and_line_breaks_only()
    {
        Assert.Null(TextRule.Lines("description", "one\n\ttwo\r\nthree"));
        Assert.Null(TextRule.Lines("description", new string('a', TextRule.MaxDescription)));

        Assert.NotNull(TextRule.Lines("description", new string('a', TextRule.MaxDescription + 1)));
        Assert.NotNull(TextRule.Lines("description", "a\0b"));
        Assert.NotNull(TextRule.Lines("description", "a\u0007b"));
    }

    [Fact]
    public void Half_of_a_surrogate_pair_is_not_text()
    {
        Assert.NotNull(TextRule.Line("name", "a\ud83db"));
        Assert.NotNull(TextRule.Line("name", "a\ude00"));
        Assert.NotNull(TextRule.Line("name", "a\ud83d"));
    }

    [Fact]
    public void Tags_are_bounded_in_number_and_in_length()
    {
        Assert.Null(TextRule.Tags("tags", Enumerable.Repeat("core", TextRule.MaxTags).ToArray()));
        Assert.NotNull(TextRule.Tags("tags", Enumerable.Repeat("core", TextRule.MaxTags + 1).ToArray()));
        Assert.NotNull(TextRule.Tags("tags", [new string('a', TextRule.MaxIdentifier + 1)]));
        Assert.NotNull(TextRule.Tags("tags", ["a\0b"]));
    }
}

/// <summary>
/// Postgres cannot store U+0000 and an index row has a size limit. Before the shared text
/// rule each of these requests ended in a 500 from the database.
/// </summary>
[Collection("Integration")]
public class BoundedTextEndpointTests(IntegrationFixture fixture)
{
    private const string Nul = "a\0b";
    private static readonly string TooLong = new('n', TextRule.MaxName + 1);

    private static string Url(Guid projectId, string path) => $"/api/projects/{projectId}/{path}";

    private static async Task AssertRefused(Task<HttpResponseMessage> request, string field)
    {
        var response = await request;
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.BadRequest, $"{field}: {response.StatusCode} {body}");
        var error = (await response.Content.ReadFromJsonAsync<ErrorResponse>())!;
        Assert.Contains(error.Errors ?? [], e => e.Field == field);
    }

    [Fact]
    public async Task Every_name_and_description_is_refused_with_its_field_when_it_holds_NUL_or_is_too_long()
    {
        using var factory = TestHost.Create(fixture);
        using var client = await TestHost.SignInAsync(factory);
        var (workspaceId, projectId) = await TestHost.CreateProjectAsync(client);

        Guid a = Guid.NewGuid(), b = Guid.NewGuid(), relationship = Guid.NewGuid(), message = Guid.NewGuid();
        Guid schema = Guid.NewGuid(), operation = Guid.NewGuid(), channel = Guid.NewGuid();
        await TestHost.Created(client.PostAsJsonAsync(Url(projectId, "elements"), new { id = a, kind = "system", name = "A" }));
        await TestHost.Created(client.PostAsJsonAsync(Url(projectId, "elements"), new { id = b, kind = "system", name = "B" }));
        await TestHost.Created(client.PostAsJsonAsync(Url(projectId, "relationships"), new { id = relationship, fromId = a, toId = b }));
        await TestHost.Created(client.PostAsJsonAsync(Url(projectId, "messages"), new { id = message, name = "M", producerId = a, consumerIds = Array.Empty<Guid>() }));
        await TestHost.Created(client.PostAsJsonAsync(Url(projectId, "schemas"), new { id = schema, name = "S", content = """{"type":"object"}""" }));
        await TestHost.Created(client.PostAsJsonAsync(Url(projectId, "operations"), new { id = operation, ownerId = a, name = "O", transport = "http" }));
        await TestHost.Created(client.PostAsJsonAsync(Url(projectId, "channels"), new { id = channel, name = "C", kind = "topic" }));
        var draft = (await (await TestHost.Created(client.PostAsJsonAsync(Url(projectId, "drafts"), new { id = Guid.NewGuid(), name = "D" })))
            .Content.ReadFromJsonAsync<DraftDto>())!;

        foreach (var bad in new[] { Nul, TooLong })
        {
            // Elements
            await AssertRefused(client.PostAsJsonAsync(Url(projectId, "elements"), new { id = Guid.NewGuid(), kind = "system", name = bad }), "name");
            await AssertRefused(client.PostAsJsonAsync(Url(projectId, "elements"), new { id = Guid.NewGuid(), kind = "system", name = "ok", technology = bad }), "technology");
            await AssertRefused(client.PostAsJsonAsync(Url(projectId, "elements"), new { id = Guid.NewGuid(), kind = "system", name = "ok", tags = new[] { bad } }), "tags");
            await AssertRefused(client.PatchAsJsonAsync(Url(projectId, $"elements/{a}"), new { name = bad }), "name");
            await AssertRefused(client.PatchAsJsonAsync(Url(projectId, $"elements/{a}"), new { icon = bad }), "icon");
            await AssertRefused(client.PatchAsJsonAsync(Url(projectId, $"elements/{a}"),
                new { factStatus = "observed-in-source", setProvenance = true, provenance = new { source = "repo@abc", revision = bad } }), "provenance.revision");

            // Relationships
            await AssertRefused(client.PostAsJsonAsync(Url(projectId, "relationships"), new { id = Guid.NewGuid(), fromId = a, toId = b, label = bad }), "label");
            await AssertRefused(client.PatchAsJsonAsync(Url(projectId, $"relationships/{relationship}"), new { technology = bad }), "technology");
            await AssertRefused(client.PatchAsJsonAsync(Url(projectId, $"relationships/{relationship}"), new { lineShape = bad }), "lineShape");

            // Messages
            await AssertRefused(client.PostAsJsonAsync(Url(projectId, "messages"), new { id = Guid.NewGuid(), name = bad, producerId = a, consumerIds = Array.Empty<Guid>() }), "name");
            await AssertRefused(client.PatchAsJsonAsync(Url(projectId, $"messages/{message}"), new { name = bad }), "name");

            // Schemas
            await AssertRefused(client.PostAsJsonAsync(Url(projectId, "schemas"), new { id = Guid.NewGuid(), name = bad, content = """{"type":"object"}""" }), "name");
            await AssertRefused(client.PatchAsJsonAsync(Url(projectId, $"schemas/{schema}"), new { name = bad }), "name");

            // Contracts
            await AssertRefused(client.PostAsJsonAsync(Url(projectId, "operations"), new { id = Guid.NewGuid(), ownerId = a, name = bad, transport = "http" }), "name");
            await AssertRefused(client.PatchAsJsonAsync(Url(projectId, $"operations/{operation}"), new { name = bad }), "name");
            await AssertRefused(client.PostAsJsonAsync(Url(projectId, "channels"), new { id = Guid.NewGuid(), name = bad, kind = "topic" }), "name");
            await AssertRefused(client.PatchAsJsonAsync(Url(projectId, $"channels/{channel}"), new { name = bad }), "name");

            // Scenarios
            await AssertRefused(client.PostAsJsonAsync(Url(projectId, "scenarios"),
                new { id = Guid.NewGuid(), name = bad, steps = Array.Empty<object>(), faults = Array.Empty<object>(), assertions = Array.Empty<object>() }), "name");

            // Drafts, variants, revisions
            await AssertRefused(client.PostAsJsonAsync(Url(projectId, "drafts"), new { id = Guid.NewGuid(), name = bad }), "name");
            await AssertRefused(client.PostAsJsonAsync(Url(projectId, "variants"), new { id = Guid.NewGuid(), name = bad }), "name");
            await AssertRefused(client.PostAsJsonAsync(Url(projectId, "revisions"), new { id = Guid.NewGuid(), name = bad }), "name");

            // Workspaces and projects
            await AssertRefused(client.PostAsJsonAsync("/api/workspaces", new { id = Guid.NewGuid(), name = bad }), "name");
            await AssertRefused(client.PatchAsJsonAsync($"/api/workspaces/{workspaceId}", new { name = bad }), "name");
            await AssertRefused(client.PostAsJsonAsync($"/api/workspaces/{workspaceId}/projects", new { id = Guid.NewGuid(), name = bad }), "name");
            await AssertRefused(client.PatchAsJsonAsync($"/api/projects/{projectId}", new { name = bad }), "name");
        }

        // Text of several lines: NUL is refused, the bound is the larger one, a line break is kept.
        var tooLongText = new string('d', TextRule.MaxDescription + 1);
        foreach (var bad in new[] { Nul, tooLongText })
        {
            await AssertRefused(client.PostAsJsonAsync(Url(projectId, "elements"), new { id = Guid.NewGuid(), kind = "system", name = "ok", description = bad }), "description");
            await AssertRefused(client.PatchAsJsonAsync(Url(projectId, $"messages/{message}"), new { description = bad, setDescription = true }), "description");
            await AssertRefused(client.PatchAsJsonAsync(Url(projectId, $"schemas/{schema}"), new { description = bad }), "description");
            await AssertRefused(client.PostAsJsonAsync(Url(projectId, "revisions"), new { id = Guid.NewGuid(), name = $"r {bad.Length}", note = bad }), "note");
            await AssertRefused(client.PatchAsJsonAsync($"/api/projects/{projectId}", new { description = bad }), "description");
            await AssertRefused(client.PostAsJsonAsync(Url(projectId, $"drafts/{draft.Id}/comments"), new { id = Guid.NewGuid(), body = bad }), "body");
        }

        var described = await client.PatchAsJsonAsync(Url(projectId, $"elements/{a}"), new { description = "line one\n\tline two" });
        Assert.Equal(HttpStatusCode.OK, described.StatusCode);
        var comment = Guid.NewGuid();
        await TestHost.Created(client.PostAsJsonAsync(Url(projectId, $"drafts/{draft.Id}/comments"), new { id = comment, body = "first\nsecond" }));
        await AssertRefused(client.PatchAsJsonAsync(Url(projectId, $"drafts/{draft.Id}/comments/{comment}"), new { body = Nul }), "body");
    }

    [Theory]
    [InlineData(""" "name": "a\u0000b" """)]
    [InlineData(""" "name": "ok", "icon": "a\u0000b" """)]
    [InlineData(""" "name": "ok", "factStatus": "observed-in-source", "provenance": { "source": "a\u0000b" } """)]
    public async Task An_import_with_text_the_database_cannot_store_is_a_400(string members)
    {
        using var factory = TestHost.Create(fixture);
        using var client = await TestHost.SignInAsync(factory);
        var (_, projectId) = await TestHost.CreateProjectAsync(client);

        var document = $$"""
            {
              "version": "1.0", "project": { "name": "P" },
              "elements": [ { "id": "{{Guid.NewGuid()}}", "kind": "system", {{members}}, "status": "current", "tags": [] } ],
              "relationships": [], "messages": [], "schemas": []
            }
            """;
        var response = await client.PostAsync(Url(projectId, "import"), new StringContent(document, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_export", (await response.Content.ReadFromJsonAsync<ErrorResponse>())!.Type);
    }

    [Fact]
    public async Task Text_that_only_reaches_a_query_is_a_400_too()
    {
        using var factory = TestHost.Create(fixture);
        using var client = await TestHost.SignInAsync(factory);
        var (workspaceId, projectId) = await TestHost.CreateProjectAsync(client);

        // A filter is compared in the database and never stored.
        var filtered = await client.GetAsync(Url(projectId, "elements?kind=%00"));
        Assert.Equal(HttpStatusCode.BadRequest, filtered.StatusCode);
        Assert.Equal("invalid_text", (await filtered.Content.ReadFromJsonAsync<ErrorResponse>())!.Type);

        await AssertRefused(client.PostAsJsonAsync($"/api/workspaces/{workspaceId}/invitations", new { email = "a\0b@vellum.local", role = "Viewer" }), "email");
    }

    [Fact]
    public async Task A_body_larger_than_the_limit_of_its_endpoint_is_a_413_with_the_error_envelope()
    {
        using var factory = TestHost.Create(fixture);
        using var client = await TestHost.SignInAsync(factory);
        var (_, projectId) = await TestHost.CreateProjectAsync(client);

        // A 2 MB channel name, with a declared length and without one.
        var huge = $$"""{ "id": "{{Guid.NewGuid()}}", "kind": "topic", "name": "{{new string('n', 2 * 1024 * 1024)}}" }""";
        var declared = await client.PostAsync(Url(projectId, "channels"), new StringContent(huge, Encoding.UTF8, "application/json"));
        var streamed = await client.PostAsync(Url(projectId, "channels"), Streamed(huge));

        foreach (var response in new[] { declared, streamed })
        {
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
            Assert.Equal("payload_too_large", (await response.Content.ReadFromJsonAsync<ErrorResponse>())!.Type);
        }

        // A command just under the limit is read and judged on its content.
        var large = $$"""{ "id": "{{Guid.NewGuid()}}", "kind": "topic", "name": "{{new string('n', 60 * 1024)}}" }""";
        var judged = await client.PostAsync(Url(projectId, "channels"), new StringContent(large, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, judged.StatusCode);
    }

    /// <summary>A body sent in chunks: the request has no Content-Length.</summary>
    private static StreamContent Streamed(string json)
    {
        var content = new StreamContent(new UnknownLengthStream(Encoding.UTF8.GetBytes(json)));
        content.Headers.ContentType = new("application/json");
        return content;
    }

    private sealed class UnknownLengthStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }
}
