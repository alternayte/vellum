using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Vellum.Modules.Drafts;
using Vellum.Modules.Settings;
using Vellum.Modules.Workspaces;
using Vellum.Shared;
using static Vellum.Shared.ResultExtensions;

namespace Vellum.Modules.Comments;

/// <param name="Kind"><c>project</c>, <c>element</c>, <c>relationship</c>, <c>message</c>, <c>operation</c>, <c>channel</c>, <c>step</c>, <c>promise</c>, <c>document</c> or <c>decision</c>.</param>
/// <param name="Id">The stable ID of the target. For <c>project</c> the project ID; for <c>step</c> the scenario ID.</param>
/// <param name="StepId">The step ID inside the scenario, for <c>step</c> only.</param>
public sealed record TargetRequest(string Kind, Guid Id, string? StepId = null);

/// <param name="Id">Optional. A repeated create with the same ID gives the thread that exists.</param>
/// <param name="DraftId">The draft the thread belongs to. Null for main.</param>
/// <param name="Mentions">The user IDs of the workspace members that the text mentions.</param>
public sealed record CreateThreadRequest(TargetRequest Target, string Body, Guid? Id = null, Guid? DraftId = null, IReadOnlyList<string>? Mentions = null);

public sealed record ReplyRequest(string Body, Guid? Id = null, IReadOnlyList<string>? Mentions = null);

public sealed record EditCommentRequest(string Body, IReadOnlyList<string>? Mentions = null);

/// <param name="Name">The current name of the target, or its last known name when the thread is detached.</param>
public sealed record ThreadTargetDto(string Kind, Guid Id, string? StepId, string Name);

/// <param name="Body">Null once the comment is deleted.</param>
/// <param name="EditedAt">When the author last changed the text. No earlier text is kept.</param>
public sealed record CommentDto(
    Guid Id, Guid ThreadId, string Author, string? AuthorName, string? Body, IReadOnlyList<string> Mentions,
    DateTimeOffset CreatedAt, DateTimeOffset? EditedAt, bool Deleted, DateTimeOffset? DeletedAt);

/// <param name="Status"><c>open</c>, <c>resolved</c> or <c>detached</c> (the target was deleted).</param>
/// <param name="ModelVersion">The version of the model stream <paramref name="ModelStreamId"/> when the thread was created.</param>
/// <param name="ModelStreamId">The model stream the thread was made on: main, or its draft, also after a merge moved it to main.</param>
/// <param name="RaisedQuestionId">The open question this thread was raised as.</param>
/// <param name="RaisedDecisionId">The proposed decision (ADR) this thread was raised as.</param>
public sealed record ThreadDto(
    Guid Id, Guid ProjectId, Guid? DraftId, ThreadTargetDto Target, long ModelVersion, Guid ModelStreamId, string Status,
    string CreatedBy, DateTimeOffset CreatedAt, string? ResolvedBy, DateTimeOffset? ResolvedAt,
    Guid? RaisedQuestionId, Guid? RaisedDecisionId, IReadOnlyList<CommentDto> Comments);

/// <summary>
/// Comment threads on the targets of a project. A Viewer reads them; a Commenter, an Editor
/// and an Owner write them. No write here touches a model stream or makes a revision.
/// </summary>
public static class ThreadEndpoints
{
    public const int MaxThreadsPerProject = 5_000;
    public const int MaxCommentsPerThread = 500;
    public const int MaxMentions = 20;

