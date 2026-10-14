using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using Vellum.Modules.Mcp;

namespace Vellum.Tests.Modules.Mcp;

/// <summary>
/// The agent reference and the capability endpoint: served to anyone, holding nothing of an
/// instance, and in step with the tools and the endpoints that the server registered.
/// </summary>
public class AgentReferenceTests(IntegrationFixture fixture) : IClassFixture<IntegrationFixture>
{
    private const string Start = "<!-- generated:mcp-tools:start -->";
    private const string End = "<!-- generated:mcp-tools:end -->";

    private static string RepositoryFile(params string[] path)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Vellum.slnx")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return Path.Combine([directory.FullName, .. path]);
    }

    [Fact]
    public async Task Llms_txt_is_open_small_and_in_the_form_of_the_convention()
    {
        var factory = fixture.Factory;
        var anonymous = factory.CreateClient();

        using var response = await anonymous.GetAsync("/llms.txt");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(("text/plain", "utf-8"), (response.Content.Headers.ContentType!.MediaType, response.Content.Headers.ContentType.CharSet));
        Assert.True(response.Headers.CacheControl is { Public: true, MaxAge.TotalMinutes: >= 1 });
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(text.Length < 4_000, $"{text.Length} characters");

        // llmstxt.org: one H1, then a blockquote, then sections of links.
        var lines = text.Split('\n');
        Assert.Equal("# Vellum", lines[0]);
        Assert.Equal("", lines[1]);
        Assert.StartsWith("> ", lines[2]);
        Assert.Single(lines, line => line.StartsWith("# ", StringComparison.Ordinal));
        var section = Array.IndexOf(lines, "## Agent interface");
        Assert.True(section > 2);
        var links = lines.Skip(section + 1).Where(line => line.Length > 0).ToList();
        Assert.All(links, link => Assert.Matches(@"^- \[[^\]]+\]\(/[^)\s]+\)(: .+)?$", link));
        Assert.Equal(
            ["/llms-full.txt", "/skills/vellum/SKILL.md", "/api/agent/capabilities"],
            links.Select(link => link[(link.IndexOf("](", StringComparison.Ordinal) + 2)..link.IndexOf(')')]));
        Assert.Contains("Authorization: Bearer", text);
        Assert.Contains("`/mcp`", text);

        // Nothing of the instance: no host, no address, no name of the workspace of the fixture.
        Assert.DoesNotContain("localhost", text);
        Assert.DoesNotContain("://", text);
        Assert.DoesNotContain("Home", text);

        // Each thing it links is there, for a caller with no credential.
        foreach (var path in new[] { "/llms-full.txt", "/skills/vellum/SKILL.md" })
            Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/agent/capabilities")).StatusCode);
    }

    [Fact]
    public async Task Llms_full_txt_states_the_tools_and_the_endpoints_that_are_registered()
    {
        var factory = fixture.Factory;
        var owner = await TestHost.SignInAsync(factory);
        var (workspaceId, _) = await TestHost.CreateProjectAsync(owner);
        var agent = await TestHost.CreateAgentAsync(factory, owner, workspaceId);

        using var response = await factory.CreateClient().GetAsync("/llms-full.txt");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/plain", response.Content.Headers.ContentType!.MediaType);
        Assert.True(response.Headers.CacheControl is { Public: true });
        var text = await response.Content.ReadAsStringAsync();
        Assert.StartsWith("# Vellum agent reference\n\n> ", text);

        // The tools as a client lists them over MCP, each with every argument, its description and its scope.
        var mcp = new McpWire(agent.Client);
        await mcp.InitializeAsync();
        var tools = (await mcp.RequestAsync("tools/list"))["result"]!["tools"]!.AsArray();
        Assert.Equal(AgentTools.All.Count, tools.Count);
        foreach (var tool in tools)
        {
            var name = tool!["name"]!.GetValue<string>();
            var from = text.IndexOf($"#### `{name}`\n", StringComparison.Ordinal);
            Assert.True(from >= 0, name);
            var next = text.IndexOf("\n#### `", from + 1, StringComparison.Ordinal);
            var section = text[from..(next < 0 ? text.IndexOf("\n## HTTP endpoints", from, StringComparison.Ordinal) : next)];

            Assert.Contains(tool["description"]!.GetValue<string>(), section);
            var entry = AgentTools.Find(name);
            Assert.Contains($"- Scope: `{Vellum.Modules.Identity.AgentTokens.AgentScopes.Name(entry.Scope)}`", section);
            Assert.Contains($"- Result `data`: {entry.Returns}", section);
            Assert.Contains($"- Limits: {entry.Limits}", section);
            var required = tool["inputSchema"]!["required"]?.AsArray().Select(r => r!.GetValue<string>()).ToHashSet() ?? [];
            foreach (var (argument, schema) in tool["inputSchema"]!["properties"]?.AsObject() ?? [])
            {
                var row = section.Split('\n').Single(line => line.StartsWith($"| `{argument}` |", StringComparison.Ordinal));
                Assert.Contains(required.Contains(argument) ? "| yes |" : "| no |", row);
                Assert.Contains(schema!["description"]!.GetValue<string>().Replace("|", "\\|"), row);
            }
        }

        // No section for a tool that is not registered.
        Assert.Equal(tools.Count, text.Split("\n#### `").Length - 1);

        // The endpoints: exactly the ones the endpoint data source opens to agents, as the capability endpoint lists them.
        var capabilities = (await owner.GetFromJsonAsync<JsonObject>("/api/agent/capabilities"))!;
        var rows = text[text.IndexOf("## HTTP endpoints open to agent tokens", StringComparison.Ordinal)..].Split('\n')
            .Where(line => line.StartsWith("| ", StringComparison.Ordinal) && line.Contains('`'))
            .Select(line => line.Split('|', StringSplitOptions.TrimEntries))
            .Select(cells => $"{cells[1]} {cells[2].Trim('`')}")
            .ToList();
        Assert.Equal(capabilities["endpoints"]!.AsArray().Select(e => $"{e!["method"]!.GetValue<string>()} {e["path"]!.GetValue<string>()}"), rows);
        Assert.Contains("POST /mcp", rows);
        Assert.DoesNotContain(rows, row => row.Contains("/approve") || row.Contains("/settings") || row.Contains("/secrets") || row.Contains("/members") || row.Contains("agent-tokens"));

        // The error types and the bounds it states are the ones the server uses.
        Assert.All(McpOutput.ErrorTypes, error => Assert.Contains($"- `{error.Type}`: ", text));
        Assert.Contains($"at most {McpOutput.MaxBytes} bytes", text);
    }

    /// <summary>
    /// The reference page of the docs holds the same text as <c>/llms-full.txt</c> for the
    /// tools. To write the page again after a change of a tool, run this test with
    /// <c>VELLUM_UPDATE_DOCS=1</c>.
    /// </summary>
    [Fact]
    public void The_reference_page_of_the_docs_holds_the_registered_tools()
    {
        var factory = fixture.Factory;
        var generated = AgentReference.ToolsMarkdown(factory.Services.GetServices<McpServerTool>());
        Assert.Contains(generated, factory.Services.GetRequiredService<AgentReference>().LlmsFull);

        var path = RepositoryFile("site", "src", "content", "docs", "reference", "mcp-tools.md");
        var page = File.ReadAllText(path);
        var (start, end) = (page.IndexOf(Start, StringComparison.Ordinal), page.IndexOf(End, StringComparison.Ordinal));
        Assert.True(start >= 0 && end > start, "The page has no generated section.");

        var expected = page[..(start + Start.Length)] + "\n\n" + generated + "\n" + page[end..];
        if (Environment.GetEnvironmentVariable("VELLUM_UPDATE_DOCS") == "1")
            File.WriteAllText(path, expected);
        else
            Assert.True(expected == page, "site/src/content/docs/reference/mcp-tools.md is not in step with the registered tools. Run this test with VELLUM_UPDATE_DOCS=1.");
    }

    [Fact]
    public async Task The_skill_is_served_from_its_one_file()
    {
        var factory = fixture.Factory;

        using var response = await factory.CreateClient().GetAsync("/skills/vellum/SKILL.md");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/markdown", response.Content.Headers.ContentType!.MediaType);
        var served = await response.Content.ReadAsStringAsync();
        Assert.Equal(File.ReadAllText(RepositoryFile("skills", "vellum", "SKILL.md")), served);

        // Frontmatter with a name and a description, and a body an agent reads whole.
        var lines = served.Split('\n');
        Assert.Equal("---", lines[0]);
        var close = Array.IndexOf(lines, "---", 1);
        Assert.Contains("name: vellum", lines[1..close]);
        Assert.Contains(lines[1..close], line => line.StartsWith("description: ", StringComparison.Ordinal) && line.Length > 80);
        Assert.True(lines.Length - close <= 170, $"{lines.Length - close} lines");

        // Every tool it names is a tool, and it names every tool.
        var named = System.Text.RegularExpressions.Regex.Matches(served, "`([a-z]+(?:_[a-z]+)+)`").Select(m => m.Groups[1].Value)
            .Where(word => word is not ("agent_not_allowed" or "not_found" or "validation_error" or "no_approved_revision" or "not_runnable" or "not_a_test_plan" or "rate_limited" or "result_too_large" or "human_only"))
            .ToHashSet();
        Assert.Equal(AgentTools.All.Select(tool => tool.Name).Order(), named.Order());
    }

    [Fact]
    public async Task The_capability_endpoint_says_what_the_caller_may_call()
    {
        var factory = fixture.Factory;
        var owner = await TestHost.SignInAsync(factory);
        var (workspaceId, projectId) = await TestHost.CreateProjectAsync(owner);
        var expiresAt = DateTimeOffset.UtcNow.AddDays(3);

        // A token that reads one project.
        var narrow = await TestHost.CreateAgentAsync(factory, owner, workspaceId, ["read"], projectId, expiresAt);
        var mine = (await narrow.Client.GetFromJsonAsync<JsonObject>("/api/agent/capabilities"))!;
        Assert.Equal(("agent-token", narrow.TokenId, workspaceId, projectId),
            (mine["caller"]!.GetValue<string>(), mine["tokenId"]!.GetValue<Guid>(), mine["workspaceId"]!.GetValue<Guid>(), mine["projectId"]!.GetValue<Guid>()));
        Assert.Equal(["read"], mine["scopes"]!.AsArray().Select(s => s!.GetValue<string>()));
        Assert.InRange(mine["expiresAt"]!.GetValue<DateTimeOffset>(), expiresAt.AddSeconds(-1), expiresAt.AddSeconds(1));
        Assert.Equal("/mcp", mine["mcpEndpoint"]!.GetValue<string>());
        Assert.Equal(
            AgentTools.All.Where(tool => tool.Scope == Vellum.Modules.Identity.AgentTokens.AgentScope.Read).Select(tool => tool.Name),
            mine["tools"]!.AsArray().Select(tool => tool!["name"]!.GetValue<string>()));
        var endpoints = mine["endpoints"]!.AsArray().Select(e => (Method: e!["method"]!.GetValue<string>(), Path: e["path"]!.GetValue<string>())).ToList();
        Assert.Contains(("GET", "/api/projects/{projectId}/promises"), endpoints);
        Assert.Contains(("GET", "/api/projects/{projectId}/build/manifest"), endpoints);
        Assert.DoesNotContain(endpoints, e => e.Path.Contains("/build/export") || e.Path.Contains("{workspaceId}") || (e.Method == "POST" && e.Path.EndsWith("/promises")));

        // What it lists is what it gets: each listed GET of a project answers, and an endpoint it does not list is refused.
        // A comparison needs query values, so without them it answers 400: the request passed the agent boundary.
        foreach (var (_, path) in endpoints.Where(e => e.Method == "GET" && e.Path.Contains("{projectId}") && e.Path.Count(c => c == '{') == 1))
        {
            var status = (await narrow.Client.GetAsync(path.Replace("{projectId}", projectId.ToString()))).StatusCode;
            Assert.Equal(path.Contains("/compare/") ? HttpStatusCode.BadRequest : HttpStatusCode.OK, status);
        }
        Assert.Equal(HttpStatusCode.Forbidden, (await narrow.Client.GetAsync($"/api/workspaces/{workspaceId}/projects")).StatusCode);

        // Every scope, no restriction.
        var wide = await TestHost.CreateAgentAsync(factory, owner, workspaceId, ["read", "propose", "export", "scan-pull-request"]);
        var all = (await wide.Client.GetFromJsonAsync<JsonObject>("/api/agent/capabilities"))!;
        Assert.Null(all["projectId"]);
        Assert.Equal(["read", "propose", "export", "scan-pull-request"], all["scopes"]!.AsArray().Select(s => s!.GetValue<string>()));
        Assert.Equal(AgentTools.All.Count, all["tools"]!.AsArray().Count);
        Assert.Contains(all["endpoints"]!.AsArray(), e => e!["path"]!.GetValue<string>() == "/api/workspaces/{workspaceId}/projects");

        // A person in a session sees the whole lists and is not an agent.
        var session = (await owner.GetFromJsonAsync<JsonObject>("/api/agent/capabilities"))!;
        Assert.Equal("session", session["caller"]!.GetValue<string>());
        Assert.Empty(session["scopes"]!.AsArray());
        Assert.Null(session["tokenId"]);
        Assert.Equal(all["endpoints"]!.ToJsonString(), session["endpoints"]!.ToJsonString());

        // Nothing here is a secret: no token text.
        Assert.DoesNotContain(wide.Token, all.ToJsonString());
        Assert.DoesNotContain("vlm_agt_", all.ToJsonString());
    }
}
