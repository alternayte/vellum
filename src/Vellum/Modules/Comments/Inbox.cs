using Microsoft.EntityFrameworkCore;
using Vellum.Shared;

namespace Vellum.Modules.Comments;

/// <param name="Reason"><c>mention</c>, <c>reply</c>, or <c>proposal</c> for a proposal that an agent submitted.</param>
/// <param name="ThreadId">The thread of a comment. Null for a proposal.</param>
/// <param name="CommentId">The comment. Null for a proposal.</param>
/// <param name="Target">What the thread is about. Null for a proposal.</param>
/// <param name="Author">Who wrote the comment, or <c>agent-token:&lt;id&gt;</c> for a proposal.</param>
/// <param name="AuthorName">The name of the author, or of the agent token.</param>
/// <param name="Excerpt">The start of the comment text, or null when the comment is deleted. For a proposal: the start of its request.</param>
/// <param name="Proposal">The proposal of a <c>proposal</c> entry. Null for a comment.</param>
public sealed record InboxItemDto(
    Guid Id, string Reason, Guid WorkspaceId, Guid ProjectId, string ProjectName, Guid? DraftId,
    Guid? ThreadId, Guid? CommentId, ThreadTargetDto? Target, string Author, string? AuthorName,
    string? Excerpt, DateTimeOffset CreatedAt, DateTimeOffset? ReadAt, InboxProposalDto? Proposal = null);

/// <summary>A proposal that an inbox entry names.</summary>
/// <param name="Kind"><c>architecture</c> for additions to the model, or <c>flow</c>.</param>
/// <param name="Name">The name of the proposal, from its summary.</param>
/// <param name="Items">For additions: how many items the proposal holds. Null for a flow.</param>
/// <param name="Pending">True while a person has something of it to decide.</param>
public sealed record InboxProposalDto(Guid Id, string Kind, string Name, int? Items, bool Pending);

public sealed record InboxResponse(int Unread, IReadOnlyList<InboxItemDto> Items);