    public static async Task<IResult> List(
        Guid projectId, Guid? draftId, Guid? targetId, string? status,
        HttpContext http, WorkspaceAuthorizationService auth, AppDbContext db, CancellationToken ct)
    {
        await auth.RequireProjectRoleAsync(projectId, http.User.UserId(), WorkspaceRole.Viewer, ct);
        var (branch, refusal) = await ThreadBranch.ResolveAsync(db, projectId, draftId, forWrite: false, ct);
        if (branch is null)
            return refusal!;

        var query = db.CommentThreads.Where(t => t.ProjectId == projectId && t.DraftId == draftId);
        if (targetId is { } target)
            query = query.Where(t => t.TargetId == target);
        var threads = await query.OrderBy(t => t.CreatedAt).ThenBy(t => t.Id).ToListAsync(ct);

        await ThreadReads.RefreshAsync(http.RequestServices, branch, threads, ct);
        var dtos = await ThreadReads.ToDtosAsync(db, threads, ct);
        if (status is not null)
            dtos = [.. dtos.Where(t => t.Status == status)];
        return Results.Ok(new Page<ThreadDto>(dtos, null));
    }

    public static async Task<IResult> Get(
        Guid projectId, Guid threadId, HttpContext http, WorkspaceAuthorizationService auth, AppDbContext db, CancellationToken ct)
    {
        await auth.RequireProjectRoleAsync(projectId, http.User.UserId(), WorkspaceRole.Viewer, ct);
        if (await db.CommentThreads.FirstOrDefaultAsync(t => t.Id == threadId && t.ProjectId == projectId, ct) is not { } thread)
            return NotFound("Thread");
        var (branch, _) = await ThreadBranch.ResolveAsync(db, projectId, thread.DraftId, forWrite: false, ct);
        if (branch is not null)
            await ThreadReads.RefreshAsync(http.RequestServices, branch, [thread], ct);
        return Results.Ok((await ThreadReads.ToDtosAsync(db, [thread], ct))[0]);
    }

    public static async Task<IResult> Create(
        Guid projectId, CreateThreadRequest request, HttpContext http,
        WorkspaceAuthorizationService auth, AppDbContext db, TimeProvider time, CancellationToken ct)
    {
        var userId = http.User.UserId();
        await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Commenter, ct);

        if (request.Target is null)
            return Invalid("target", "A thread needs a target");
        if (ParseTarget(request.Target) is not { } target)
            return Invalid("target.kind", "Must be project, element, relationship, message, operation, channel, step, promise, document or decision; a step needs stepId");
        if (CheckBody(request.Body) is { } invalid)
            return Invalid(invalid);
        if (request.Id == Guid.Empty)
            return Invalid("id", "The ID must not be the empty GUID");

