using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Vellum.Modules.Ai.Design;
using Vellum.Modules.Promises;
using Vellum.Modules.Workspaces.Authorization;
using Vellum.Shared;

namespace Vellum.Modules.Review;

public static class QuestionStatus
{
    public const string Open = "open";
    public const string Answered = "answered";
    public const string Dropped = "dropped";
}

/// <param name="About">The IDs of the things the question is about.</param>
/// <param name="Origin">On create only: <c>human</c> (the default) or <c>ai-suggested</c>. An agent token always gives <c>agent-suggested</c>.</param>
public sealed record QuestionRequest(string Text, IReadOnlyList<Guid>? About = null, string? Origin = null);

/// <summary>Keeps one open question of an AI flow proposal or architecture proposal as an open question of the project.</summary>
/// <param name="Index">The place of the question in <c>openQuestions</c> of the proposal, from 0.</param>
public sealed record KeepQuestionRequest(Guid ProposalId, int Index);

public sealed record AnswerQuestionRequest(string Answer);

/// <param name="Status"><c>open</c>, <c>answered</c> or <c>dropped</c>.</param>
public sealed record QuestionDto(
    Guid Id, Guid ProjectId, string Text, IReadOnlyList<Guid> About, string Origin, string Status,
    string? Answer, string? AnsweredBy, DateTimeOffset? AnsweredAt, string CreatedBy, DateTimeOffset CreatedAt);

/// <summary>
/// The open questions of a project: what the design leaves undecided. A person, an agent
/// token with the <c>propose</c> scope or a kept AI suggestion opens one. Only a person
/// answers or drops one, and only while it is open: an answer is not replaced.
/// </summary>
public static class Questions
{
    public const int MaxPerProject = 500;
    public const int MaxAbout = 50;

    public static async Task<IResult> List(
        Guid projectId, HttpContext http, WorkspaceAuthorizationService auth, ReviewDbContext db, CancellationToken ct)
    {
        await auth.RequireProjectRoleAsync(projectId, ReviewAccess.UserId(http.User), WorkspaceRole.Viewer, ct);
        var rows = await db.Questions.AsNoTracking().Where(q => q.ProjectId == projectId)
            .OrderBy(q => q.CreatedAt).ThenBy(q => q.Id).ToListAsync(ct);
        return Results.Ok(new Page<QuestionDto>([.. rows.Select(ToDto)], null));
    }

    public static async Task<IResult> Create(
        Guid projectId, HttpContext http, WorkspaceAuthorizationService auth, ReviewDbContext db, TimeProvider time, CancellationToken ct)
    {
        var userId = ReviewAccess.UserId(http.User);
        await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Editor, ct);
        var (request, error) = await PromiseAccess.ReadAsync<QuestionRequest>(http);
        if (request is null)
            return error!;
        // The origin comes from who calls, not from what the body says.
        if (PromiseAccess.Origin(http.User, request.Origin) is not { } origin)
            return ReviewAccess.Invalid("origin", "The origin of a question a person creates is human or ai-suggested");

