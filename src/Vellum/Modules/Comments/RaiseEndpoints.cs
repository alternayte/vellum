using Microsoft.EntityFrameworkCore;
using Vellum.Modules.Promises;
using Vellum.Modules.Review;
using Vellum.Modules.Workspaces;
using Vellum.Shared;
using static Vellum.Shared.ResultExtensions;

namespace Vellum.Modules.Comments;

/// <param name="Title">The title of the proposed decision.</param>
/// <param name="Decision">What is proposed to be decided.</param>
public sealed record RaiseDecisionRequest(string Title, string Decision);

/// <summary>
/// Turns a thread into a Review item that cites it: an open question (Commenter or above) or
/// a proposed decision (Editor or above). The item takes the normal Review path from there; a
/// comment never becomes a fact by itself.
/// </summary>
public static class RaiseEndpoints
{
    public static async Task<IResult> AsQuestion(
        Guid projectId, Guid threadId, HttpContext http, WorkspaceAuthorizationService auth, AppDbContext db, TimeProvider time, CancellationToken ct)
    {
        var userId = http.User.UserId();
        await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Commenter, ct);
        // The thread row stays locked until the commit, so two raises of one thread take turns.
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        if (await db.CommentThreads.FromSql($"SELECT * FROM comments.threads WHERE id = {threadId} FOR UPDATE")
                .FirstOrDefaultAsync(t => t.ProjectId == projectId, ct) is not { } thread)
            return NotFound("Thread");
        if (thread.RaisedQuestionId is { } raised && await db.Questions.AsNoTracking().FirstOrDefaultAsync(q => q.Id == raised && q.ProjectId == projectId, ct) is { } existing)
            return Results.Ok(Questions.ToDto(existing));
        if (await OpeningTextAsync(db, thread, ct) is not { } opening)
            return Conflict("The first comment of the thread is deleted, so the thread has no text to raise");

        var citation = Citation(thread);
        var room = TextRule.MaxDescription - citation.Length - 2;
        var text = $"{(opening.Length > room ? opening[..room] : opening)}\n\n{citation}";
        // The question and the link from the thread commit together.
        var (row, refusal) = await Questions.AddAsync(db, projectId, text, About(thread), PromiseOrigin.Human, userId, time.GetUtcNow(), ct);
        if (row is null)
            return refusal!;
        thread.RaisedQuestionId = row.Id;
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return Results.Created($"/api/projects/{projectId}/questions/{row.Id}", Questions.ToDto(row));
    }

    public static async Task<IResult> AsDecision(
        Guid projectId, Guid threadId, RaiseDecisionRequest request, HttpContext http,
        WorkspaceAuthorizationService auth, AppDbContext db, TimeProvider time, CancellationToken ct)
    {
        var userId = http.User.UserId();
        await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Editor, ct);
        if (await db.CommentThreads.AsNoTracking().FirstOrDefaultAsync(t => t.Id == threadId && t.ProjectId == projectId, ct) is not { } thread)
            return NotFound("Thread");
        if (thread.RaisedDecisionId is { } raised && await db.Decisions.AsNoTracking().AnyAsync(d => d.Id == raised && d.ProjectId == projectId, ct))
            return Conflict("The thread is raised as a decision already");
        if (await OpeningTextAsync(db, thread, ct) is not { } opening)
            return Conflict("The first comment of the thread is deleted, so the thread has no text to raise");

        var citation = Citation(thread);
        var room = TextRule.MaxDescription - citation.Length - 2;
        var context = $"{(opening.Length > room ? opening[..room] : opening)}\n\n{citation}";
        var content = new DecisionContent(request.Title ?? "", context, request.Decision ?? "", [], null, About(thread), [], null);
        if (thread.RaisedDecisionId == Guid.Empty)
            return Conflict("The thread is being raised as a decision already");
        // Claim the thread first: of two raises, one sets the link and the other is refused
        // before it writes a decision. A decision that fails to write gives the claim back.
        var claimed = await db.CommentThreads
            .Where(t => t.Id == threadId && (t.RaisedDecisionId == null || t.RaisedDecisionId == thread.RaisedDecisionId))
            .ExecuteUpdateAsync(set => set.SetProperty(t => t.RaisedDecisionId, Guid.Empty), ct);
        if (claimed == 0)
            return Conflict("The thread is being raised as a decision already");
        var (dto, refusal) = await DecisionEndpoints.CreateAsync(
            http.RequestServices, projectId, content, PromiseOrigin.Human, userId, time.GetUtcNow(), ct);
        await db.CommentThreads.Where(t => t.Id == threadId)
            .ExecuteUpdateAsync(set => set.SetProperty(t => t.RaisedDecisionId, dto == null ? thread.RaisedDecisionId : dto.Id), ct);
        if (dto is null)
            return refusal!;
        return Results.Created($"/api/projects/{projectId}/decisions/{dto.Id}", dto);
    }

    private static async Task<string?> OpeningTextAsync(AppDbContext db, ThreadEntity thread, CancellationToken ct)
    {
        var first = await db.Comments.AsNoTracking().Where(c => c.ThreadId == thread.Id)
            .OrderBy(c => c.CreatedAt).ThenBy(c => c.Id).FirstOrDefaultAsync(ct);
        return first is { DeletedAt: null } ? first.Body : null;
    }

    private static string Citation(ThreadEntity thread)
    {
        var name = thread.TargetName.Length > TextRule.MaxName ? thread.TargetName[..TextRule.MaxName] : thread.TargetName;
        return $"Raised from comment thread {thread.Id} on {EnumText.Of(thread.TargetKind)} \"{name}\".";
    }

    /// <summary>The target, unless it is the project itself.</summary>
    private static IReadOnlyList<Guid> About(ThreadEntity thread) =>
        thread.TargetKind == TargetKind.Project ? [] : [thread.TargetId];
}