        if (request.Id is { } id && await db.CommentThreads.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id, ct) is { } existing)
        {
            if (existing.ProjectId != projectId || existing.CreatedBy != userId)
                return IdInUse.Conflict();
            return Results.Ok((await ThreadReads.ToDtosAsync(db, [existing], ct))[0]);
        }

        var (branch, refusal) = await ThreadBranch.ResolveAsync(db, projectId, request.DraftId, forWrite: true, ct);
        if (branch is null)
            return refusal!;
        var (mentions, mentionRefusal) = await CheckMentionsAsync(db, branch.WorkspaceId, request.Mentions, ct);
        if (mentions is null)
            return mentionRefusal!;

        var book = await TargetBook.LoadAsync(http.RequestServices, projectId, branch.ModelStreamId, [target], ct);
        if (book.NameOf(target) is not { } name)
            return Invalid("target", $"No {EnumText.Of(target.Kind)} with this ID exists on this branch of the project");
        if (await db.CommentThreads.CountAsync(t => t.ProjectId == projectId, ct) >= MaxThreadsPerProject)
            return Conflict($"A project holds at most {MaxThreadsPerProject} comment threads.");

        var now = time.GetUtcNow();
        var thread = new ThreadEntity
        {
            Id = request.Id ?? Guid.NewGuid(),
            WorkspaceId = branch.WorkspaceId,
            ProjectId = projectId,
            DraftId = request.DraftId,
            TargetKind = target.Kind,
            TargetId = target.Id,
            StepId = target.StepId,
            TargetName = name,
            ModelVersion = book.ModelVersion,
            ModelStreamId = branch.ModelStreamId,
            CreatedBy = userId,
            CreatedAt = now,
        };
        var comment = new CommentEntity
        {
            Id = Guid.NewGuid(), ThreadId = thread.Id, Author = userId, Body = request.Body.Trim(), Mentions = [.. mentions], CreatedAt = now,
        };

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        if (await ThreadBranch.LockOpenAsync(db, request.DraftId, ct) is { } closedNow)
            return closedNow;
        db.CommentThreads.Add(thread);
        db.Comments.Add(comment);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException e) when (e.InnerException is Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.UniqueViolation })
        {
            // A second create with the same ID committed first.
            return IdInUse.Conflict();
        }
        await CommentNotices.NotifyAsync(db, thread, comment, mentions, isReply: false, now, ct);
        await CommentWebhooks.EnqueueAsync(http, thread, comment, CommentWebhooks.Created, now, ct);
        if (mentions.Count > 0)
            await CommentWebhooks.EnqueueAsync(http, thread, comment, CommentWebhooks.Mentioned, now, ct, mentions);
        await transaction.CommitAsync(ct);
        CommentWebhooks.Wake(http);

        return Results.Created($"/api/projects/{projectId}/threads/{thread.Id}", (await ThreadReads.ToDtosAsync(db, [thread], ct))[0]);
    }

    public static async Task<IResult> Reply(
        Guid projectId, Guid threadId, ReplyRequest request, HttpContext http,
        WorkspaceAuthorizationService auth, AppDbContext db, TimeProvider time, CancellationToken ct)
    {
        var userId = http.User.UserId();
        await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Commenter, ct);
        if (CheckBody(request.Body) is { } invalid)
            return Invalid(invalid);
        if (request.Id == Guid.Empty)
            return Invalid("id", "The ID must not be the empty GUID");

        if (await db.CommentThreads.FirstOrDefaultAsync(t => t.Id == threadId && t.ProjectId == projectId, ct) is not { } thread)
            return NotFound("Thread");
        // A thread left on a merged or abandoned draft is history.
        if ((await ThreadBranch.ResolveAsync(db, projectId, thread.DraftId, forWrite: true, ct)).Refusal is { } closed)
            return closed;
        if (request.Id is { } id && await db.Comments.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct) is { } existing)
        {
            return existing.ThreadId == threadId && existing.Author == userId
                ? Results.Ok(await ThreadReads.CommentDtoAsync(db, existing, ct))
                : IdInUse.Conflict();
        }
        var (mentions, mentionRefusal) = await CheckMentionsAsync(db, thread.WorkspaceId, request.Mentions, ct);
        if (mentions is null)
            return mentionRefusal!;
        if (await db.Comments.CountAsync(c => c.ThreadId == threadId, ct) >= MaxCommentsPerThread)
            return Conflict($"A thread holds at most {MaxCommentsPerThread} comments. Start a new thread.");

        var now = time.GetUtcNow();
        var comment = new CommentEntity
        {
            Id = request.Id ?? Guid.NewGuid(), ThreadId = threadId, Author = userId, Body = request.Body.Trim(), Mentions = [.. mentions], CreatedAt = now,
        };

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        if (await ThreadBranch.LockOpenAsync(db, thread.DraftId, ct) is { } closedNow)
            return closedNow;
        db.Comments.Add(comment);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException e) when (e.InnerException is Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.UniqueViolation })
        {
            return IdInUse.Conflict();
        }
        await CommentNotices.NotifyAsync(db, thread, comment, mentions, isReply: true, now, ct);
        await CommentWebhooks.EnqueueAsync(http, thread, comment, CommentWebhooks.Created, now, ct);
        if (mentions.Count > 0)
            await CommentWebhooks.EnqueueAsync(http, thread, comment, CommentWebhooks.Mentioned, now, ct, mentions);
        await transaction.CommitAsync(ct);
        CommentWebhooks.Wake(http);

        return Results.Created($"/api/projects/{projectId}/threads/{threadId}", await ThreadReads.CommentDtoAsync(db, comment, ct));
    }

    /// <summary>Only the author changes the text. The comment then shows when it was edited; no earlier text is kept.</summary>
    public static async Task<IResult> Edit(
        Guid projectId, Guid threadId, Guid commentId, EditCommentRequest request, HttpContext http,
        WorkspaceAuthorizationService auth, AppDbContext db, TimeProvider time, CancellationToken ct)
    {
        var userId = http.User.UserId();
        await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Commenter, ct);
        if (CheckBody(request.Body) is { } invalid)
            return Invalid(invalid);

        var (thread, comment) = await FindAsync(db, projectId, threadId, commentId, ct);
        if (comment is null)
            return NotFound("Comment");
        if (comment.Author != userId)
            return Forbidden("Only the author edits a comment");
        if (comment.DeletedAt is not null)
            return Conflict("The comment is deleted");
        if ((await ThreadBranch.ResolveAsync(db, projectId, thread!.DraftId, forWrite: true, ct)).Refusal is { } closed)
            return closed;
        var (mentions, mentionRefusal) = await CheckMentionsAsync(db, thread.WorkspaceId, request.Mentions, ct);
        if (mentions is null)
            return mentionRefusal!;

        var now = time.GetUtcNow();
        var added = mentions.Except(comment.Mentions).ToList();
        comment.Body = request.Body.Trim();
        comment.Mentions = [.. mentions];
        comment.EditedAt = now;

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        if (await ThreadBranch.LockOpenAsync(db, thread.DraftId, ct) is { } closedNow)
            return closedNow;
        await db.SaveChangesAsync(ct);
        // Only a person the edit mentions for the first time hears of it.
        if (added.Count > 0)
        {
            await CommentNotices.NotifyAsync(db, thread, comment, added, isReply: false, now, ct);
            await CommentWebhooks.EnqueueAsync(http, thread, comment, CommentWebhooks.Mentioned, now, ct, added);
        }
        await transaction.CommitAsync(ct);
        CommentWebhooks.Wake(http);
        return Results.Ok(await ThreadReads.CommentDtoAsync(db, comment, ct));
    }

    /// <summary>
    /// The author, or an Owner, deletes a comment. The row stays as a tombstone with no text,
    /// so the replies around it keep their place. An Owner's delete of another person's
    /// comment goes to the audit log.
    /// </summary>
    public static async Task<IResult> Delete(
        Guid projectId, Guid threadId, Guid commentId, HttpContext http,
        WorkspaceAuthorizationService auth, AppDbContext db, TimeProvider time, CancellationToken ct)
    {
        var userId = http.User.UserId();
        await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Commenter, ct);

        var (thread, comment) = await FindAsync(db, projectId, threadId, commentId, ct);
        if (comment is null)
            return NotFound("Comment");
        var moderated = comment.Author != userId;
        if (moderated && await auth.GetRoleAsync(thread!.WorkspaceId, userId, ct) != WorkspaceRole.Owner)
            return Forbidden("Only the author or an Owner deletes a comment");
        if (comment.DeletedAt is not null)
            return Results.NoContent();

        comment.Body = "";
        comment.Mentions = [];
        comment.DeletedAt = time.GetUtcNow();
        comment.DeletedBy = userId;

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.SaveChangesAsync(ct);
        if (moderated)
        {
            await AuditLog.AppendAsync(db, thread!.WorkspaceId, userId, AuditLog.CommentDeletedByOwner,
                $"comment:{comment.Id} thread:{thread.Id} project:{projectId} author:{comment.Author}", ct);
        }
        await transaction.CommitAsync(ct);
        return Results.NoContent();
    }

    public static Task<IResult> Resolve(
        Guid projectId, Guid threadId, HttpContext http, WorkspaceAuthorizationService auth, AppDbContext db, TimeProvider time, CancellationToken ct) =>
        SetStatusAsync(projectId, threadId, ThreadStatus.Resolved, http, auth, db, time, ct);

    public static Task<IResult> Reopen(
        Guid projectId, Guid threadId, HttpContext http, WorkspaceAuthorizationService auth, AppDbContext db, TimeProvider time, CancellationToken ct) =>
        SetStatusAsync(projectId, threadId, ThreadStatus.Open, http, auth, db, time, ct);

    /// <summary>The author of the thread, an Editor or an Owner resolves or reopens it.</summary>
    private static async Task<IResult> SetStatusAsync(
        Guid projectId, Guid threadId, ThreadStatus status, HttpContext http,
        WorkspaceAuthorizationService auth, AppDbContext db, TimeProvider time, CancellationToken ct)
    {
        var userId = http.User.UserId();
        await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Commenter, ct);
        if (await db.CommentThreads.FirstOrDefaultAsync(t => t.Id == threadId && t.ProjectId == projectId, ct) is not { } thread)
            return NotFound("Thread");
        if (thread.CreatedBy != userId && !(await auth.GetRoleAsync(thread.WorkspaceId, userId, ct) >= WorkspaceRole.Editor))
            return Forbidden("Only the author of the thread, an Editor or an Owner resolves or reopens it");
        if ((await ThreadBranch.ResolveAsync(db, projectId, thread.DraftId, forWrite: true, ct)).Refusal is { } closed)
            return closed;

        if (thread.Status != status)
        {
            var now = time.GetUtcNow();
            thread.Status = status;
            thread.ResolvedBy = status == ThreadStatus.Resolved ? userId : null;
            thread.ResolvedAt = status == ThreadStatus.Resolved ? now : null;
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            if (await ThreadBranch.LockOpenAsync(db, thread.DraftId, ct) is { } closedNow)
                return closedNow;
            await db.SaveChangesAsync(ct);
            if (status == ThreadStatus.Resolved)
                await CommentWebhooks.EnqueueAsync(http, thread, null, CommentWebhooks.Resolved, now, ct);
            await transaction.CommitAsync(ct);
            CommentWebhooks.Wake(http);
        }

        var (branch, _) = await ThreadBranch.ResolveAsync(db, projectId, thread.DraftId, forWrite: false, ct);
        if (branch is not null)
            await ThreadReads.RefreshAsync(http.RequestServices, branch, [thread], ct);
        return Results.Ok((await ThreadReads.ToDtosAsync(db, [thread], ct))[0]);
    }

    internal static TargetRef? ParseTarget(TargetRequest target)
    {
        if (!EnumText.TryParse<TargetKind>(target.Kind, out var kind) || target.Id == Guid.Empty)
            return null;
        if (kind == TargetKind.Step)
        {
            return string.IsNullOrWhiteSpace(target.StepId) || TextRule.Line("stepId", target.StepId, TextRule.MaxIdentifier) is not null
                ? null
                : new TargetRef(kind, target.Id, target.StepId);
        }
        return new TargetRef(kind, target.Id, null);
    }

    private static ValidationError? CheckBody(string? body) =>
        string.IsNullOrWhiteSpace(body) ? new ValidationError("body", "A comment needs text") : TextRule.Lines("body", body);

    /// <summary>The mentioned user IDs, each a member of the workspace; or the refusal.</summary>
    private static async Task<(IReadOnlyList<string>? Mentions, IResult? Refusal)> CheckMentionsAsync(
        AppDbContext db, Guid workspaceId, IReadOnlyList<string>? requested, CancellationToken ct)
    {
        var ids = (requested ?? []).Where(m => !string.IsNullOrEmpty(m)).Distinct(StringComparer.Ordinal).ToList();
        if (ids.Count > MaxMentions)
            return (null, Invalid("mentions", $"A comment mentions at most {MaxMentions} people"));
        if (ids.Count == 0)
            return ([], null);
        var members = await db.Memberships.AsNoTracking()
            .Where(m => m.WorkspaceId == workspaceId && ids.Contains(m.UserId)).Select(m => m.UserId).ToListAsync(ct);
        return members.Count == ids.Count
            ? (ids, null)
            : (null, Invalid("mentions", "A mention names a member of the workspace by user ID"));
    }

    private static async Task<(ThreadEntity? Thread, CommentEntity? Comment)> FindAsync(
        AppDbContext db, Guid projectId, Guid threadId, Guid commentId, CancellationToken ct)
    {
        var thread = await db.CommentThreads.AsNoTracking().FirstOrDefaultAsync(t => t.Id == threadId && t.ProjectId == projectId, ct);
        var comment = thread is null ? null : await db.Comments.FirstOrDefaultAsync(c => c.Id == commentId && c.ThreadId == threadId, ct);
        return (thread, comment);
    }

    private static IResult Forbidden(string message) =>
        Results.Json(new ErrorResponse("forbidden", message), statusCode: StatusCodes.Status403Forbidden);
}

