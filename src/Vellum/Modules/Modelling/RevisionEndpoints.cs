using Vellum.Modules.Identity.AgentTokens;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Vellum.Modules.Workspaces;
using Vellum.Shared;
using System.Text.Json;
using Deedbox;
using Vellum.Modules.Schemas;
using static Vellum.Shared.ResultExtensions;
using System.Globalization;

namespace Vellum.Modules.Modelling;

public static class RevisionEndpoints
{
    public static WebApplication MapRevisionEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/projects/{projectId}/revisions")
            .RequireAuthorization()
            .WithTags("Revisions");

        group.MapPost("/", CreateRevision.Handle);
        group.MapGet("/", ListRevisions.Handle).AllowAgent(AgentScope.Read);
        group.MapGet("/{revisionId}", GetRevision.Handle).AllowAgent(AgentScope.Read);
        group.MapPatch("/{revisionId}", RenameRevision.Handle).Produces<RevisionDto>();
        group.MapDelete("/{revisionId}", DeleteRevision.Handle).Produces(StatusCodes.Status204NoContent);
        group.MapGet("/{revisionId}/model", PinnedModelRead.RevisionModel).Produces<RevisionModelDto>();

        app.MapGet("/api/projects/{projectId}/model/compare", PinnedModelRead.Compare)
            .RequireAuthorization()
            .WithTags("Revisions")
            .Produces<ModelComparisonDto>();

        return app;
    }
}

public static class ListRevisions
{
    public static async Task<IResult> Handle(
        Guid projectId,
        ClaimsPrincipal user,
        AppDbContext db,
        WorkspaceAuthorizationService auth,
        CancellationToken ct)
    {
        var userId = user.UserId();
        await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Viewer, ct);

        var revisions = await db.Revisions.AsNoTracking()
            .Where(r => r.ProjectId == projectId)
            .OrderByDescending(r => r.CreatedAt).ThenBy(r => r.Id)
            .Select(r => new RevisionDto(
                r.Id, r.ProjectId, r.Name, r.Note,
                r.StreamId, r.Version, r.CreatedBy, r.CreatedAt))
            .ToListAsync(ct);

        return Results.Ok(revisions);
    }
}

public static class GetRevision
{
    public static async Task<IResult> Handle(
        Guid projectId,
        Guid revisionId,
        ClaimsPrincipal user,
        AppDbContext db,
        WorkspaceAuthorizationService auth,
        CancellationToken ct)
    {
        var userId = user.UserId();
        await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Viewer, ct);

        var revision = await db.Revisions.AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == revisionId && r.ProjectId == projectId, ct);
        if (revision is null) return Results.NotFound(new ErrorResponse("not_found", "Revision not found"));

        return Results.Ok(CreateRevision.ToDto(revision));
    }
}

public sealed record CreateRevisionRequest(Guid Id, string Name, string? Note);

public sealed record RevisionDto(
    Guid Id, Guid ProjectId, string Name, string? Note,
    Guid StreamId, int Version, string CreatedBy, DateTimeOffset CreatedAt);

