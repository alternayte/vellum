using System.Security.Claims;
using System.Text.Json;
using Deedbox;
using Microsoft.EntityFrameworkCore;
using Vellum.Modules.Modelling;
using Vellum.Modules.Schemas;
using Vellum.Modules.Workspaces;
using Vellum.Shared;
using static Vellum.Shared.ResultExtensions;
using Vellum.Modules.Costs;

namespace Vellum.Modules.Drafts;

public static class DraftEndpoints
{
    public static WebApplication MapDraftEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/projects/{projectId}/drafts")
            .RequireAuthorization()
            .WithTags("Drafts");

        group.MapPost("/", CreateDraft.Handle);
        group.MapGet("/", ListDrafts.Handle);
        group.MapGet("/{draftId}", GetDraft.Handle);
        group.MapPost("/{draftId}/abandon", AbandonDraft.Handle);

        return app;
    }
}

public sealed record DraftDto(
    Guid Id, Guid ProjectId, string Name, Guid StreamId,
    Guid BaseStreamId, int ForkVersion, string Status,
    string CreatedBy, DateTimeOffset CreatedAt,
    DateTimeOffset? MergedAt, DateTimeOffset? AbandonedAt);

public sealed record CreateDraftRequest(Guid Id, string Name);

public static class CreateDraft
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static async Task<IResult> Handle(
        Guid projectId,
        CreateDraftRequest request,
        ClaimsPrincipal user,
        HttpContext http,
        WorkspaceAuthorizationService auth,
        CancellationToken ct)
    {
        var userId = user.UserId();
        await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Editor, ct);

        if (request.Id == Guid.Empty)
            return Invalid("id", "An ID is required");
        if (string.IsNullOrWhiteSpace(request.Name))
            return Invalid("name", "Name is required");
        if (TextRule.Line("name", request.Name) is { } textError)
            return Invalid(textError.Field, textError.Message);

        // The draft row, the fork event and the branch read-model rows commit together.
        return await SharedTransaction.RunAsync(http.RequestServices, async services =>
        {
            var db = services.GetRequiredService<AppDbContext>();
            var store = services.GetRequiredService<IEventStore>();

            var existing = await db.Drafts.FindAsync([request.Id], ct);
            // The ID makes a repeated create idempotent inside one project. It never reads a
            // draft of another project.
            if (existing is not null)
            {
                return existing.ProjectId == projectId
                    ? Results.Ok(ToDto(existing))
                    : Results.Conflict(new ErrorResponse("conflict", "A draft with this ID already exists."));
            }

            var proj = await db.Projects.AsNoTracking()
                .FirstAsync(p => p.Id == projectId, ct);

            // The stream lock gives the newest model, in which every schema reference points at
            // a schema that exists. Holding those schema rows until the commit keeps a delete
            // from running between this read and the fork (see SchemaReferences).
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var branches = store.ForProject(projectId).UseDbContext(db);
            var main = await branches.Execute<ModelState>(StreamId.From(proj.StreamId), _ => [], ct);
            var (mainState, mainVersion) = (main.State, main.Version);
            await SchemaReferences.HoldAsync(
                db, projectId, ContractDecider.ReferencedSchemaIds(mainState), ct);

            var draft = new DraftEntity
            {
                Id = request.Id,
                ProjectId = projectId,
                Name = request.Name,
                StreamId = Guid.NewGuid(),
                BaseStreamId = proj.StreamId,
                ForkVersion = checked((int)mainVersion),
                CreatedBy = userId,
                BaseSnapshot = JsonSerializer.Serialize(mainState, JsonOpts),
            };
            db.Drafts.Add(draft);

            // One append. Deedbox saves the draft row in the transaction. The inline projection
            // writes the branch rows in it, so the draft reads as a full model, not just the
            // changes made on it.
            await branches.Append(
                StreamId.From(draft.StreamId), ExpectedVersion.NoStream,
                [ModelStore.Fork(mainState, proj.StreamId, mainVersion)], ct);
            await transaction.CommitAsync(ct);

            return Results.Created(
                $"/api/projects/{projectId}/drafts/{draft.Id}",
                ToDto(draft));
        }, ct);
    }

    internal static DraftDto ToDto(DraftEntity d) =>
        new(d.Id, d.ProjectId, d.Name, d.StreamId, d.BaseStreamId,
            d.ForkVersion, EnumText.Of(d.Status), d.CreatedBy, d.CreatedAt,
            d.MergedAt, d.AbandonedAt);

}

public static class ListDrafts
{
    public static async Task<IResult> Handle(
        Guid projectId,
        string? status, string? cursor, int? limit,
        ClaimsPrincipal user,
        AppDbContext db,
        WorkspaceAuthorizationService auth,
        CancellationToken ct)
    {
        var userId = user.UserId();
        await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Viewer, ct);