/// <summary>The branch a thread is on: main or one draft, with the model stream that holds its targets.</summary>
internal sealed record ThreadBranch(Guid WorkspaceId, Guid ProjectId, Guid? DraftId, Guid ModelStreamId)
{
    /// <summary>
    /// The branch of <paramref name="draftId"/> (main when null). A read reaches a draft of any
    /// status; a write needs an open draft.
    /// </summary>
    public static async Task<(ThreadBranch? Branch, IResult? Refusal)> ResolveAsync(
        AppDbContext db, Guid projectId, Guid? draftId, bool forWrite, CancellationToken ct)
    {
        var project = await db.Projects.AsNoTracking().Where(p => p.Id == projectId)
            .Select(p => new { p.WorkspaceId, p.StreamId }).FirstAsync(ct);
        if (draftId is null)
            return (new ThreadBranch(project.WorkspaceId, projectId, null, project.StreamId), null);

        var draft = await db.Drafts.AsNoTracking().FirstOrDefaultAsync(d => d.Id == draftId && d.ProjectId == projectId, ct);
        if (draft is null)
            return (null, NotFound("Draft"));
        if (forWrite && draft.Status != DraftStatus.Open)
            return (null, Conflict($"Draft is {EnumText.Of(draft.Status)}"));
        return (new ThreadBranch(project.WorkspaceId, projectId, draftId, draft.StreamId), null);
    }