/// <summary>Who hears of a new comment: the people it mentions, and the others in the thread when it is a reply.</summary>
internal static class CommentNotices
{
    /// <summary>
    /// Adds inbox entries for <paramref name="comment"/>. A mentioned person gets a
    /// <c>mention</c>; on a reply, the author of the thread and everyone who replied get a
    /// <c>reply</c>. The author of the comment gets nothing, and nor does a person who is no
    /// member of the workspace any more. One person gets one entry for one comment.
    /// </summary>
    public static async Task NotifyAsync(
        AppDbContext db, ThreadEntity thread, CommentEntity comment, IReadOnlyCollection<string> mentioned,
        bool isReply, DateTimeOffset now, CancellationToken ct)
    {
        var recipients = mentioned.Where(m => m != comment.Author).Select(m => (UserId: m, Reason: InboxReason.Mention)).ToList();
        if (isReply)
        {
            var participants = await db.Comments.AsNoTracking()
                .Where(c => c.ThreadId == thread.Id && c.DeletedAt == null).Select(c => c.Author).Distinct().ToListAsync(ct);
            recipients.AddRange(participants.Append(thread.CreatedBy).Distinct()
                .Where(p => p != comment.Author && !mentioned.Contains(p))
                .Select(p => (p, InboxReason.Reply)));
        }
        if (recipients.Count == 0)
            return;

        var ids = recipients.Select(r => r.UserId).ToList();
        var members = (await db.Memberships.AsNoTracking()
            .Where(m => m.WorkspaceId == thread.WorkspaceId && ids.Contains(m.UserId)).Select(m => m.UserId).ToListAsync(ct)).ToHashSet();
        foreach (var (userId, reason) in recipients.Where(r => members.Contains(r.UserId)))
        {
            // The label of the Postgres enum type: the member name in snake_case.
            var label = AppDatabase.EnumLabels.TranslateMemberName(reason.ToString());
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO comments.inbox_items (id, user_id, workspace_id, project_id, thread_id, comment_id, reason, created_at)
                VALUES ({Guid.NewGuid()}, {userId}, {thread.WorkspaceId}, {thread.ProjectId}, {thread.Id}, {comment.Id}, {label}::comments.inbox_reason, {now})
                ON CONFLICT (user_id, comment_id) DO NOTHING
                """, ct);
        }
    }
}

/// <summary>
/// Who hears of a proposal that an agent submitted: the person whose agent token submitted it.
/// The agent acts for that person, and the person decides in the app. A proposal that a person
/// submitted, or that the in-app assistant made, makes no entry.
/// </summary>
internal static class ProposalNotices
{
    public const string AgentOrigin = "agent-token:";

    /// <summary>Adds the entry to the unit of work of <paramref name="db"/>, so it is saved with the proposal or not at all.</summary>
    public static void Add(AppDbContext db, Guid workspaceId, Guid projectId, Guid proposalId, string? origin, string userId, DateTimeOffset now)
    {
        if (origin is null || !origin.StartsWith(AgentOrigin, StringComparison.Ordinal))
            return;
        db.InboxItems.Add(new InboxItemEntity
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            WorkspaceId = workspaceId,
            ProjectId = projectId,
            ProposalId = proposalId,
            Reason = InboxReason.Proposal,
            CreatedAt = now,
        });
    }
}

/// <summary>
/// The inbox of the signed-in person, across their workspaces. An entry of a workspace the
/// person is no member of any more is not shown.
/// </summary>
public static class InboxEndpoints
{
    public const int MaxItems = 200;
    private const int ExcerptLength = 280;

    public static async Task<IResult> List(bool? unreadOnly, HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var userId = http.User.UserId();
        var query = Visible(db, userId);
        if (unreadOnly == true)
            query = query.Where(i => i.ReadAt == null);
        var items = await query.OrderByDescending(i => i.CreatedAt).ThenBy(i => i.Id).Take(MaxItems).ToListAsync(ct);
        var unread = await Visible(db, userId).CountAsync(i => i.ReadAt == null, ct);

        var threadIds = items.Where(i => i.ThreadId is not null).Select(i => i.ThreadId!.Value).Distinct().ToList();
        var commentIds = items.Where(i => i.CommentId is not null).Select(i => i.CommentId!.Value).ToList();
        var proposalIds = items.Where(i => i.ProposalId is not null).Select(i => i.ProposalId!.Value).ToList();
        var projectIds = items.Select(i => i.ProjectId).Distinct().ToList();
        var threads = await db.CommentThreads.AsNoTracking().Where(t => threadIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id, ct);
        var comments = await db.Comments.AsNoTracking().Where(c => commentIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, ct);
        // A proposal that was discarded is gone, and so is its entry.
        var proposals = await db.DesignProposals.AsNoTracking().Where(p => proposalIds.Contains(p.Id))
            .Select(p => new { p.Id, p.Kind, p.Name, p.Request, p.Status, p.Origin, p.Document }).ToDictionaryAsync(p => p.Id, ct);
        var projects = await db.Projects.AsNoTracking().Where(p => projectIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.Name, ct);
        var names = await ThreadReads.NamesAsync(db, comments.Values.Select(c => c.Author), ct);
        var tokenIds = proposals.Values
            .Select(p => p.Origin is { } o && o.StartsWith(ProposalNotices.AgentOrigin, StringComparison.Ordinal) && Guid.TryParse(o[ProposalNotices.AgentOrigin.Length..], out var id) ? id : Guid.Empty)
            .ToList();
        var tokenNames = await db.AgentTokens.AsNoTracking().Where(t => tokenIds.Contains(t.Id)).ToDictionaryAsync(t => $"{ProposalNotices.AgentOrigin}{t.Id}", t => t.Name, ct);

        var dtos = new List<InboxItemDto>();
        foreach (var i in items.Where(i => projects.ContainsKey(i.ProjectId)))
        {
            if (i.ProposalId is { } proposalId)
            {
                if (!proposals.TryGetValue(proposalId, out var proposal))
                    continue;
                var architecture = proposal.Kind == Ai.DesignProposalKind.Architecture;
                var origin = proposal.Origin ?? "";
                dtos.Add(new InboxItemDto(
                    i.Id, EnumText.Of(i.Reason), i.WorkspaceId, i.ProjectId, projects[i.ProjectId], null,
                    null, null, null, origin, tokenNames.GetValueOrDefault(origin),
                    Excerpt(proposal.Request), i.CreatedAt, i.ReadAt,
                    new InboxProposalDto(proposalId, architecture ? Ai.Design.ArchitectureProposals.Kind : Ai.Design.FlowProposals.Kind, proposal.Name,
                        architecture ? Ai.Design.ArchitectureProposals.ItemCount(proposal.Document) : null,
                        proposal.Status == Ai.DesignProposalStatus.Draft)));
                continue;
            }
            if (i.ThreadId is not { } threadId || i.CommentId is not { } commentId
                || !threads.TryGetValue(threadId, out var thread) || !comments.TryGetValue(commentId, out var comment))
                continue;
            dtos.Add(new InboxItemDto(
                i.Id, EnumText.Of(i.Reason), i.WorkspaceId, i.ProjectId, projects[i.ProjectId], thread.DraftId,
                threadId, commentId, ThreadReads.TargetOf(thread), comment.Author, names.GetValueOrDefault(comment.Author),
                comment.DeletedAt is null ? Excerpt(comment.Body) : null, i.CreatedAt, i.ReadAt));
        }
        return Results.Ok(new InboxResponse(unread, dtos));
    }

    public static async Task<IResult> MarkRead(Guid itemId, HttpContext http, AppDbContext db, TimeProvider time, CancellationToken ct)
    {
        var userId = http.User.UserId();
        var now = time.GetUtcNow();
        var found = await db.InboxItems.Where(i => i.Id == itemId && i.UserId == userId)
            .ExecuteUpdateAsync(set => set.SetProperty(i => i.ReadAt, i => i.ReadAt ?? now), ct);
        return found == 1 ? Results.NoContent() : ResultExtensions.NotFound("Inbox entry");
    }

    public static async Task<IResult> MarkAllRead(HttpContext http, AppDbContext db, TimeProvider time, CancellationToken ct)
    {
        var userId = http.User.UserId();
        var now = time.GetUtcNow();
        await db.InboxItems.Where(i => i.UserId == userId && i.ReadAt == null)
            .ExecuteUpdateAsync(set => set.SetProperty(i => i.ReadAt, now), ct);
        return Results.NoContent();
    }

    private static IQueryable<InboxItemEntity> Visible(AppDbContext db, string userId) =>
        db.InboxItems.AsNoTracking().Where(i => i.UserId == userId
            && db.Memberships.Any(m => m.WorkspaceId == i.WorkspaceId && m.UserId == userId));

    private static string Excerpt(string body) =>
        body.Length <= ExcerptLength ? body : string.Concat(body.AsSpan(0, char.IsHighSurrogate(body[ExcerptLength - 1]) ? ExcerptLength - 1 : ExcerptLength), "…");
}
