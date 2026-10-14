using Microsoft.EntityFrameworkCore;
using Vellum.Shared;

namespace Vellum.Modules.Comments;

/// <param name="Reason"><c>mention</c> or <c>reply</c>.</param>
/// <param name="Excerpt">The start of the comment text, or null when the comment is deleted.</param>
public sealed record InboxItemDto(
    Guid Id, string Reason, Guid WorkspaceId, Guid ProjectId, string ProjectName, Guid? DraftId,
    Guid ThreadId, Guid CommentId, ThreadTargetDto Target, string Author, string? AuthorName,
    string? Excerpt, DateTimeOffset CreatedAt, DateTimeOffset? ReadAt);

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

        var threadIds = items.Select(i => i.ThreadId).Distinct().ToList();
        var commentIds = items.Select(i => i.CommentId).ToList();
        var projectIds = items.Select(i => i.ProjectId).Distinct().ToList();
        var threads = await db.CommentThreads.AsNoTracking().Where(t => threadIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id, ct);
        var comments = await db.Comments.AsNoTracking().Where(c => commentIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, ct);
        var projects = await db.Projects.AsNoTracking().Where(p => projectIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.Name, ct);
        var names = await ThreadReads.NamesAsync(db, comments.Values.Select(c => c.Author), ct);

        var dtos = items
            .Where(i => threads.ContainsKey(i.ThreadId) && comments.ContainsKey(i.CommentId) && projects.ContainsKey(i.ProjectId))
            .Select(i =>
            {
                var (thread, comment) = (threads[i.ThreadId], comments[i.CommentId]);
                return new InboxItemDto(
                    i.Id, EnumText.Of(i.Reason), i.WorkspaceId, i.ProjectId, projects[i.ProjectId], thread.DraftId,
                    i.ThreadId, i.CommentId, ThreadReads.TargetOf(thread), comment.Author, names.GetValueOrDefault(comment.Author),
                    comment.DeletedAt is null ? Excerpt(comment.Body) : null, i.CreatedAt, i.ReadAt);
            })
            .ToList();
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
