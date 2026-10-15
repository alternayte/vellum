using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Vellum.Modules.Comments;
using Vellum.Modules.Identity;
using Vellum.Tests.Shared.Outbound;

namespace Vellum.Tests.Modules.Comments;

/// <summary>
/// The workspace webhook through the real Refit client and handler pipeline. The worker of
/// the test host sends; the transport records what arrives. No test opens a socket.
/// </summary>
public class CommentWebhookTests(SharedOutboundHost shared) : IClassFixture<SharedOutboundHost>
{
    private readonly OutboundHost _host = shared.Host;

    private static async Task<IReadOnlyList<SentRequest>> ArrivedAsync(StubTransport stub, int count)
    {
        for (var i = 0; i < 400 && stub.Requests.Count < count; i++)
            await Task.Delay(50);
        Assert.True(stub.Requests.Count >= count, $"{stub.Requests.Count} of {count} webhook calls arrived");
        return stub.Requests;
    }

    [Fact]
    public async Task Comment_events_reach_the_webhook_signed_with_ids_names_and_a_link_and_no_comment_or_source_text()
    {
        var (owner, workspaceId) = await _host.NewWorkspaceAsync();
        var projectId = Guid.NewGuid();
        await TestHost.Created(owner.PostAsJsonAsync($"/api/workspaces/{workspaceId}/projects", new { id = projectId, name = "Hooked" }));
        var hostName = $"{Guid.NewGuid():N}.hooks.test";
        await OutboundHost.PutSettingsAsync(owner, workspaceId,
            new JsonObject { ["notifications"] = new JsonObject { ["webhookUrl"] = $"https://{hostName}/in?team=arch" } });
        const string secret = "whsec-test-0123456789";
        await OutboundHost.PutSecretAsync(owner, workspaceId, "notifications:webhookSecret", secret);
        var stub = _host.Transport.For(hostName).Then(() => new HttpResponseMessage(HttpStatusCode.NoContent));

        var member = await TestHost.SignInMemberAsync(_host.Factory, owner, workspaceId, "Commenter");
        var memberId = (await member.GetFromJsonAsync<UserInfoResponse>("/api/auth/me"))!.Id;
        var elementId = Guid.NewGuid();
        await TestHost.Created(owner.PostAsJsonAsync($"/api/projects/{projectId}/elements", new { id = elementId, kind = "system", name = "Payments" }));

        const string text = "Secret pricing note zq-hook-4412";
        var created = await TestHost.Created(owner.PostAsJsonAsync($"/api/projects/{projectId}/threads",
            new { target = new { kind = "element", id = elementId }, body = text, mentions = new[] { memberId } }));
        var thread = (await created.Content.ReadFromJsonAsync<ThreadDto>())!;
        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsync($"/api/projects/{projectId}/threads/{thread.Id}/resolve", null)).StatusCode);

        var sent = await ArrivedAsync(stub, 3);
        var events = sent.Select(r => JsonNode.Parse(r.Body)!.AsObject()).ToList();
        Assert.Equal(["comment.created", "comment.mentioned", "comment.resolved"], events.Select(e => e["event"]!.GetValue<string>()).Order());

        foreach (var (request, payload) in sent.Zip(events))
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/in", request.Uri.AbsolutePath);
            Assert.Equal("?team=arch", request.Uri.Query);
            Assert.Equal(payload["event"]!.GetValue<string>(), request.Header("X-Vellum-Event"));
            Assert.Equal(payload["deliveryId"]!.GetValue<string>(), request.Header("X-Vellum-Delivery"));
            Assert.Equal(WebhookSender.Sign(secret, request.Header("X-Vellum-Timestamp")!, Encoding.UTF8.GetBytes(request.Body)), request.Header("X-Vellum-Signature"));
            // No cookie or credential of the person who commented goes along.
            Assert.Null(request.Header("Cookie"));
            Assert.Null(request.Header("Authorization"));

            Assert.Equal(thread.Id, payload["threadId"]!.GetValue<Guid>());
            Assert.Equal(projectId, payload["projectId"]!.GetValue<Guid>());
            Assert.Equal("Payments", payload["target"]!["name"]!.GetValue<string>());
            Assert.Equal(elementId, payload["target"]!["id"]!.GetValue<Guid>());
            Assert.Equal($"https://vellum.example.com/projects/{projectId}?thread={thread.Id}", payload["link"]!.GetValue<string>());
            Assert.DoesNotContain("zq-hook-4412", request.Body);
            Assert.DoesNotContain("pricing", request.Body);
        }

        var mentioned = events.Single(e => e["event"]!.GetValue<string>() == "comment.mentioned");
        Assert.Equal(memberId, mentioned["mentioned"]![0]!["userId"]!.GetValue<string>());
    }

    [Fact]
    public async Task An_event_of_a_workspace_without_a_webhook_url_is_not_queued()
    {
        var (owner, workspaceId) = await _host.NewWorkspaceAsync();
        var projectId = Guid.NewGuid();
        await TestHost.Created(owner.PostAsJsonAsync($"/api/workspaces/{workspaceId}/projects", new { id = projectId, name = "Quiet" }));
        var hostName = $"{Guid.NewGuid():N}.hooks.test";
        var stub = _host.Transport.For(hostName).Then(() => new HttpResponseMessage(HttpStatusCode.OK));

        await TestHost.Created(owner.PostAsJsonAsync($"/api/projects/{projectId}/threads",
            new { target = new { kind = "project", id = projectId }, body = "Before the hook" }));
        await OutboundHost.PutSettingsAsync(owner, workspaceId,
            new JsonObject { ["notifications"] = new JsonObject { ["webhookUrl"] = $"https://{hostName}/in" } });
        var after = await TestHost.Created(owner.PostAsJsonAsync($"/api/projects/{projectId}/threads",
            new { target = new { kind = "project", id = projectId }, body = "After the hook" }));
        var thread = (await after.Content.ReadFromJsonAsync<ThreadDto>())!;

        var sent = Assert.Single(await ArrivedAsync(stub, 1));
        Assert.Equal(thread.Id, JsonNode.Parse(sent.Body)!["threadId"]!.GetValue<Guid>());
        // No signing secret is set, so the request carries no signature.
        Assert.Null(sent.Header("X-Vellum-Signature"));
        await Task.Delay(500);
        Assert.Single(stub.Requests);
    }
}
