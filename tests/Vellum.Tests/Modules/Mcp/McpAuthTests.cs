using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Vellum.Modules.Identity;

namespace Vellum.Tests.Modules.Mcp;

/// <summary>
/// The MCP endpoint over the real app: it takes agent tokens only, and every tool stays
/// inside the workspace, the project restriction and the scopes of the token.
/// </summary>
public class McpAuthTests(IntegrationFixture fixture) : IClassFixture<IntegrationFixture>
{
    private static JsonObject Project(Guid projectId) => new() { ["projectId"] = projectId };

    [Fact]
    public async Task A_cookie_session_does_not_open_mcp()
    {
        var factory = fixture.Factory;
        var owner = await TestHost.SignInAsync(factory);
        await TestHost.CreateProjectAsync(owner);

        using var response = await new McpWire(owner).PostAsync(McpWire.Initialize());
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        using var anonymous = await new McpWire(factory.CreateClient()).PostAsync(McpWire.Initialize());
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }

    [Fact]
    public async Task An_agent_token_initializes_lists_the_tools_and_calls_one()
    {
        var factory = fixture.Factory;
        var owner = await TestHost.SignInAsync(factory);
        var (workspaceId, projectId) = await TestHost.CreateProjectAsync(owner);
        await TestHost.Created(owner.PostAsJsonAsync($"/api/projects/{projectId}/elements", new { id = Guid.NewGuid(), kind = "system", name = "Shop" }));
        var agent = await TestHost.CreateAgentAsync(factory, owner, workspaceId, ["read"]);

        var mcp = new McpWire(agent.Client);
        var initialized = await mcp.InitializeAsync();
        Assert.NotNull(initialized["result"]!["serverInfo"]);

        var tools = (await mcp.RequestAsync("tools/list"))["result"]!["tools"]!.AsArray();
        Assert.Contains("get_model_summary", tools.Select(t => t!["name"]!.GetValue<string>()));

        var (isError, text) = await mcp.ToolAsync("get_model_summary", Project(projectId));
        Assert.False(isError, text);
        var summary = JsonNode.Parse(text)!;
        Assert.Equal(projectId, summary["projectId"]!.GetValue<Guid>());
        Assert.Equal(("Test Project", 1), (summary["data"]!["projectName"]!.GetValue<string>(), summary["data"]!["totalElements"]!.GetValue<int>()));

        var messages = await mcp.ToolAsync("list_messages", Project(projectId));
        Assert.False(messages.IsError, messages.Text);
        Assert.Equal(0, JsonNode.Parse(messages.Text)!["data"]!["count"]!.GetValue<int>());
    }

    [Fact]
    public async Task A_tool_gives_nothing_of_a_project_in_another_workspace()
    {
        var factory = fixture.Factory;
        var owner = await TestHost.SignInAsync(factory);
        var (_, projectId) = await TestHost.CreateProjectAsync(owner);
        var stranger = await TestHost.SignInAsync(factory);
        var (strangerWorkspace, _) = await TestHost.CreateProjectAsync(stranger);
        var agent = await TestHost.CreateAgentAsync(factory, stranger, strangerWorkspace);

        var mcp = new McpWire(agent.Client);
        await mcp.InitializeAsync();
        foreach (var (name, arguments) in new (string, JsonObject)[]
        {
            ("get_model_summary", Project(projectId)),
            ("find_elements", Project(projectId)),
            ("list_messages", Project(projectId)),
            ("get_element", new JsonObject { ["projectId"] = projectId, ["elementId"] = Guid.NewGuid() }),
            ("get_dependencies", new JsonObject { ["projectId"] = projectId, ["elementId"] = Guid.NewGuid() }),
            ("get_dependents", new JsonObject { ["projectId"] = projectId, ["elementId"] = Guid.NewGuid() }),
        })
        {
            var (isError, text) = await mcp.ToolAsync(name, arguments);
            Assert.True(isError, $"{name}: {text}");
            Assert.DoesNotContain("Test Project", text);
            Assert.DoesNotContain("totalElements", text);
            Assert.DoesNotContain("count", text);
        }

        // A project that does not exist gets the same answer: the tool is no way to find IDs.
        var other = await mcp.ToolAsync("get_model_summary", Project(projectId));
        var missing = await mcp.ToolAsync("get_model_summary", Project(Guid.NewGuid()));
        Assert.Equal(other, missing);
        Assert.Contains("Project not found", other.Text);
    }

    [Fact]
    public async Task A_tool_obeys_the_scope_and_the_project_restriction_of_the_token()
    {
        var factory = fixture.Factory;
        var owner = await TestHost.SignInAsync(factory);
        var (workspaceId, projectA) = await TestHost.CreateProjectAsync(owner);
        var projectB = Guid.NewGuid();
        await TestHost.Created(owner.PostAsJsonAsync($"/api/workspaces/{workspaceId}/projects", new { id = projectB, name = "Second Project" }));

        var withoutRead = await TestHost.CreateAgentAsync(factory, owner, workspaceId, ["propose", "export"]);
        var blind = new McpWire(withoutRead.Client);
        await blind.InitializeAsync();
        var (refused, text) = await blind.ToolAsync("get_model_summary", Project(projectA));
        Assert.True(refused, text);
        Assert.DoesNotContain("Test Project", text);

        var restricted = await TestHost.CreateAgentAsync(factory, owner, workspaceId, ["read"], projectId: projectA);
        var narrow = new McpWire(restricted.Client);
        await narrow.InitializeAsync();
        var own = await narrow.ToolAsync("get_model_summary", Project(projectA));
        Assert.False(own.IsError, own.Text);
        var sibling = await narrow.ToolAsync("get_model_summary", Project(projectB));
        Assert.True(sibling.IsError, sibling.Text);
        Assert.DoesNotContain("Second Project", sibling.Text);
    }

    [Fact]
    public async Task A_token_stops_on_mcp_when_it_is_revoked_or_its_user_leaves_the_workspace()
    {
        var factory = fixture.Factory;
        var owner = await TestHost.SignInAsync(factory);
        var (workspaceId, projectId) = await TestHost.CreateProjectAsync(owner);
        var second = await TestHost.SignInMemberAsync(factory, owner, workspaceId, "Owner");
        var secondId = (await second.GetFromJsonAsync<UserInfoResponse>("/api/auth/me"))!.Id;

        var agent = await TestHost.CreateAgentAsync(factory, second, workspaceId, ["read"]);
        var mcp = new McpWire(agent.Client);
        await mcp.InitializeAsync();
        Assert.False((await mcp.ToolAsync("get_model_summary", Project(projectId))).IsError);

        // The user the token acts for leaves. The session that was open does not carry on.
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/api/workspaces/{workspaceId}/members/{secondId}")).StatusCode);
        using var left = await mcp.PostAsync(new JsonObject
        {
            ["jsonrpc"] = "2.0", ["id"] = 99, ["method"] = "tools/call",
            ["params"] = new JsonObject { ["name"] = "get_model_summary", ["arguments"] = Project(projectId) },
        });
        Assert.Equal(HttpStatusCode.Forbidden, left.StatusCode);
        Assert.DoesNotContain("Test Project", await left.Content.ReadAsStringAsync());

        var revoked = await TestHost.CreateAgentAsync(factory, owner, workspaceId, ["read"]);
        var session = new McpWire(revoked.Client);
        await session.InitializeAsync();
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/api/workspaces/{workspaceId}/agent-tokens/{revoked.TokenId}")).StatusCode);
        using var after = await session.PostAsync(McpWire.Initialize(7));
        Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);
    }
}
