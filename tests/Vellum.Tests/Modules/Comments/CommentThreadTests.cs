using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Deedbox;
using Microsoft.Extensions.DependencyInjection;
using Vellum.Modules.Comments;
using Vellum.Modules.Drafts;
using Vellum.Modules.Identity;
using Vellum.Modules.Modelling;
using Vellum.Modules.Settings;
using Vellum.Shared;

namespace Vellum.Tests.Modules.Comments;

/// <summary>
/// Comment threads: who may write them, what they attach to, what happens when the target is
/// deleted or the draft merges, moderation, the inbox, tenant scoping, and that no export and
/// no model stream holds them.
/// </summary>
public class CommentThreadTests(IntegrationFixture fixture) : IClassFixture<IntegrationFixture>
{
    private sealed record Project(HttpClient Owner, Guid WorkspaceId, Guid ProjectId)
    {
        public string Threads(string path = "") => $"/api/projects/{ProjectId}/threads{path}";
    }

    private async Task<Project> NewProjectAsync()
    {
        var owner = await TestHost.SignInAsync(fixture.Factory);
        var (workspaceId, projectId) = await TestHost.CreateProjectAsync(owner);
        return new Project(owner, workspaceId, projectId);
    }

    private static async Task<Guid> AddElementAsync(Project p, string name, Guid? branchId = null)
    {
        var id = Guid.NewGuid();
        await TestHost.Created(p.Owner.PostAsJsonAsync(
            $"/api/projects/{p.ProjectId}/elements{(branchId is { } b ? $"?branchId={b}" : "")}", new { id, kind = "system", name }));
        return id;
    }

    private static Task<HttpResponseMessage> OpenThreadAsync(HttpClient client, Project p, Guid elementId, string body, Guid? draftId = null, string[]? mentions = null) =>
        client.PostAsJsonAsync(p.Threads(), new { target = new { kind = "element", id = elementId }, body, draftId, mentions });

    private static async Task<ThreadDto> ThreadAsync(Task<HttpResponseMessage> request)
    {
        var response = await request;
        Assert.True(response.StatusCode is HttpStatusCode.Created or HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<ThreadDto>())!;
    }

    private static async Task<List<ThreadDto>> ListAsync(HttpClient client, Project p, Guid? draftId = null) =>
        [.. (await client.GetFromJsonAsync<Page<ThreadDto>>(p.Threads(draftId is { } d ? $"?draftId={d}" : "")))!.Items];

