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

/// <param name="ContentHash">The <c>contentHash</c> of the decision as the person read it. The acceptance binds exactly that decision.</param>
/// <param name="RevisionId">The pinned state whose findings the person read. Default: the main model.</param>
/// <param name="BranchId">A draft or variant whose findings the person read. Default: the main model.</param>
public sealed record AcceptDecisionRequest(string? ContentHash, Guid? RevisionId = null, Guid? BranchId = null);

/// <param name="ContentHash">The <c>contentHash</c> of the decision as the person read it.</param>
public sealed record RejectDecisionRequest(string? Reason, string? ContentHash = null);

/// <summary>The answer to an acceptance or a rejection of a decision that is not the one that was read.</summary>
/// <param name="Type">Always <c>decision_changed</c>.</param>
/// <param name="ContentHash">The content hash of the decision as it is now.</param>
public sealed record DecisionChangedResponse(string Type, string Title, string Detail, string ContentHash);

/// <param name="Number"><c>ADR-0001</c>. Issued once in the project and never again.</param>
/// <param name="Status"><c>proposed</c>, <c>accepted</c>, <c>superseded</c> or <c>rejected</c>.</param>
/// <param name="Settles">The findings the decision answers. It settles them only while its status is <c>accepted</c>.</param>
/// <param name="Stamps">What each settled finding was when a person settled it.</param>
/// <param name="SupersededBy">The accepted decision that names this one in <c>supersedes</c>.</param>
/// <param name="ContentHash">The hash of the number, the stored status and the content. An acceptance and a rejection name it.</param>
public sealed record DecisionDto(
    Guid Id, Guid ProjectId, string Number, string Title, string Status, string Origin,
    string Context, string Decision, IReadOnlyList<Alternative> Alternatives, string? Consequences,
    IReadOnlyList<Guid> AffectedIds, IReadOnlyList<Settles> Settles, IReadOnlyList<SettlementStamp> Stamps,
    Guid? Supersedes, Guid? SupersededBy,
    string CreatedBy, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, string? AcceptedBy, DateTimeOffset? AcceptedAt,
    IReadOnlyList<DecisionHistoryEntry> History, string ContentHash)
{
    internal static DecisionDto From(DecisionBook book, PinnedDecision decision)
    {
        var (state, content) = (decision.State, decision.State.Content!);
        return new DecisionDto(
            decision.Id, state.ProjectId!.Value, DecisionState.Label(state.Number), content.Title, book.StatusOf(decision), state.Origin!,
            content.Context, content.Decision, content.Alternatives, content.Consequences,
            content.AffectedIds, content.Settles, state.Stamps, content.Supersedes, book.SupersededBy(decision.Id)?.Id,
            state.CreatedBy!, state.CreatedAt!.Value, state.UpdatedAt!.Value, state.AcceptedBy, state.AcceptedAt, state.History,
            DecisionContentHash.Of(state));
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
        Guid projectId, HttpContext http, WorkspaceAuthorizationService auth, AppDbContext db, CancellationToken ct)
    {
        await auth.RequireProjectRoleAsync(projectId, http.User.UserId(), WorkspaceRole.Viewer, ct);
        var book = new DecisionBook(await DecisionReads.CurrentAsync(db, projectId, ct));
        return Results.Ok(new Page<DecisionDto>([.. book.All.Select(d => DecisionDto.From(book, d))], null));
    }

    public static async Task<IResult> Get(
        Guid projectId, Guid decisionId, HttpContext http, WorkspaceAuthorizationService auth, AppDbContext db, CancellationToken ct)
    {
        await auth.RequireProjectRoleAsync(projectId, http.User.UserId(), WorkspaceRole.Viewer, ct);
        return await ReadAsync(db, projectId, decisionId, ct) is { } decision ? Results.Ok(decision) : ReviewAccess.NotFound("Decision");
    }

    public static async Task<IResult> Create(
        Guid projectId, HttpContext http, WorkspaceAuthorizationService auth, TimeProvider time, CancellationToken ct)
    {
        var userId = http.User.UserId();
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
        var db = requestServices.GetRequiredService<AppDbContext>();
        if (content.Supersedes is { } supersedes && !await db.Decisions.AsNoTracking().AnyAsync(d => d.Id == supersedes && d.ProjectId == projectId, ct))
            return (null, ReviewAccess.Invalid("supersedes", "No decision with this ID exists in the project"));

        var decisionId = Guid.NewGuid();
        var result = await SharedTransaction.RunAsync(requestServices, async services =>
        {
            var db = services.GetRequiredService<AppDbContext>();
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
            ? (await ReadAsync(db, projectId, decisionId, ct), null)
            : (null, result.ToHttpResult());
    }

    public static async Task<IResult> Update(
        Guid projectId, Guid decisionId, HttpContext http, WorkspaceAuthorizationService auth, IEventStore store, AppDbContext db, TimeProvider time, CancellationToken ct)
    {
        var userId = http.User.UserId();
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
    /// Acceptance needs the Owner role and a person in an interactive session. It names the
    /// content hash of the decision, so it binds exactly the decision the person read: any
    /// other text, status or list of findings is refused with 409 <c>decision_changed</c> and
    /// the hash of the decision as it is now. The stream is locked from the check to the
    /// append, so of a change and an acceptance at the same time one is first, and the
    /// acceptance that comes after the change gets the 409.
    /// <para>
    /// For each finding the decision names, the acceptance records what the finding is on the
    /// model the person read it on: the main model, or the revision or the branch the request
    /// names. That exact finding is what the decision settles.
    /// </para>
    /// </summary>
    public static async Task<IResult> Accept(
        Guid projectId, Guid decisionId, HttpContext http, WorkspaceAuthorizationService auth, IEventStore store, AppDbContext db, TimeProvider time, CancellationToken ct)
    {
        var userId = http.User.UserId();
        await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Owner, ct);
        var (request, error) = await PromiseAccess.ReadAsync<AcceptDecisionRequest>(http);
        if (request is null)
            return error!;
        if (string.IsNullOrEmpty(request.ContentHash))
            return RequireHash("accept");
        if (!await db.Decisions.AsNoTracking().AnyAsync(d => d.Id == decisionId && d.ProjectId == projectId, ct))
            return ReviewAccess.NotFound("Decision");

        var (review, refusal) = await ReviewEngine.LoadAsync(http.RequestServices, projectId, request.RevisionId, request.BranchId, ct);
        if (review is null)
            return refusal!;
        var findings = review.Report.Findings.ToDictionary(f => (f.RuleId, f.Subject.Id));
        var now = time.GetUtcNow();
        string? changed = null;
        var result = await DecideAsync(store, decisionId, state =>
        {
            changed = DecisionDecider.Changed(state, projectId, request.ContentHash);
            return DecisionDecider.Accept(state, projectId, request.ContentHash, settles => Stamp(findings, settles.RuleId, settles.SubjectId), userId, now);
        }, ct);
        return await RespondAsync(result, changed, db, projectId, decisionId, ct);
    }

    private static IResult RequireHash(string act) => ReviewAccess.Invalid(
        "contentHash", $"The content hash of the decision is required. Read the decision and send the contentHash of the decision you {act}.");

    internal static SettlementStamp Stamp(IReadOnlyDictionary<(string, Guid), Rules.Finding> findings, string ruleId, Guid subjectId) =>
        findings.TryGetValue((ruleId, subjectId), out var finding)
            ? new SettlementStamp(ruleId, subjectId, finding.RuleVersion, finding.Fingerprint)
            : new SettlementStamp(ruleId, subjectId, Rules.RuleSet.VersionOf(ruleId) ?? 0, null);

    /// <summary>
    /// Rejects a proposed decision, or withdraws an accepted one. Every finding it settled is
    /// open again. The request names the content hash of the decision the person read, as an
    /// acceptance does: a decision that changed since, or that another person accepted since,
    /// is refused with 409 <c>decision_changed</c>.
    /// </summary>
    public static async Task<IResult> Reject(
        Guid projectId, Guid decisionId, HttpContext http, WorkspaceAuthorizationService auth, IEventStore store, AppDbContext db, TimeProvider time, CancellationToken ct)
    {
        var userId = http.User.UserId();
        await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Owner, ct);
        var (request, error) = await PromiseAccess.ReadAsync<RejectDecisionRequest>(http);
        if (request is null)
            return error!;
        if (string.IsNullOrEmpty(request.ContentHash))
            return RequireHash("reject");

        var now = time.GetUtcNow();
        string? changed = null;
        var result = await DecideAsync(store, decisionId, state =>
        {
            changed = DecisionDecider.Changed(state, projectId, request.ContentHash);
            return DecisionDecider.Reject(state, projectId, request.ContentHash, request.Reason, userId, now);
        }, ct);
        return await RespondAsync(result, changed, db, projectId, decisionId, ct);
    }

    public static async Task<IResult> Delete(
        Guid projectId, Guid decisionId, HttpContext http, WorkspaceAuthorizationService auth, IEventStore store, TimeProvider time, CancellationToken ct)
    {
        var userId = http.User.UserId();
        await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Editor, ct);
        var now = time.GetUtcNow();
        var result = await DecideAsync(store, decisionId, state => DecisionDecider.Delete(state, projectId, userId, now), ct);
        return result is CommandResult<DecisionState>.Success ? Results.NoContent() : result.ToHttpResult();
    }

    /// <summary>One decision as Markdown.</summary>
    public static async Task<IResult> Export(
        Guid projectId, Guid decisionId, HttpContext http, WorkspaceAuthorizationService auth, AppDbContext db, CancellationToken ct)
    {
        await auth.RequireProjectRoleAsync(projectId, http.User.UserId(), WorkspaceRole.Viewer, ct);
        var book = new DecisionBook(await DecisionReads.CurrentAsync(db, projectId, ct));
        if (book.All.FirstOrDefault(d => d.Id == decisionId) is not { } decision)
            return ReviewAccess.NotFound("Decision");
        var text = DecisionDocuments.Markdown(book, decision, await NamesAsync(http.RequestServices, projectId, ct));
        return Results.Bytes(Encoding.UTF8.GetBytes(text), "text/markdown; charset=utf-8", $"{DecisionState.Label(decision.State.Number)}.md");
    }

    /// <summary>Every decision of the project in one Markdown document.</summary>
    public static async Task<IResult> ExportAll(
        Guid projectId, HttpContext http, WorkspaceAuthorizationService auth, AppDbContext db, CancellationToken ct)
    {
        await auth.RequireProjectRoleAsync(projectId, http.User.UserId(), WorkspaceRole.Viewer, ct);
        var book = new DecisionBook(await DecisionReads.CurrentAsync(db, projectId, ct));
        var projectName = await db.Projects.AsNoTracking().Where(p => p.Id == projectId).Select(p => p.Name).FirstAsync(ct);
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

    internal static async Task<DecisionDto?> ReadAsync(AppDbContext db, Guid projectId, Guid decisionId, CancellationToken ct)
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

    /// <param name="changed">The content hash of the decision as it is now, when the request named another.</param>
    private static async Task<IResult> RespondAsync(
        CommandResult<DecisionState> result, string? changed, AppDbContext db, Guid projectId, Guid decisionId, CancellationToken ct) =>
        result is not CommandResult<DecisionState>.Success && changed is not null
            ? Results.Conflict(new DecisionChangedResponse("decision_changed", "The decision is not the one that was read", DecisionDecider.ChangedMessage, changed))
            : await RespondAsync(result, db, projectId, decisionId, ct);

    private static async Task<IResult> RespondAsync(
        CommandResult<DecisionState> result, AppDbContext db, Guid projectId, Guid decisionId, CancellationToken ct) =>
        result is CommandResult<DecisionState>.Success
            ? Results.Ok(await ReadAsync(db, projectId, decisionId, ct))
            : result.ToHttpResult();
}