public static class CreateRevision
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static async Task<IResult> Handle(
        Guid projectId,
        CreateRevisionRequest request,
        ClaimsPrincipal user,
        HttpContext http,
        WorkspaceAuthorizationService auth,
        TimeProvider time,
        CancellationToken ct)
    {
        var userId = user.UserId();
        await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Editor, ct);

        if (request.Id == Guid.Empty)
            return Invalid("id", "An ID is required");
        if (string.IsNullOrWhiteSpace(request.Name))
            return Results.BadRequest(new ErrorResponse("validation_error", "Revision name is required"));
        if ((TextRule.Line("name", request.Name) ?? TextRule.Lines("note", request.Note)) is { } textError)
            return Invalid(textError.Field, textError.Message);

        // The snapshot and the hold on the schemas it names are one transaction. A unit that
        // fails with a deadlock wrote nothing and runs again.
        return await SharedTransaction.RunAsync(http.RequestServices, async services =>
        {
            var db = services.GetRequiredService<AppDbContext>();

            var project = await db.Projects.AsNoTracking()
                .FirstOrDefaultAsync(p => p.Id == projectId, ct);
            if (project is null) return Results.NotFound(new ErrorResponse("not_found", "Project not found"));

            var existingById = await db.Revisions.AsNoTracking()
                .FirstOrDefaultAsync(r => r.Id == request.Id, ct);
            if (existingById is not null)
            {
                return existingById.ProjectId == projectId
                    ? Results.Ok(ToDto(existingById))
                    : Results.Conflict(new ErrorResponse("conflict", "Revision id is already in use"));
            }

            if (await db.Revisions.AnyAsync(
                    r => r.ProjectId == projectId && r.Name == request.Name, ct))
            {
                return Results.Conflict(new ErrorResponse("conflict", "A revision with this name already exists"));
            }

            // The stream lock gives the newest model, in which every schema reference points at
            // a schema that exists. Holding those schema rows until the commit keeps a delete
            // from running between this read and the stored snapshot (see SchemaReferences).
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var store = services.GetRequiredService<IEventStore>().UseDbContext(db);
            var pinned = await store.Execute<ModelState>(StreamId.From(project.StreamId), _ => [], ct);
            await SchemaReferences.HoldAsync(
                db, projectId, ContractDecider.ReferencedSchemaIds(pinned.State), ct);

            var revision = new RevisionEntity
            {
                Id = request.Id,
                ProjectId = projectId,
                Name = request.Name,
                Note = request.Note,
                StreamId = project.StreamId,
                Version = checked((int)pinned.Version),
                State = JsonSerializer.Serialize(pinned.State, JsonOpts),
                CreatedBy = userId,
                CreatedAt = time.GetUtcNow(),
            };

            db.Revisions.Add(revision);
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);

            return Results.Created(
                $"/api/projects/{projectId}/revisions/{revision.Id}",
                ToDto(revision));
        }, ct);
    }

    /// <summary>The model a revision stores.</summary>
    internal static ModelState ReadState(string state) => JsonSerializer.Deserialize<ModelState>(state, JsonOpts)!;

    internal static RevisionDto ToDto(RevisionEntity revision) => new(
        revision.Id, revision.ProjectId, revision.Name, revision.Note,
        revision.StreamId, revision.Version, revision.CreatedBy, revision.CreatedAt);

}

public sealed record RenameRevisionRequest(string? Name);

/// <summary>
/// A rename of a named revision. The stored model of the revision does not change. A bundle
/// that was exported from the revision keeps the name it was pinned with (see
/// <c>PinnedState.RevisionName</c>), so it is generated again byte for byte.
/// </summary>
public static class RenameRevision
{
    public static async Task<IResult> Handle(
        Guid projectId, Guid revisionId, RenameRevisionRequest request,
        ClaimsPrincipal user, AppDbContext db, WorkspaceAuthorizationService auth, CancellationToken ct)
    {
        await auth.RequireProjectRoleAsync(projectId, user.UserId(), WorkspaceRole.Editor, ct);

        if (string.IsNullOrWhiteSpace(request.Name))
            return Invalid("name", "Revision name is required");
        if (TextRule.Line("name", request.Name) is { } textError)
            return Invalid(textError);

        var revision = await db.Revisions.FirstOrDefaultAsync(r => r.Id == revisionId && r.ProjectId == projectId, ct);
        if (revision is null)
            return NotFound("Revision");
        if (revision.Name == request.Name)
            return Results.Ok(CreateRevision.ToDto(revision));
        if (await db.Revisions.AnyAsync(r => r.ProjectId == projectId && r.Name == request.Name && r.Id != revisionId, ct))
            return Conflict("A revision with this name already exists");

        revision.Name = request.Name;
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.UniqueViolation })
        {
            // Another rename or create took the name between the check and the write.
            return Conflict("A revision with this name already exists");
        }
        return Results.Ok(CreateRevision.ToDto(revision));
    }
}

/// <summary>
/// A delete of a named revision that nothing pins. A promise run, a review snapshot and a
/// variant name the revision they were made on, and that record must stay readable and
/// repeatable, so the revision stays while one of them names it. The
/// refusal names each of them.
/// </summary>
public static class DeleteRevision
{
    /// <summary>The most references a refusal names one by one. The rest are counted.</summary>
    private const int MaxNamed = 20;

