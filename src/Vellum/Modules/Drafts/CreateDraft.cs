// src/Vellum/Modules/Drafts/CreateDraft.cs
using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Vellum.Kernel.Aggregates;
using Vellum.Kernel.EventStore;
using Vellum.Modules.Drafts.Entities;
using Vellum.Modules.Modelling.Model;
using Vellum.Modules.Workspaces;
using Vellum.Modules.Workspaces.Authorization;
using Vellum.Shared;

namespace Vellum.Modules.Drafts;

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
        DraftsDbContext draftsDb,
        WorkspacesDbContext workspacesDb,
        WorkspaceAuthorizationService auth,
        AggregateStore store,
        IEventStore eventStore,
        CancellationToken ct)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier)!;
        await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Editor, ct);

        var existing = await draftsDb.Drafts.FindAsync([request.Id], ct);
        if (existing is not null)
            return Results.Ok(ToDto(existing));

        var proj = await workspacesDb.Projects.AsNoTracking()
            .FirstAsync(p => p.Id == projectId, ct);

        var (mainState, mainVersion) = await store.LoadAsync<ModelState, ModelEvent>(proj.StreamId, ct);

        var draftStreamId = Guid.NewGuid();
        var stateJson = JsonSerializer.SerializeToDocument(mainState, JsonOpts);
        await eventStore.AppendAsync(draftStreamId, "model", 0, stateJson, [], ct);

        var baseSnapshotJson = JsonSerializer.Serialize(mainState, JsonOpts);

        var draft = new DraftEntity
        {
            Id = request.Id,
            ProjectId = projectId,
            Name = request.Name,
            StreamId = draftStreamId,
            BaseStreamId = proj.StreamId,
            ForkVersion = mainVersion,
            CreatedBy = userId,
            BaseSnapshot = baseSnapshotJson,
        };
        draftsDb.Drafts.Add(draft);
        await draftsDb.SaveChangesAsync(ct);

        return Results.Created(
            $"/api/projects/{projectId}/drafts/{draft.Id}",
            ToDto(draft));
    }

    internal static DraftDto ToDto(DraftEntity d) =>
        new(d.Id, d.ProjectId, d.Name, d.StreamId, d.BaseStreamId,
            d.ForkVersion, d.Status, d.CreatedBy, d.CreatedAt,
            d.MergedAt, d.AbandonedAt);
}