    /// <summary>
    /// In the open transaction, holds the draft row against a merge or an abandon until the
    /// commit, and refuses when the draft is not open any more. A merge that commits first is
    /// seen here; a merge that comes after waits, then moves the new thread to main.
    /// </summary>
    public static async Task<IResult?> LockOpenAsync(AppDbContext db, Guid? draftId, CancellationToken ct)
    {
        if (draftId is not { } id)
            return null;
        var status = await db.Database.SqlQuery<string>(
            $"SELECT status::text AS \"Value\" FROM drafts.drafts WHERE id = {id} FOR SHARE").ToListAsync(ct);
        return status is ["open"] ? null : Conflict($"Draft is {(status.Count == 0 ? "gone" : status[0])}");
    }
}

/// <summary>Reads threads for the API, and keeps their target names and detached marks current.</summary>
internal static class ThreadReads
{
    /// <summary>
    /// Checks each target of <paramref name="threads"/> (tracked rows of one branch) against the
    /// branch as it is now. A target that exists gives its current name and clears the
    /// detached mark. A target that is gone marks the thread detached, with the last name the
    /// target had: for a model target that name comes from the history of the model stream.
    /// </summary>
    public static async Task RefreshAsync(IServiceProvider services, ThreadBranch branch, IReadOnlyList<ThreadEntity> threads, CancellationToken ct)
    {
        if (threads.Count == 0)
            return;
        var db = services.GetRequiredService<AppDbContext>();
        var book = await TargetBook.LoadAsync(services, branch.ProjectId, branch.ModelStreamId, threads.Select(Ref), ct);
        var now = services.GetRequiredService<TimeProvider>().GetUtcNow();

        var gone = new List<ThreadEntity>();
        foreach (var thread in threads)
        {
            if (book.NameOf(Ref(thread)) is { } name)
            {
                thread.TargetName = name;
                thread.Detached = false;
                thread.DetachedAt = null;
            }
            else if (!thread.Detached)
            {
                gone.Add(thread);
            }
        }

        var modelTargets = gone.Where(t => TargetBook.IsModelKind(t.TargetKind)).Select(Ref).Distinct().ToList();
        var lastNames = await TargetBook.LastNamesAsync(services.GetRequiredService<Deedbox.IEventStore>(), book.ModelStreamId, modelTargets, ct);
        foreach (var thread in gone)
        {
            thread.Detached = true;
            thread.DetachedAt = now;
            if (lastNames.GetValueOrDefault(Ref(thread)) is { } last)
                thread.TargetName = last;
        }

        await db.SaveChangesAsync(ct);
    }

