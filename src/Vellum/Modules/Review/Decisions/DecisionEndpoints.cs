using System.Text;
using Deedbox;
using Microsoft.EntityFrameworkCore;
using Vellum.Modules.Promises;
using Vellum.Modules.Trace.Scenarios;
using Vellum.Modules.Workspaces;
using Vellum.Modules.Workspaces.Authorization;
using Vellum.Shared;

namespace Vellum.Modules.Review.Decisions;

/// <param name="Origin">On create only: <c>human</c> (the default) or <c>ai-suggested</c>. An agent token always gives <c>agent-suggested</c>.</param>
public sealed record DecisionRequest(
    string Title, string? Context, string Decision,
    IReadOnlyList<Alternative>? Alternatives, string? Consequences,
    IReadOnlyList<Guid>? AffectedIds, IReadOnlyList<Settles>? Settles, Guid? Supersedes,
    string? Origin = null)
{
    public DecisionContent ToContent() => new(Title, Context ?? "", Decision, Alternatives ?? [], Consequences, AffectedIds ?? [], Settles ?? [], Supersedes);
}

public sealed record RejectDecisionRequest(string? Reason);

/// <param name="Number"><c>ADR-0001</c>. Issued once in the project and never again.</param>
/// <param name="Status"><c>proposed</c>, <c>accepted</c>, <c>superseded</c> or <c>rejected</c>.</param>
/// <param name="Settles">The findings the decision answers. It settles them only while its status is <c>accepted</c>.</param>
/// <param name="Stamps">What each settled finding was when a person settled it.</param>
/// <param name="SupersededBy">The accepted decision that names this one in <c>supersedes</c>.</param>
public sealed record DecisionDto(
    Guid Id, Guid ProjectId, string Number, string Title, string Status, string Origin,
    string Context, string Decision, IReadOnlyList<Alternative> Alternatives, string? Consequences,
    IReadOnlyList<Guid> AffectedIds, IReadOnlyList<Settles> Settles, IReadOnlyList<SettlementStamp> Stamps,
    Guid? Supersedes, Guid? SupersededBy,
    string CreatedBy, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, string? AcceptedBy, DateTimeOffset? AcceptedAt,
    IReadOnlyList<DecisionHistoryEntry> History)
{
    internal static DecisionDto From(DecisionBook book, PinnedDecision decision)
    {
        var (state, content) = (decision.State, decision.State.Content!);
        return new DecisionDto(
            decision.Id, state.ProjectId!.Value, DecisionState.Label(state.Number), content.Title, book.StatusOf(decision), state.Origin!,
            content.Context, content.Decision, content.Alternatives, content.Consequences,
            content.AffectedIds, content.Settles, state.Stamps, content.Supersedes, book.SupersededBy(decision.Id)?.Id,
            state.CreatedBy!, state.CreatedAt!.Value, state.UpdatedAt!.Value, state.AcceptedBy, state.AcceptedAt, state.History);
    }
}

/// <summary>
/// Decisions (ADRs) of a project. Anyone with the Editor role, and an agent token with the
/// <c>propose</c> scope, writes a proposed decision. Only a person with the Owner role
/// accepts or rejects one; an accepted decision is the only thing that settles a blocking
/// finding (SDD guarantees 8 and 12).
/// </summary>
public static class DecisionEndpoints
{
    public static async Task<IResult> List(
        Guid projectId, HttpContext http, WorkspaceAuthorizationService auth, ReviewDbContext db, CancellationToken ct)
    {
        await auth.RequireProjectRoleAsync(projectId, ReviewAccess.UserId(http.User), WorkspaceRole.Viewer, ct);
        var book = new DecisionBook(await DecisionReads.CurrentAsync(db, projectId, ct));
        return Results.Ok(new Page<DecisionDto>([.. book.All.Select(d => DecisionDto.From(book, d))], null));
    }

