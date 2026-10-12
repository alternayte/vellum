// src/Vellum/Modules/Drafts/CreateDraft.cs
using System.Security.Claims;
using System.Text.Json;
using Deedbox;
using Microsoft.EntityFrameworkCore;
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
        HttpContext http,
        WorkspaceAuthorizationService auth,
        CancellationToken ct)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier)!;
        await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Editor, ct);

        // The draft row, the fork event and the branch read-model rows commit together.
        return await SharedTransaction.RunAsync(http.RequestServices, async services =>
        {
            var draftsDb = services.GetRequiredService<DraftsDbContext>();
            var workspacesDb = services.GetRequiredService<WorkspacesDbContext>();
            var store = services.GetRequiredService<IEventStore>();

            var existing = await draftsDb.Drafts.FindAsync([request.Id], ct);
            if (existing is not null)
                return Results.Ok(ToDto(existing));

            var proj = await workspacesDb.Projects.AsNoTracking()
                .FirstAsync(p => p.Id == projectId, ct);

            var (mainState, mainVersion) = await store.Load<ModelState>(StreamId.From(proj.StreamId), ct);

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
            draftsDb.Drafts.Add(draft);

            // One append in a transaction that Deedbox opens on the DbContext and commits. Deedbox
            // saves the draft row in it. The inline projection writes the branch rows in it, so
            // the draft reads as a full model, not just the changes made on it.
            await store.ForProject(projectId).UseDbContext(draftsDb).Append(
                StreamId.From(draft.StreamId), ExpectedVersion.NoStream,
                [ModelStore.Fork(mainState, proj.StreamId, mainVersion)], ct);

            return Results.Created(
                $"/api/projects/{projectId}/drafts/{draft.Id}",
                ToDto(draft));
        }, ct);
    }

    internal static DraftDto ToDto(DraftEntity d) =>
        new(d.Id, d.ProjectId, d.Name, d.StreamId, d.BaseStreamId,
            d.ForkVersion, d.Status, d.CreatedBy, d.CreatedAt,
            d.MergedAt, d.AbandonedAt);
}
