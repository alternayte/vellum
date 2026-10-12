using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Vellum.Kernel.Aggregates;
using Vellum.Kernel.EventStore;
using Vellum.Modules.Modelling.Entities;
using Vellum.Modules.Modelling.Model;
using Vellum.Modules.Workspaces;
using Vellum.Modules.Workspaces.Authorization;
using Vellum.Shared;

namespace Vellum.Modules.Modelling.Variants;

public sealed record CreateVariantRequest(Guid Id, string Name, Guid? BaseRevisionId);

public sealed record VariantDto(
    Guid Id, Guid ProjectId, string Name,
    Guid StreamId, Guid BaseStreamId, Guid? BaseRevisionId,
    int ForkVersion, string Status, string CreatedBy, DateTimeOffset CreatedAt);

public static class CreateVariant
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static async Task<IResult> Handle(
        Guid projectId,
        CreateVariantRequest request,
        ClaimsPrincipal user,
        WorkspacesDbContext workspacesDb,
        ModellingDbContext modellingDb,
        AggregateStore store,
        IEventStore eventStore,
        WorkspaceAuthorizationService auth,
        CancellationToken ct)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier)!;
        await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Editor, ct);

        if (string.IsNullOrWhiteSpace(request.Name))
            return Results.BadRequest(new ErrorResponse("validation_error", "Variant name is required"));

        var project = await workspacesDb.Projects.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == projectId, ct);
        if (project is null) return Results.NotFound(new ErrorResponse("not_found", "Project not found"));

        var existingById = await modellingDb.Variants.AsNoTracking()
            .FirstOrDefaultAsync(v => v.Id == request.Id, ct);
        if (existingById is not null)
        {
            return existingById.ProjectId == projectId
                ? Results.Ok(ToDto(existingById))
                : Results.Conflict(new ErrorResponse("conflict", "Variant id is already in use"));
        }

        if (await modellingDb.Variants.AnyAsync(
                v => v.ProjectId == projectId && v.Name == request.Name, ct))
        {
            return Results.Conflict(new ErrorResponse("conflict", "A variant with this name already exists"));
        }

        ModelState sourceState;
        Guid baseStreamId;
        int forkVersion;

        if (request.BaseRevisionId is { } revisionId)
        {
            var revision = await modellingDb.Revisions.AsNoTracking()
                .FirstOrDefaultAsync(r => r.Id == revisionId && r.ProjectId == projectId, ct);
            if (revision is null)
                return Results.NotFound(new ErrorResponse("not_found", "Base revision not found"));

            sourceState = JsonSerializer.Deserialize<ModelState>(revision.State, JsonOpts)!;
            baseStreamId = revision.StreamId;
            forkVersion = revision.Version;
        }
        else
        {
            (sourceState, forkVersion) = await store.LoadAsync<ModelState, ModelEvent>(project.StreamId, ct);
            baseStreamId = project.StreamId;
        }

        var variantStreamId = Guid.NewGuid();
        await eventStore.AppendAsync(
            variantStreamId, "model", 0,
            JsonSerializer.SerializeToDocument(sourceState, JsonOpts), [], ct);

        BranchRows.Write(projectId, variantStreamId, sourceState, modellingDb);

        var variant = new VariantEntity
        {
            Id = request.Id,
            ProjectId = projectId,
            Name = request.Name,
            StreamId = variantStreamId,
            BaseStreamId = baseStreamId,
            BaseRevisionId = request.BaseRevisionId,
            ForkVersion = forkVersion,
            Status = "open",
            CreatedBy = userId,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        modellingDb.Variants.Add(variant);
        await modellingDb.SaveChangesAsync(ct);

        return Results.Created(
            $"/api/projects/{projectId}/variants/{variant.Id}",
            ToDto(variant));
    }

    internal static VariantDto ToDto(VariantEntity variant) => new(
        variant.Id, variant.ProjectId, variant.Name,
        variant.StreamId, variant.BaseStreamId, variant.BaseRevisionId,
        variant.ForkVersion, variant.Status, variant.CreatedBy, variant.CreatedAt);
}