    public static async Task<IResult> Get(
        Guid projectId, Guid decisionId, HttpContext http, WorkspaceAuthorizationService auth, ReviewDbContext db, CancellationToken ct)
    {
        await auth.RequireProjectRoleAsync(projectId, ReviewAccess.UserId(http.User), WorkspaceRole.Viewer, ct);
        return await ReadAsync(db, projectId, decisionId, ct) is { } decision ? Results.Ok(decision) : ReviewAccess.NotFound("Decision");
    }

    public static async Task<IResult> Create(
        Guid projectId, HttpContext http, WorkspaceAuthorizationService auth, TimeProvider time, CancellationToken ct)
    {
        var userId = ReviewAccess.UserId(http.User);
        await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Editor, ct);
        var (request, error) = await PromiseAccess.ReadAsync<DecisionRequest>(http);
        if (request is null)
            return error!;
        // The origin comes from who calls, not from what the body says.
        if (PromiseAccess.Origin(http.User, request.Origin) is not { } origin)
            return ReviewAccess.Invalid("origin", "The origin of a decision a person creates is human or ai-suggested");

        var created = await CreateAsync(http.RequestServices, projectId, request.ToContent(), origin, userId, time.GetUtcNow(), ct);
        return created.Dto is { } dto ? Results.Created($"/api/projects/{projectId}/decisions/{dto.Id}", dto) : created.Refusal!;
    }

    /// <summary>
    /// Writes a proposed decision. The number and the decision commit together: the statement
    /// that takes the number locks the project's numbering row until the commit, so creates of
    /// one project take turns, and a refused create gives its number back because it was never issued.
    /// </summary>
    internal static async Task<(DecisionDto? Dto, IResult? Refusal)> CreateAsync(
        IServiceProvider requestServices, Guid projectId, DecisionContent content, string origin, string userId, DateTimeOffset now, CancellationToken ct)
    {
        var reviewDb = requestServices.GetRequiredService<ReviewDbContext>();
        if (content.Supersedes is { } supersedes && !await reviewDb.Decisions.AsNoTracking().AnyAsync(d => d.Id == supersedes && d.ProjectId == projectId, ct))
            return (null, ReviewAccess.Invalid("supersedes", "No decision with this ID exists in the project"));

        var decisionId = Guid.NewGuid();
        var result = await SharedTransaction.RunAsync(requestServices, async services =>
        {
            var db = services.GetRequiredService<ReviewDbContext>();
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var number = (await db.Database.SqlQuery<int>($"""
                INSERT INTO review.decision_numbering (project_id, next_number) VALUES ({projectId}, 2)
                ON CONFLICT (project_id) DO UPDATE SET next_number = review.decision_numbering.next_number + 1
                RETURNING next_number - 1 AS "Value"
                """).ToListAsync(ct)).Single();
            if (await db.Decisions.CountAsync(d => d.ProjectId == projectId, ct) >= DecisionRules.MaxPerProject)
                return new CommandResult<DecisionState>.Conflict($"A project holds at most {DecisionRules.MaxPerProject} decisions.");

            var proposed = await services.GetRequiredService<IEventStore>().UseDbContext(db)
                .DecideInTransaction<DecisionState, DecisionState>(
                    decisionId, state => DecisionDecider.Propose(state, projectId, number, origin, content, userId, now), state => state, ct);
            if (proposed is CommandResult<DecisionState>.Success)
                await transaction.CommitAsync(ct);
            return proposed;
        }, ct);

        return result is CommandResult<DecisionState>.Success
            ? (await ReadAsync(reviewDb, projectId, decisionId, ct), null)
            : (null, result.ToHttpResult());
    }

    public static async Task<IResult> Update(
        Guid projectId, Guid decisionId, HttpContext http, WorkspaceAuthorizationService auth, IEventStore store, ReviewDbContext db, TimeProvider time, CancellationToken ct)
    {
        var userId = ReviewAccess.UserId(http.User);
        await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Editor, ct);
        var (request, error) = await PromiseAccess.ReadAsync<DecisionRequest>(http);
        if (request is null)
            return error!;
        if (request.Supersedes is { } supersedes
            && (supersedes == decisionId || !await db.Decisions.AsNoTracking().AnyAsync(d => d.Id == supersedes && d.ProjectId == projectId, ct)))
            return ReviewAccess.Invalid("supersedes", "No other decision with this ID exists in the project");

        var now = time.GetUtcNow();
        return await RespondAsync(
            await DecideAsync(store, decisionId, state => DecisionDecider.Change(state, projectId, request.ToContent(), userId, now), ct), db, projectId, decisionId, ct);
    }

    /// <summary>
    /// Acceptance needs the Owner role and a person in an interactive session. For each
    /// finding the decision names, the acceptance records what the finding is on the main
    /// model now: that exact finding is what the decision settles.
    /// </summary>
    public static async Task<IResult> Accept(
        Guid projectId, Guid decisionId, HttpContext http, WorkspaceAuthorizationService auth, IEventStore store, ReviewDbContext db, TimeProvider time, CancellationToken ct)
    {
        var userId = ReviewAccess.UserId(http.User);
        await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Owner, ct);
        if (!await db.Decisions.AsNoTracking().AnyAsync(d => d.Id == decisionId && d.ProjectId == projectId, ct))
            return ReviewAccess.NotFound("Decision");

        var (review, refusal) = await ReviewEngine.LoadAsync(http.RequestServices, projectId, null, null, ct);
        if (review is null)
            return refusal!;
        var findings = review.Report.Findings.ToDictionary(f => (f.RuleId, f.Subject.Id));
        var now = time.GetUtcNow();
        return await RespondAsync(
            await DecideAsync(store, decisionId, state => DecisionDecider.Accept(state, projectId, settles => Stamp(findings, settles.RuleId, settles.SubjectId), userId, now), ct),
            db, projectId, decisionId, ct);
    }

    internal static SettlementStamp Stamp(IReadOnlyDictionary<(string, Guid), Rules.Finding> findings, string ruleId, Guid subjectId) =>
        findings.TryGetValue((ruleId, subjectId), out var finding)
            ? new SettlementStamp(ruleId, subjectId, finding.RuleVersion, finding.Fingerprint)
            : new SettlementStamp(ruleId, subjectId, Rules.RuleSet.VersionOf(ruleId) ?? 0, null);

    /// <summary>Rejects a proposed decision, or withdraws an accepted one. Every finding it settled is open again.</summary>
    public static async Task<IResult> Reject(
        Guid projectId, Guid decisionId, HttpContext http, WorkspaceAuthorizationService auth, IEventStore store, ReviewDbContext db, TimeProvider time, CancellationToken ct)
    {
        var userId = ReviewAccess.UserId(http.User);
        await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Owner, ct);
        var (request, error) = await PromiseAccess.ReadAsync<RejectDecisionRequest>(http, allowEmpty: true);
        if (error is not null)
            return error;

        var now = time.GetUtcNow();
        return await RespondAsync(
            await DecideAsync(store, decisionId, state => DecisionDecider.Reject(state, projectId, request?.Reason, userId, now), ct), db, projectId, decisionId, ct);
    }

    public static async Task<IResult> Delete(
        Guid projectId, Guid decisionId, HttpContext http, WorkspaceAuthorizationService auth, IEventStore store, TimeProvider time, CancellationToken ct)
    {
        var userId = ReviewAccess.UserId(http.User);
        await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Editor, ct);
        var now = time.GetUtcNow();
        var result = await DecideAsync(store, decisionId, state => DecisionDecider.Delete(state, projectId, userId, now), ct);
        return result is CommandResult<DecisionState>.Success ? Results.NoContent() : result.ToHttpResult();
    }

    /// <summary>One decision as Markdown.</summary>
    public static async Task<IResult> Export(
        Guid projectId, Guid decisionId, HttpContext http, WorkspaceAuthorizationService auth, ReviewDbContext db, CancellationToken ct)
    {
        await auth.RequireProjectRoleAsync(projectId, ReviewAccess.UserId(http.User), WorkspaceRole.Viewer, ct);
        var book = new DecisionBook(await DecisionReads.CurrentAsync(db, projectId, ct));
        if (book.All.FirstOrDefault(d => d.Id == decisionId) is not { } decision)
            return ReviewAccess.NotFound("Decision");
        var text = DecisionDocuments.Markdown(book, decision, await NamesAsync(http.RequestServices, projectId, ct));
        return Results.Bytes(Encoding.UTF8.GetBytes(text), "text/markdown; charset=utf-8", $"{DecisionState.Label(decision.State.Number)}.md");
    }

    /// <summary>Every decision of the project in one Markdown document.</summary>
    public static async Task<IResult> ExportAll(
        Guid projectId, HttpContext http, WorkspaceAuthorizationService auth, ReviewDbContext db, WorkspacesDbContext workspaces, CancellationToken ct)
    {
        await auth.RequireProjectRoleAsync(projectId, ReviewAccess.UserId(http.User), WorkspaceRole.Viewer, ct);
        var book = new DecisionBook(await DecisionReads.CurrentAsync(db, projectId, ct));
        var projectName = await workspaces.Projects.AsNoTracking().Where(p => p.Id == projectId).Select(p => p.Name).FirstAsync(ct);
        var text = DecisionDocuments.MarkdownAll(book, projectName, await NamesAsync(http.RequestServices, projectId, ct));
        return Results.Bytes(Encoding.UTF8.GetBytes(text), "text/markdown; charset=utf-8", "decisions.md");
    }

    /// <summary>The names of the things of the main model as it is now, for the lists of a document.</summary>
    private static async Task<Func<Guid, string>> NamesAsync(IServiceProvider services, Guid projectId, CancellationToken ct)
    {
        var (pinned, _) = await PinnedModel.ResolveAsync(services, projectId, null, null, ct);
        var labels = TraceModelBuilder.Labels(pinned!.State);
        return id => labels.GetValueOrDefault(id, id.ToString());
    }

    internal static async Task<DecisionDto?> ReadAsync(ReviewDbContext db, Guid projectId, Guid decisionId, CancellationToken ct)
    {
        var book = new DecisionBook(await DecisionReads.CurrentAsync(db, projectId, ct));
        return book.All.FirstOrDefault(d => d.Id == decisionId) is { } decision ? DecisionDto.From(book, decision) : null;
    }

    /// <summary>
    /// Runs a decision on a decision stream. Every stream type shares one space of IDs, so an
    /// ID that names a model, a schema or a promise is not a decision: the write finds nothing.
    /// </summary>
    internal static async Task<CommandResult<DecisionState>> DecideAsync(
        IEventStore store, Guid decisionId, Func<DecisionState, CommandResult<IReadOnlyList<object>>> decide, CancellationToken ct)
    {
        try
        {
            return await store.Decide<DecisionState, DecisionState>(decisionId, decide, state => state, ct);
        }
        // The stream of a decision of a deleted project is deleted with it.
        catch (DeedboxException e) when (e.Code is DeedboxError.StreamTypeMismatch or DeedboxError.StreamDeleted)
        {
            return new CommandResult<DecisionState>.NotFound("Decision not found");
        }
    }

    private static async Task<IResult> RespondAsync(
        CommandResult<DecisionState> result, ReviewDbContext db, Guid projectId, Guid decisionId, CancellationToken ct) =>
        result is CommandResult<DecisionState>.Success
            ? Results.Ok(await ReadAsync(db, projectId, decisionId, ct))
            : result.ToHttpResult();
}