        var (row, refusal) = await AddAsync(db, projectId, request.Text, request.About ?? [], origin, userId, time.GetUtcNow(), ct);
        return row is null ? refusal! : Results.Created($"/api/projects/{projectId}/questions/{row.Id}", ToDto(row));
    }

    /// <summary>
    /// One action of a person on a stored AI proposal: its open question becomes an open
    /// question of the project, with the origin <c>ai-suggested</c>. The text is read from the
    /// stored proposal, not from the request.
    /// </summary>
    public static async Task<IResult> Keep(
        Guid projectId, HttpContext http, WorkspaceAuthorizationService auth, ReviewDbContext db, AiDesignDbContext proposals, TimeProvider time, CancellationToken ct)
    {
        var userId = ReviewAccess.UserId(http.User);
        await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Editor, ct);
        var (request, error) = await PromiseAccess.ReadAsync<KeepQuestionRequest>(http);
        if (request is null)
            return error!;

        var document = await proposals.Proposals.AsNoTracking()
            .Where(p => p.Id == request.ProposalId && p.ProjectId == projectId).Select(p => p.Document).FirstOrDefaultAsync(ct);
        if (document is null)
            return ReviewAccess.NotFound("Proposal");
        // An architecture proposal holds its questions at the top of its document, a flow proposal in its output.
        using var parsed = JsonDocument.Parse(document, new JsonDocumentOptions { MaxDepth = 256 });
        var holder = parsed.RootElement.TryGetProperty("output", out var output) && output.ValueKind == JsonValueKind.Object ? output : parsed.RootElement;
        if (!holder.TryGetProperty("openQuestions", out var questions) || questions.ValueKind != JsonValueKind.Array
            || request.Index < 0 || request.Index >= questions.GetArrayLength()
            || questions[request.Index] is not { ValueKind: JsonValueKind.String } kept || kept.GetString() is not { Length: > 0 } text)
            return ReviewAccess.NotFound("Open question of the proposal");

        // Keeping the same question twice gives the question that is there.
        if (await db.Questions.AsNoTracking().FirstOrDefaultAsync(q => q.ProjectId == projectId && q.Text == text && q.Status == QuestionStatus.Open, ct) is { } existing)
            return Results.Ok(ToDto(existing));
        var (row, refusal) = await AddAsync(db, projectId, text, [], PromiseOrigin.AiSuggested, userId, time.GetUtcNow(), ct);
        return row is null ? refusal! : Results.Created($"/api/projects/{projectId}/questions/{row.Id}", ToDto(row));
    }

    internal static async Task<(QuestionEntity? Row, IResult? Refusal)> AddAsync(
        ReviewDbContext db, Guid projectId, string? text, IReadOnlyList<Guid> about, string origin, string userId, DateTimeOffset now, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text))
            return (null, ReviewAccess.Invalid("text", "A question needs text"));
        if (TextRule.Lines("text", text) is { } invalid)
            return (null, ReviewAccess.Invalid(invalid.Field, invalid.Message));
        if (about.Count > MaxAbout)
            return (null, ReviewAccess.Invalid("about", $"A question names at most {MaxAbout} things"));
        if (await db.Questions.CountAsync(q => q.ProjectId == projectId, ct) >= MaxPerProject)
            return (null, ReviewAccess.Conflict($"A project holds at most {MaxPerProject} questions. Delete one first."));

        var row = new QuestionEntity
        {
            Id = Guid.NewGuid(), ProjectId = projectId, Text = text.Trim(), About = PromiseJson.Write(about.Distinct().ToList()),
            Origin = origin, Status = QuestionStatus.Open, CreatedBy = userId, CreatedAt = now,
        };
        db.Questions.Add(row);
        await db.SaveChangesAsync(ct);
        return (row, null);
    }

    public static async Task<IResult> Answer(
        Guid projectId, Guid questionId, HttpContext http, WorkspaceAuthorizationService auth, ReviewDbContext db, TimeProvider time, CancellationToken ct)
    {
        var userId = ReviewAccess.UserId(http.User);
        await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Editor, ct);
        var (request, error) = await PromiseAccess.ReadAsync<AnswerQuestionRequest>(http);
        if (request is null)
            return error!;
        if (string.IsNullOrWhiteSpace(request.Answer))
            return ReviewAccess.Invalid("answer", "An answer needs text");
        if (TextRule.Lines("answer", request.Answer) is { } invalid)
            return ReviewAccess.Invalid(invalid.Field, invalid.Message);

        return await CloseAsync(db, projectId, questionId, QuestionStatus.Answered, request.Answer.Trim(), userId, time.GetUtcNow(), ct);
    }

    public static async Task<IResult> Drop(
        Guid projectId, Guid questionId, HttpContext http, WorkspaceAuthorizationService auth, ReviewDbContext db, TimeProvider time, CancellationToken ct)
    {
        var userId = ReviewAccess.UserId(http.User);
        await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Editor, ct);
        return await CloseAsync(db, projectId, questionId, QuestionStatus.Dropped, null, userId, time.GetUtcNow(), ct);
    }

    /// <summary>
    /// Answers or drops an open question. The status is checked in the statement that writes
    /// it, so of two people who close one question at the same time one is first, and the
    /// other gets a 409 and not the first answer overwritten.
    /// </summary>
    private static async Task<IResult> CloseAsync(
        ReviewDbContext db, Guid projectId, Guid questionId, string status, string? answer, string userId, DateTimeOffset now, CancellationToken ct)
    {
        var closed = await db.Questions
            .Where(q => q.Id == questionId && q.ProjectId == projectId && q.Status == QuestionStatus.Open)
            .ExecuteUpdateAsync(set => set
                .SetProperty(q => q.Status, status).SetProperty(q => q.Answer, answer)
                .SetProperty(q => q.AnsweredBy, userId).SetProperty(q => q.AnsweredAt, now), ct);
        if (await db.Questions.AsNoTracking().FirstOrDefaultAsync(q => q.Id == questionId && q.ProjectId == projectId, ct) is not { } row)
            return ReviewAccess.NotFound("Question");
        return closed == 1
            ? Results.Ok(ToDto(row))
            : ReviewAccess.Conflict($"The question is {row.Status}. Only an open question is answered or dropped. Read the questions again.");
    }

    public static async Task<IResult> Delete(
        Guid projectId, Guid questionId, HttpContext http, WorkspaceAuthorizationService auth, ReviewDbContext db, CancellationToken ct)
    {
        await auth.RequireProjectRoleAsync(projectId, ReviewAccess.UserId(http.User), WorkspaceRole.Editor, ct);
        return await db.Questions.Where(q => q.Id == questionId && q.ProjectId == projectId).ExecuteDeleteAsync(ct) == 1
            ? Results.NoContent()
            : ReviewAccess.NotFound("Question");
    }

    internal static QuestionDto ToDto(QuestionEntity row) => new(
        row.Id, row.ProjectId, row.Text, PromiseJson.Read<List<Guid>>(row.About), row.Origin, row.Status,
        row.Answer, row.AnsweredBy, row.AnsweredAt, row.CreatedBy, row.CreatedAt);
}
