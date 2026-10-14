using System.Security.Claims;
using Deedbox;
using Microsoft.EntityFrameworkCore;
using Vellum.Modules.Costs;
using Vellum.Modules.Modelling.Entities;
using Vellum.Modules.Modelling.Model;
using Vellum.Modules.Modelling.Revisions;
using Vellum.Modules.Schemas;
using Vellum.Modules.Workspaces;
using Vellum.Modules.Workspaces.Authorization;
using Vellum.Shared;

namespace Vellum.Modules.Modelling.Variants;

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
        CancellationToken ct)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier)!;
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
                var revision = await db.Revisions.AsNoTracking()
                    .FirstOrDefaultAsync(r => r.Id == revisionId && r.ProjectId == projectId, ct);
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
                Status = "open",
                CreatedBy = userId,
                CreatedAt = DateTimeOffset.UtcNow,
            };
            db.Variants.Add(variant);

            // Read before the first append, like every other stream this unit reads.
            var sheetBranch = sheetFrom ?? baseStreamId;
            var (sheet, sheetVersion) = await CostAccess.LoadSheetAsync(branches, sheetBranch, ct);

            // One append. Deedbox saves the variant row in the transaction, and the inline
            // projection writes the branch rows in it.
            await branches.Append(
                StreamId.From(variant.StreamId), ExpectedVersion.NoStream,
                [ModelStore.Fork(sourceState, baseStreamId, forkVersion)], ct);
            // The variant starts with a copy of the cost sheet. The stream of that copy is new
            // and no other writer knows it, so this append waits for no lock.
            await CostBranches.ForkAsync(
                branches, projectId, variant.StreamId, sourceState, sheet, sheetBranch, sheetVersion, userId, variant.CreatedAt, ct);
            await transaction.CommitAsync(ct);

            return Results.Created(
                $"/api/projects/{projectId}/variants/{variant.Id}",
                ToDto(variant));
        }, ct);
    }

    internal static VariantDto ToDto(VariantEntity variant) => new(
        variant.Id, variant.ProjectId, variant.Name,
        variant.StreamId, variant.BaseStreamId, variant.BaseRevisionId,
        variant.ForkVersion, variant.Status, variant.CreatedBy, variant.CreatedAt);

    private static IResult Invalid(string field, string message) =>
        Results.BadRequest(new ErrorResponse("validation_error", "Validation failed", Errors: [new FieldError(field, message)]));
}