    private static async Task<string> UserIdAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<UserInfoResponse>("/api/auth/me"))!.Id;

    [Fact]
    public async Task A_commenter_comments_and_cannot_change_the_model_and_a_viewer_cannot_comment()
    {
        var p = await NewProjectAsync();
        var elementId = await AddElementAsync(p, "Orders API");
        var commenter = await TestHost.SignInMemberAsync(fixture.Factory, p.Owner, p.WorkspaceId, "Commenter");
        var viewer = await TestHost.SignInMemberAsync(fixture.Factory, p.Owner, p.WorkspaceId, "Viewer");

        var thread = await ThreadAsync(OpenThreadAsync(commenter, p, elementId, "Why is this sync?"));
        Assert.Equal("open", thread.Status);
        Assert.Equal("Orders API", thread.Target.Name);
        Assert.Equal(HttpStatusCode.Created, (await commenter.PostAsJsonAsync(p.Threads($"/{thread.Id}/replies"), new { body = "Follow-up" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await commenter.PostAsync(p.Threads($"/{thread.Id}/resolve"), null)).StatusCode);

        // The commenter reads the model and cannot change it, on main or in a draft.
        Assert.Equal(HttpStatusCode.OK, (await commenter.GetAsync($"/api/projects/{p.ProjectId}/elements")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await commenter.PostAsJsonAsync($"/api/projects/{p.ProjectId}/elements",
            new { id = Guid.NewGuid(), kind = "system", name = "Sneaky" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await commenter.PatchAsJsonAsync($"/api/projects/{p.ProjectId}/elements/{elementId}", new { name = "Renamed" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await commenter.PostAsJsonAsync($"/api/projects/{p.ProjectId}/drafts", new { id = Guid.NewGuid(), name = "d" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await commenter.PostAsync(p.Threads($"/{thread.Id}/raise-decision"), JsonContent.Create(new { title = "t", decision = "d" }))).StatusCode);

        // The viewer reads the threads and writes nothing.
        Assert.Single(await ListAsync(viewer, p));
        Assert.Equal(HttpStatusCode.Forbidden, (await OpenThreadAsync(viewer, p, elementId, "Me too")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsJsonAsync(p.Threads($"/{thread.Id}/replies"), new { body = "Me too" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsync(p.Threads($"/{thread.Id}/reopen"), null)).StatusCode);

        // A commenter resolves only a thread they started; an Editor resolves any.
        var ownerThread = await ThreadAsync(OpenThreadAsync(p.Owner, p, elementId, "Owner's thread"));
        Assert.Equal(HttpStatusCode.Forbidden, (await commenter.PostAsync(p.Threads($"/{ownerThread.Id}/resolve"), null)).StatusCode);
        var editor = await TestHost.SignInMemberAsync(fixture.Factory, p.Owner, p.WorkspaceId, "Editor");
        Assert.Equal(HttpStatusCode.OK, (await editor.PostAsync(p.Threads($"/{ownerThread.Id}/resolve"), null)).StatusCode);

        // The role is offered and kept by its name.
        var members = await p.Owner.GetFromJsonAsync<JsonObject>($"/api/workspaces/{p.WorkspaceId}/members");
        Assert.Contains(members!["members"]!.AsArray(), m => m!["role"]!.GetValue<string>() == "Commenter");
    }

    [Fact]
    public async Task A_thread_on_a_deleted_element_is_detached_with_the_last_name_it_had()
    {
        var p = await NewProjectAsync();
        var elementId = await AddElementAsync(p, "Billing");
        var thread = await ThreadAsync(OpenThreadAsync(p.Owner, p, elementId, "Split this?"));

        // Renamed and then deleted, with no read of the threads in between.
        Assert.Equal(HttpStatusCode.OK, (await p.Owner.PatchAsJsonAsync($"/api/projects/{p.ProjectId}/elements/{elementId}", new { name = "Invoicing" })).StatusCode);
        Assert.True((await p.Owner.DeleteAsync($"/api/projects/{p.ProjectId}/elements/{elementId}")).IsSuccessStatusCode);

        var read = Assert.Single(await ListAsync(p.Owner, p));
        Assert.Equal(thread.Id, read.Id);
        Assert.Equal("detached", read.Status);
        Assert.Equal("Invoicing", read.Target.Name);
        Assert.Equal(elementId, read.Target.Id);
        Assert.Equal("Split this?", read.Comments[0].Body);
    }

    [Fact]
    public async Task A_thread_needs_a_target_that_exists_and_keeps_the_model_version_it_was_made_at()
    {
        var p = await NewProjectAsync();
        var elementId = await AddElementAsync(p, "Gateway");
        Assert.Equal(HttpStatusCode.BadRequest, (await OpenThreadAsync(p.Owner, p, Guid.NewGuid(), "Nothing there")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await p.Owner.PostAsJsonAsync(p.Threads(),
            new { target = new { kind = "canvas", id = elementId }, body = "x" })).StatusCode);

        var store = fixture.Factory.Services.GetRequiredService<IEventStore>();
        var streamId = await MainStreamAsync(p);
        var (_, before) = await store.Load<ModelState>(StreamId.From(streamId), default);

        var thread = await ThreadAsync(OpenThreadAsync(p.Owner, p, elementId, "On the gateway"));
        await ThreadAsync(p.Owner.PostAsJsonAsync(p.Threads(), new { target = new { kind = "project", id = p.ProjectId }, body = "On the project" }));
        await p.Owner.PostAsJsonAsync(p.Threads($"/{thread.Id}/replies"), new { body = "Reply" });
        await p.Owner.PostAsync(p.Threads($"/{thread.Id}/resolve"), null);

        // Comments are outside the model stream: no event, no revision.
        var (_, after) = await store.Load<ModelState>(StreamId.From(streamId), default);
        Assert.Equal(before, after);
        Assert.Equal(before, thread.ModelVersion);
        Assert.Empty((await p.Owner.GetFromJsonAsync<JsonArray>($"/api/projects/{p.ProjectId}/revisions"))!);
    }

    [Fact]
    public async Task An_author_edits_and_an_owner_delete_of_another_persons_comment_leaves_a_tombstone_and_an_audit_entry()
    {
        var p = await NewProjectAsync();
        var elementId = await AddElementAsync(p, "Queue");
        var commenter = await TestHost.SignInMemberAsync(fixture.Factory, p.Owner, p.WorkspaceId, "Commenter");
        var editor = await TestHost.SignInMemberAsync(fixture.Factory, p.Owner, p.WorkspaceId, "Editor");
        var commenterId = await UserIdAsync(commenter);

        var thread = await ThreadAsync(OpenThreadAsync(commenter, p, elementId, "Secret plan text"));
        var commentId = thread.Comments[0].Id;
        var comment = $"/{thread.Id}/comments/{commentId}";

        Assert.Equal(HttpStatusCode.Forbidden, (await p.Owner.PatchAsJsonAsync(p.Threads(comment), new { body = "Owner rewrite" })).StatusCode);
        var edited = await (await commenter.PatchAsJsonAsync(p.Threads(comment), new { body = "Edited plan text" })).Content.ReadFromJsonAsync<CommentDto>();
        Assert.Equal("Edited plan text", edited!.Body);
        Assert.NotNull(edited.EditedAt);

        // An Editor who is not the author cannot delete it; an Owner can.
        Assert.Equal(HttpStatusCode.Forbidden, (await editor.DeleteAsync(p.Threads(comment))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await p.Owner.DeleteAsync(p.Threads(comment))).StatusCode);

        var tombstone = Assert.Single(Assert.Single(await ListAsync(p.Owner, p)).Comments);
        Assert.True(tombstone.Deleted);
        Assert.Null(tombstone.Body);
        Assert.Equal(commenterId, tombstone.Author);

        var audit = await p.Owner.GetFromJsonAsync<List<AuditEntryDto>>($"/api/workspaces/{p.WorkspaceId}/audit");
        var entry = Assert.Single(audit!, a => a.Action == AuditLog.CommentDeletedByOwner);
        Assert.Contains($"comment:{commentId}", entry.Target);
        Assert.Contains($"author:{commenterId}", entry.Target);
        Assert.DoesNotContain("plan text", entry.Target);

        // An author's own delete is no moderation and is not audited.
        var own = await ThreadAsync(OpenThreadAsync(commenter, p, elementId, "Mine"));
        Assert.Equal(HttpStatusCode.NoContent, (await commenter.DeleteAsync(p.Threads($"/{own.Id}/comments/{own.Comments[0].Id}"))).StatusCode);
        audit = await p.Owner.GetFromJsonAsync<List<AuditEntryDto>>($"/api/workspaces/{p.WorkspaceId}/audit");
        Assert.Single(audit!, a => a.Action == AuditLog.CommentDeletedByOwner);
    }

    [Fact]
    public async Task Comments_are_absent_from_the_model_export_the_sdd_and_the_agent_brief()
    {
        var p = await NewProjectAsync();
        var elementId = await AddElementAsync(p, "Ledger");
        const string marker = "zq-comment-marker-7731";
        var thread = await ThreadAsync(OpenThreadAsync(p.Owner, p, elementId, $"Thread {marker}"));
        await p.Owner.PostAsJsonAsync(p.Threads($"/{thread.Id}/replies"), new { body = $"Reply {marker}" });

        var export = await p.Owner.GetStringAsync($"/api/projects/{p.ProjectId}/export");
        Assert.Contains("Ledger", export);
        Assert.DoesNotContain(marker, export);
        Assert.DoesNotContain(thread.Id.ToString(), export);

        var sdd = await p.Owner.PostAsJsonAsync($"/api/projects/{p.ProjectId}/build/export", new { format = "sdd" });
        Assert.Equal(HttpStatusCode.OK, sdd.StatusCode);
        Assert.DoesNotContain(marker, await sdd.Content.ReadAsStringAsync());

        var brief = await p.Owner.PostAsJsonAsync($"/api/projects/{p.ProjectId}/build/brief", new { elementIds = new[] { elementId } });
        Assert.Equal(HttpStatusCode.OK, brief.StatusCode);
        Assert.DoesNotContain(marker, await brief.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_draft_merge_moves_its_open_threads_to_main_with_their_targets()
    {
        var p = await NewProjectAsync();
        await AddElementAsync(p, "Main element");
        var draft = (await (await TestHost.Created(p.Owner.PostAsJsonAsync($"/api/projects/{p.ProjectId}/drafts",
            new { id = Guid.NewGuid(), name = "Auth" }))).Content.ReadFromJsonAsync<DraftDto>())!;
        var draftElement = await AddElementAsync(p, "Auth Service", draft.StreamId);

        // A draft thread needs the target on the draft; main does not have it.
        Assert.Equal(HttpStatusCode.BadRequest, (await OpenThreadAsync(p.Owner, p, draftElement, "Not on main")).StatusCode);
        var open = await ThreadAsync(OpenThreadAsync(p.Owner, p, draftElement, "Token lifetime?", draft.Id));
        var resolved = await ThreadAsync(OpenThreadAsync(p.Owner, p, draftElement, "Typo", draft.Id));
        await p.Owner.PostAsync(p.Threads($"/{resolved.Id}/resolve"), null);
        Assert.Empty(await ListAsync(p.Owner, p));
        Assert.Equal(2, (await ListAsync(p.Owner, p, draft.Id)).Count);

        var preview = (await (await p.Owner.PostAsync($"/api/projects/{p.ProjectId}/drafts/{draft.Id}/merge/preview", null))
            .Content.ReadFromJsonAsync<MergePreviewResponse>())!;
        Assert.Equal(HttpStatusCode.OK, (await p.Owner.PostAsJsonAsync($"/api/projects/{p.ProjectId}/drafts/{draft.Id}/merge/execute",
            new { resolutions = Array.Empty<object>(), expectedMainVersion = preview.MainVersion })).StatusCode);

        var moved = Assert.Single(await ListAsync(p.Owner, p));
        Assert.Equal(open.Id, moved.Id);
        Assert.Null(moved.DraftId);
        Assert.Equal("open", moved.Status);
        Assert.Equal(draftElement, moved.Target.Id);
        Assert.Equal("Auth Service", moved.Target.Name);
        // The resolved thread stays with the merged draft, which takes no more writes.
        var left = Assert.Single(await ListAsync(p.Owner, p, draft.Id));
        Assert.Equal(resolved.Id, left.Id);
        Assert.Equal(HttpStatusCode.Conflict, (await p.Owner.PostAsJsonAsync(p.Threads($"/{left.Id}/replies"), new { body = "late" })).StatusCode);
    }

    [Fact]
    public async Task A_mention_and_a_reply_reach_the_inbox_with_unread_state()
    {
        var p = await NewProjectAsync();
        var elementId = await AddElementAsync(p, "Search");
        var alice = await TestHost.SignInMemberAsync(fixture.Factory, p.Owner, p.WorkspaceId, "Commenter");
        var bob = await TestHost.SignInMemberAsync(fixture.Factory, p.Owner, p.WorkspaceId, "Viewer");
        var bobId = await UserIdAsync(bob);
        var stranger = await TestHost.SignInAsync(fixture.Factory);

        Assert.Equal(HttpStatusCode.BadRequest, (await OpenThreadAsync(alice, p, elementId, "@stranger", mentions: [await UserIdAsync(stranger)])).StatusCode);

        var thread = await ThreadAsync(OpenThreadAsync(alice, p, elementId, "@Bob please look", mentions: [bobId]));
        var bobInbox = (await bob.GetFromJsonAsync<InboxResponse>("/api/inbox"))!;
        Assert.Equal(1, bobInbox.Unread);
        var mention = Assert.Single(bobInbox.Items);
        Assert.Equal("mention", mention.Reason);
        Assert.Equal(thread.Id, mention.ThreadId);
        Assert.Equal("Search", mention.Target.Name);
        Assert.Equal("@Bob please look", mention.Excerpt);

        // The owner replies: alice started the thread and hears of it; the owner does not hear of their own reply.
        await p.Owner.PostAsJsonAsync(p.Threads($"/{thread.Id}/replies"), new { body = "On it" });
        var aliceInbox = (await alice.GetFromJsonAsync<InboxResponse>("/api/inbox"))!;
        Assert.Equal("reply", Assert.Single(aliceInbox.Items).Reason);
        Assert.Empty((await p.Owner.GetFromJsonAsync<InboxResponse>("/api/inbox"))!.Items);

        // Alice replies: the owner replied before, so the owner hears of it.
        await alice.PostAsJsonAsync(p.Threads($"/{thread.Id}/replies"), new { body = "Thanks" });
        Assert.Equal(1, (await p.Owner.GetFromJsonAsync<InboxResponse>("/api/inbox"))!.Unread);

        Assert.Equal(HttpStatusCode.NoContent, (await bob.PostAsync($"/api/inbox/{mention.Id}/read", null)).StatusCode);
        Assert.Equal(0, (await bob.GetFromJsonAsync<InboxResponse>("/api/inbox"))!.Unread);
        // One person cannot mark another person's entry read.
        Assert.Equal(HttpStatusCode.NotFound, (await alice.PostAsync($"/api/inbox/{mention.Id}/read", null)).StatusCode);

        // An entry of a workspace the person left is not shown.
        Assert.Equal(HttpStatusCode.NoContent, (await p.Owner.DeleteAsync($"/api/workspaces/{p.WorkspaceId}/members/{bobId}")).StatusCode);
        Assert.Empty((await bob.GetFromJsonAsync<InboxResponse>("/api/inbox"))!.Items);
    }

    [Fact]
    public async Task Threads_of_one_workspace_are_out_of_reach_of_another()
    {
        var p = await NewProjectAsync();
        var elementId = await AddElementAsync(p, "Private");
        var thread = await ThreadAsync(OpenThreadAsync(p.Owner, p, elementId, "Internal"));

        var other = await NewProjectAsync();
        // Not a member: refused, with the same answer as an unknown project.
        Assert.Equal(HttpStatusCode.Forbidden, (await other.Owner.GetAsync(p.Threads())).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await other.Owner.GetAsync(p.Threads($"/{thread.Id}"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await other.Owner.PostAsJsonAsync(p.Threads($"/{thread.Id}/replies"), new { body = "x" })).StatusCode);
        // The thread ID through the other project's own route finds nothing.
        Assert.Equal(HttpStatusCode.NotFound, (await other.Owner.GetAsync(other.Threads($"/{thread.Id}"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.Owner.PostAsJsonAsync(other.Threads($"/{thread.Id}/replies"), new { body = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.Owner.DeleteAsync(other.Threads($"/{thread.Id}/comments/{thread.Comments[0].Id}"))).StatusCode);
        // A target of another project is no target here.
        Assert.Equal(HttpStatusCode.BadRequest, (await OpenThreadAsync(other.Owner, other, elementId, "x")).StatusCode);
        // Reusing the thread ID in another project is refused, not read.
        var reuse = await other.Owner.PostAsJsonAsync(other.Threads(), new { id = thread.Id, target = new { kind = "project", id = other.ProjectId }, body = "x" });
        Assert.Equal(HttpStatusCode.Conflict, reuse.StatusCode);
        Assert.DoesNotContain("Internal", await reuse.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_thread_raised_as_a_question_or_a_decision_cites_the_thread()
    {
        var p = await NewProjectAsync();
        var elementId = await AddElementAsync(p, "Cache");
        var commenter = await TestHost.SignInMemberAsync(fixture.Factory, p.Owner, p.WorkspaceId, "Commenter");
        var thread = await ThreadAsync(OpenThreadAsync(commenter, p, elementId, "What is the eviction policy?"));

        var question = await (await TestHost.Created(commenter.PostAsync(p.Threads($"/{thread.Id}/raise-question"), null))).Content.ReadFromJsonAsync<JsonObject>();
        Assert.Contains("What is the eviction policy?", question!["text"]!.GetValue<string>());
        Assert.Contains(thread.Id.ToString(), question["text"]!.GetValue<string>());
        Assert.Contains(elementId, question["about"]!.AsArray().Select(a => a!.GetValue<Guid>()));
        Assert.Equal("open", question["status"]!.GetValue<string>());
        // Raising it again gives the same question.
        var again = await commenter.PostAsync(p.Threads($"/{thread.Id}/raise-question"), null);
        Assert.Equal(question["id"]!.GetValue<Guid>(), (await again.Content.ReadFromJsonAsync<JsonObject>())!["id"]!.GetValue<Guid>());

        var decision = await (await TestHost.Created(p.Owner.PostAsJsonAsync(p.Threads($"/{thread.Id}/raise-decision"),
            new { title = "Use LRU eviction", decision = "The cache evicts the least recently used entry." }))).Content.ReadFromJsonAsync<JsonObject>();
        Assert.Equal("proposed", decision!["status"]!.GetValue<string>());
        Assert.Contains(thread.Id.ToString(), decision["context"]!.GetValue<string>());

        var read = Assert.Single(await ListAsync(p.Owner, p));
        Assert.Equal(question["id"]!.GetValue<Guid>(), read.RaisedQuestionId);
        Assert.Equal(decision["id"]!.GetValue<Guid>(), read.RaisedDecisionId);
    }

    private async Task<Guid> MainStreamAsync(Project p) =>
        (await p.Owner.GetFromJsonAsync<JsonObject>($"/api/projects/{p.ProjectId}"))!["streamId"]!.GetValue<Guid>();
}