    public static async Task<IResult> Handle(
        Guid projectId, Guid revisionId, ClaimsPrincipal user, HttpContext http,
        WorkspaceAuthorizationService auth, CancellationToken ct)
    {
        await auth.RequireProjectRoleAsync(projectId, user.UserId(), WorkspaceRole.Editor, ct);

        return await SharedTransaction.RunAsync(http.RequestServices, async services =>
        {
            var db = services.GetRequiredService<AppDbContext>();
            await using var transaction = await db.Database.BeginTransactionAsync(ct);

            // The row lock keeps a new variant from being forked from this revision until the
            // commit: a fork holds the row with FOR KEY SHARE (see CreateVariant).
            var held = await db.Database.SqlQuery<Guid>($"""
                SELECT id AS "Value" FROM modelling.revisions
                WHERE id = {revisionId} AND project_id = {projectId}
                FOR UPDATE
                """).ToListAsync(ct);
            if (held.Count == 0)
                return NotFound("Revision");

            var references = await ReferencesAsync(db, projectId, revisionId, ct);
            if (references.Count > 0)
            {
                return Results.Conflict(new ErrorResponse(
                    "revision_in_use",
                    "The revision is pinned, so it stays.",
                    "Each record below names this revision. A record of a check or a review stays readable and repeatable on the model it names.",
                    [.. references.Select(r => new FieldError("references", r))]));
            }

            await db.Revisions.Where(r => r.Id == revisionId && r.ProjectId == projectId).ExecuteDeleteAsync(ct);
            await transaction.CommitAsync(ct);
            return Results.NoContent();
        }, ct);
    }

    /// <summary>A sentence for each record that names the revision: promise runs, review snapshots, variants.</summary>
    internal static async Task<IReadOnlyList<string>> ReferencesAsync(
        AppDbContext db, Guid projectId, Guid revisionId, CancellationToken ct)
    {
        var references = new List<string>();
        var idText = revisionId.ToString();

        // A run stores its pin in the JSON document (VerificationRun.ModelRevision).
        var runCount = await db.Database.SqlQuery<int>($"""
            SELECT count(*)::int AS "Value" FROM promises.runs
            WHERE project_id = {projectId} AND document -> 'modelRevision' ->> 'revisionId' = {idText}
            """).SingleAsync(ct);
        if (runCount > 0)
        {
            var runs = await db.Database.SqlQuery<PinnedRun>($"""
                SELECT obligation_id, at FROM promises.runs
                WHERE project_id = {projectId} AND document -> 'modelRevision' ->> 'revisionId' = {idText}
                ORDER BY at, id
                LIMIT {MaxNamed}
                """).ToListAsync(ct);
            references.AddRange(runs.Select(r =>
                $"The promise run of {r.ObligationId} at {r.At.ToString("u", CultureInfo.InvariantCulture)}"));
            if (runCount > runs.Count)
                references.Add($"{runCount - runs.Count} more promise runs");
        }

        var snapshots = await db.ReviewSnapshots.AsNoTracking()
            .Where(s => s.ProjectId == projectId && s.RevisionId == revisionId)
            .OrderBy(s => s.CreatedAt).ThenBy(s => s.Id)
            .Select(s => new { s.Note, s.CreatedAt })
            .ToListAsync(ct);
        references.AddRange(snapshots.Select(s => s.Note is { } note
            ? $"The review snapshot '{note}'"
            : $"The review snapshot of {s.CreatedAt.ToString("u", CultureInfo.InvariantCulture)}"));

        var variants = await db.Variants.AsNoTracking()
            .Where(v => v.ProjectId == projectId && v.BaseRevisionId == revisionId)
            .OrderBy(v => v.Name)
            .Select(v => v.Name)
            .ToListAsync(ct);
        references.AddRange(variants.Select(name => $"The variant '{name}', which was forked from it"));

        return references;
    }

    private sealed class PinnedRun
    {
        public string ObligationId { get; set; } = "";
        public DateTimeOffset At { get; set; }
    }
}
