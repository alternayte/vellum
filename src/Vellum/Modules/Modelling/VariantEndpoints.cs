using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Vellum.Modules.Workspaces;
using Vellum.Shared;
using Deedbox;
using Vellum.Modules.Costs;
using Vellum.Modules.Schemas;
using static Vellum.Shared.ResultExtensions;

namespace Vellum.Modules.Modelling;

public static class VariantEndpoints
{
    public static WebApplication MapVariantEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/projects/{projectId}/variants")
            .RequireAuthorization()
            .WithTags("Variants");

        group.MapPost("/", CreateVariant.Handle);
        group.MapGet("/", ListVariants.Handle);
        group.MapGet("/{variantId}", GetVariant.Handle);
        group.MapDelete("/{variantId}", DeleteVariant.Handle).Produces(StatusCodes.Status204NoContent);

        return app;
    }
}

public static class ListVariants
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

        var variants = await db.Variants.AsNoTracking()
            .Where(v => v.ProjectId == projectId)
            .OrderByDescending(v => v.CreatedAt).ThenBy(v => v.Id)
            .Select(v => new VariantDto(
                v.Id, v.ProjectId, v.Name,
                v.StreamId, v.BaseStreamId, v.BaseRevisionId,
                v.ForkVersion, EnumText.Of(v.Status), v.CreatedBy, v.CreatedAt))
            .ToListAsync(ct);

        return Results.Ok(variants);
    }
}

public static class GetVariant
{
    public static async Task<IResult> Handle(
        Guid projectId,
        Guid variantId,
        ClaimsPrincipal user,
        AppDbContext db,
        WorkspaceAuthorizationService auth,
        CancellationToken ct)
    {
        var userId = user.UserId();
        await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Viewer, ct);

        var variant = await db.Variants.AsNoTracking()
            .FirstOrDefaultAsync(v => v.Id == variantId && v.ProjectId == projectId, ct);
        if (variant is null) return Results.NotFound(new ErrorResponse("not_found", "Variant not found"));

        return Results.Ok(CreateVariant.ToDto(variant));
    }
}

/// <param name="CostSheetFrom">
/// The branch whose cost sheet the variant starts with: <c>main</c>, or the stream ID of an
/// open draft or variant. Default: the branch the model is forked from.
/// </param>
public sealed record CreateVariantRequest(Guid Id, string Name, Guid? BaseRevisionId, string? CostSheetFrom = null);

public sealed record VariantDto(
    Guid Id, Guid ProjectId, string Name,
    Guid StreamId, Guid BaseStreamId, Guid? BaseRevisionId,
    int ForkVersion, string Status, string CreatedBy, DateTimeOffset CreatedAt);