    public static TargetRef Ref(ThreadEntity thread) => new(thread.TargetKind, thread.TargetId, thread.StepId);

    public static string StatusOf(ThreadEntity thread) => thread.Detached ? "detached" : EnumText.Of(thread.Status);

    public static ThreadTargetDto TargetOf(ThreadEntity thread) =>
        new(EnumText.Of(thread.TargetKind), thread.TargetId, thread.StepId, thread.TargetName);

    public static async Task<List<ThreadDto>> ToDtosAsync(AppDbContext db, IReadOnlyList<ThreadEntity> threads, CancellationToken ct)
    {
        var ids = threads.Select(t => t.Id).ToList();
        var comments = await db.Comments.AsNoTracking().Where(c => ids.Contains(c.ThreadId))
            .OrderBy(c => c.CreatedAt).ThenBy(c => c.Id).ToListAsync(ct);
        var names = await NamesAsync(db, comments.Select(c => c.Author), ct);
        var byThread = comments.ToLookup(c => c.ThreadId);
        return
        [
            .. threads.Select(t => new ThreadDto(
                t.Id, t.ProjectId, t.DraftId, TargetOf(t), t.ModelVersion, t.ModelStreamId, StatusOf(t),
                t.CreatedBy, t.CreatedAt, t.ResolvedBy, t.ResolvedAt, t.RaisedQuestionId, t.RaisedDecisionId,
                [.. byThread[t.Id].Select(c => ToDto(c, names))])),
        ];
    }

    public static async Task<CommentDto> CommentDtoAsync(AppDbContext db, CommentEntity comment, CancellationToken ct) =>
        ToDto(comment, await NamesAsync(db, [comment.Author], ct));

    /// <summary>The display name of each user ID, or the email address when the account has none.</summary>
    public static async Task<Dictionary<string, string>> NamesAsync(AppDbContext db, IEnumerable<string> userIds, CancellationToken ct)
    {
        var ids = userIds.Distinct().ToList();
        return await db.Users.AsNoTracking().Where(u => ids.Contains(u.Id))
            .Select(u => new { u.Id, Name = u.DisplayName ?? u.Email ?? "" })
            .ToDictionaryAsync(u => u.Id, u => u.Name, ct);
    }

    private static CommentDto ToDto(CommentEntity c, IReadOnlyDictionary<string, string> names) => new(
        c.Id, c.ThreadId, c.Author, names.GetValueOrDefault(c.Author), c.DeletedAt is null ? c.Body : null,
        c.Mentions, c.CreatedAt, c.EditedAt, c.DeletedAt is not null, c.DeletedAt);
}
