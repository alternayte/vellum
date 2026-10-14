using System.Globalization;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Vellum.Modules.Workspaces;
using Vellum.Modules.Workspaces.Authorization;
using Vellum.Shared;
using static Vellum.Shared.ResultExtensions;

namespace Vellum.Modules.Modelling.Revisions;

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
/// repeatable (SDD guarantee 4), so the revision stays while one of them names it. The
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