public static class CreateVariant
{
    public static async Task<IResult> Handle(
        Guid projectId,
        CreateVariantRequest request,
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
            return Results.BadRequest(new ErrorResponse("validation_error", "Variant name is required"));
        if (TextRule.Line("name", request.Name) is { } textError)
            return Invalid(textError.Field, textError.Message);

        Guid? sheetFrom = null;
        if (request.CostSheetFrom is not null)
        {
            var (branch, refusal) = await CostSheets.ResolveSideAsync(http.RequestServices, projectId, request.CostSheetFrom, "costSheetFrom", ct);
            if (refusal is not null)
                return refusal;
            sheetFrom = branch;
        }

        // The variant row, the fork event, the branch read-model rows and the copy of the cost
        // sheet commit together.
        return await SharedTransaction.RunAsync(http.RequestServices, async services =>
        {
            var db = services.GetRequiredService<AppDbContext>();
            var store = services.GetRequiredService<IEventStore>();

            var project = await db.Projects.AsNoTracking()
                .FirstOrDefaultAsync(p => p.Id == projectId, ct);
            if (project is null) return Results.NotFound(new ErrorResponse("not_found", "Project not found"));

            var existingById = await db.Variants.AsNoTracking()
                .FirstOrDefaultAsync(v => v.Id == request.Id, ct);
            if (existingById is not null)
            {
                return existingById.ProjectId == projectId
                    ? Results.Ok(ToDto(existingById))
                    : Results.Conflict(new ErrorResponse("conflict", "Variant id is already in use"));
            }

            if (await db.Variants.AnyAsync(
                    v => v.ProjectId == projectId && v.Name == request.Name, ct))
            {
                return Results.Conflict(new ErrorResponse("conflict", "A variant with this name already exists"));
            }

            // One transaction: the variant row, the fork event, the branch read-model rows and
            // the hold on the schemas the fork names (see SchemaReferences).
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var branches = store.ForProject(projectId).UseDbContext(db);

            ModelState sourceState;
            Guid baseStreamId;
            long forkVersion;

            if (request.BaseRevisionId is { } revisionId)
            {
                // The row is held until the commit, so a delete of the revision waits for the
                // fork and then finds the variant that names it (see DeleteRevision).
                var revision = await db.Revisions
                    .FromSql($"SELECT * FROM modelling.revisions WHERE id = {revisionId} AND project_id = {projectId} FOR KEY SHARE")
                    .AsNoTracking()
                    .ToListAsync(ct) is [var held] ? held : null;
                if (revision is null)
                    return Results.NotFound(new ErrorResponse("not_found", "Base revision not found"));

                sourceState = CreateRevision.ReadState(revision.State);
                baseStreamId = revision.StreamId;
                forkVersion = revision.Version;
            }
            else
            {
                // The stream lock gives the newest model, in which every schema reference
                // points at a schema that exists.
                var main = await branches.Execute<ModelState>(StreamId.From(project.StreamId), _ => [], ct);
                (sourceState, forkVersion) = (main.State, main.Version);
                baseStreamId = project.StreamId;
            }

            await SchemaReferences.HoldAsync(
                db, projectId, ContractDecider.ReferencedSchemaIds(sourceState), ct);

            var variant = new VariantEntity
            {
                Id = request.Id,
                ProjectId = projectId,
                Name = request.Name,
                StreamId = Guid.NewGuid(),
                BaseStreamId = baseStreamId,
                BaseRevisionId = request.BaseRevisionId,
                ForkVersion = checked((int)forkVersion),
                Status = VariantStatus.Open,
                CreatedBy = userId,
                CreatedAt = time.GetUtcNow(),
            };
            db.Variants.Add(variant);

            // Read before the first append, like every other stream this unit reads.
            var sheetBranch = sheetFrom ?? baseStreamId;
            var (sheet, sheetVersion) = await CostAccess.LoadSheetAsync(db, sheetBranch, ct);

            // One append. Deedbox saves the variant row in the transaction, and the inline
            // projection writes the branch rows in it.
            await branches.Append(
                StreamId.From(variant.StreamId), ExpectedVersion.NoStream,
                [ModelStore.Fork(sourceState, baseStreamId, forkVersion)], ct);
            // The variant starts with a copy of the cost sheet. The branch is new and no other
            // writer knows it, so this write waits for no lock.
            await CostBranches.ForkAsync(
                db, projectId, variant.StreamId, sourceState, sheet, sheetBranch, sheetVersion, userId, variant.CreatedAt, ct);
            await transaction.CommitAsync(ct);

            return Results.Created(
                $"/api/projects/{projectId}/variants/{variant.Id}",
                ToDto(variant));
        }, ct);
    }

    internal static VariantDto ToDto(VariantEntity variant) => new(
        variant.Id, variant.ProjectId, variant.Name,
        variant.StreamId, variant.BaseStreamId, variant.BaseRevisionId,
        variant.ForkVersion, EnumText.Of(variant.Status), variant.CreatedBy, variant.CreatedAt);

}

/// <summary>
/// Deletes a variant: its row, its read-model rows, its cost sheet and its saved estimates.
/// The model stream of the variant stays as history, like the stream of an abandoned draft:
/// a stress run, a load-test script or a promise run that was pinned to a version of it can
/// still be played again on that version. A project delete removes the
/// stream with every other stream of the project.
/// </summary>
public static class DeleteVariant
{
    public static async Task<IResult> Handle(
        Guid projectId, Guid variantId, ClaimsPrincipal user, HttpContext http,
        WorkspaceAuthorizationService auth, CancellationToken ct)
    {
        await auth.RequireProjectRoleAsync(projectId, user.UserId(), WorkspaceRole.Editor, ct);

        // The row, the closing event, the removal of the branch rows and of the cost sheet
        // commit together.
        return await SharedTransaction.RunAsync(http.RequestServices, async services =>
        {
            var db = services.GetRequiredService<AppDbContext>();
            var streamId = await db.Variants.AsNoTracking()
                .Where(v => v.Id == variantId && v.ProjectId == projectId)
                .Select(v => (Guid?)v.StreamId)
                .FirstOrDefaultAsync(ct);
            if (streamId is not { } branch)
                return NotFound("Variant");

            // A return before the commit rolls everything back.
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var store = services.GetRequiredService<IEventStore>().ForProject(projectId).UseDbContext(db);

            // The model stream and then the cost sheet stay locked until the commit, so a
            // write of either that waits for the lock finds the variant gone.
            await store.Execute<ModelState>(StreamId.From(branch), _ => [], ct);
            await CostBranches.LockSheetsAsync(db, [branch], ct);

            var variant = await db.Variants.FirstOrDefaultAsync(v => v.Id == variantId && v.ProjectId == projectId, ct);
            if (variant is null)
                return NotFound("Variant");
            db.Variants.Remove(variant);

            // One append. Deedbox saves the removed row in the transaction, and the inline
            // projection drops the branch rows.
            await store.Append(StreamId.From(branch), ExpectedVersion.Any, [new ModelEvent.BranchClosed(branch)], ct);
            await CostBranches.CloseAsync(db, projectId, branch, ct);

            await transaction.CommitAsync(ct);
            return Results.NoContent();
        }, ct);
    }
}