        var pageSize = Math.Clamp(limit ?? 50, 1, 200);

        var query = db.Drafts.AsNoTracking()
            .Where(d => d.ProjectId == projectId);

        // A status that names no member matches no draft.
        if (!string.IsNullOrEmpty(status))
        {
            query = EnumText.TryParse(status, out DraftStatus wanted)
                ? query.Where(d => d.Status == wanted)
                : query.Where(d => false);
        }

        query = query.OrderByDescending(d => d.CreatedAt).ThenBy(d => d.Id);

        var decoded = CursorEncoder.Decode(cursor);
        if (decoded is not null)
        {
            var (sortKey, afterId) = decoded.Value;
            var afterDate = DateTimeOffset.Parse(sortKey);
            query = query.Where(d => d.CreatedAt < afterDate
                || (d.CreatedAt == afterDate && d.Id.CompareTo(afterId) > 0));
        }

        var items = await query
            .Take(pageSize + 1)
            .Select(d => new DraftDto(d.Id, d.ProjectId, d.Name, d.StreamId, d.BaseStreamId,
                d.ForkVersion, EnumText.Of(d.Status), d.CreatedBy, d.CreatedAt, d.MergedAt, d.AbandonedAt))
            .ToListAsync(ct);

        string? nextCursor = null;
        if (items.Count > pageSize)
        {
            items.RemoveAt(items.Count - 1);
            var last = items[^1];
            nextCursor = CursorEncoder.Encode(last.CreatedAt.ToString("O"), last.Id);
        }

        return Results.Ok(new Page<DraftDto>(items, nextCursor));
    }
}

public static class GetDraft
{
    public static async Task<IResult> Handle(
        Guid projectId, Guid draftId,
        ClaimsPrincipal user,
        AppDbContext db,
        WorkspaceAuthorizationService auth,
        CancellationToken ct)
    {
        var userId = user.UserId();
        await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Viewer, ct);

        var draft = await db.Drafts.AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == draftId && d.ProjectId == projectId, ct);
        if (draft is null)
            return Results.NotFound(new ErrorResponse("not_found", "Draft not found"));

        return Results.Ok(CreateDraft.ToDto(draft));
    }
}

public static class AbandonDraft
{
    public static async Task<IResult> Handle(
        Guid projectId, Guid draftId,
        ClaimsPrincipal user,
        HttpContext http,
        WorkspaceAuthorizationService auth,
        TimeProvider time,
        CancellationToken ct)
    {
        var userId = user.UserId();
        await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Editor, ct);

        // The draft status, the closing event, the removal of the draft's read-model rows and
        // the removal of its cost sheet and saved estimates commit together.
        return await SharedTransaction.RunAsync(http.RequestServices, async services =>
        {
            var db = services.GetRequiredService<AppDbContext>();

            var found = await db.Drafts.AsNoTracking()
                .Where(d => d.Id == draftId && d.ProjectId == projectId)
                .Select(d => new { d.StreamId, d.Status })
                .FirstOrDefaultAsync(ct);
            if (found is null)
                return Results.NotFound(new ErrorResponse("not_found", "Draft not found"));
            // The status is read again under the lock.
            if (found.Status != DraftStatus.Open)
                return Results.Conflict(new ErrorResponse("conflict", $"Draft is already {EnumText.Of(found.Status)}"));

            // A return before the commit rolls everything back.
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var store = services.GetRequiredService<IEventStore>().ForProject(projectId).UseDbContext(db);

            // The draft's model stream and then its cost sheet stay locked until the commit, so
            // a merge of this draft and this abandon take turns, and a write of the sheet that
            // waits for the lock finds the draft closed. The status is read under that lock.
            await store.Execute<ModelState>(StreamId.From(found.StreamId), _ => [], ct);
            await CostBranches.LockSheetsAsync(db, [found.StreamId], ct);

            var draft = await db.Drafts.FirstAsync(d => d.Id == draftId, ct);
            if (draft.Status != DraftStatus.Open)
                return Results.Conflict(new ErrorResponse("conflict", $"Draft is already {EnumText.Of(draft.Status)}"));

            draft.Status = DraftStatus.Abandoned;
            draft.AbandonedAt = time.GetUtcNow();

            // One append. Deedbox saves the status in the transaction, and the inline projection
            // drops the draft's rows.
            await store.Append(
                StreamId.From(draft.StreamId), ExpectedVersion.Any, [new ModelEvent.BranchClosed(draft.StreamId)], ct);
            await CostBranches.CloseAsync(db, projectId, draft.StreamId, ct);

            await transaction.CommitAsync(ct);
            return Results.Ok(CreateDraft.ToDto(draft));
        }, ct);
    }
}
