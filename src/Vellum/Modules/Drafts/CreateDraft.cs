// src/Vellum/Modules/Drafts/CreateDraft.cs
using System.Security.Claims;
using System.Text.Json;
using Deedbox;
using Microsoft.EntityFrameworkCore;
using Vellum.Modules.Drafts.Entities;
using Vellum.Modules.Modelling.Model;
using Vellum.Modules.Schemas;
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
            d.ForkVersion, d.Status, d.CreatedBy, d.CreatedAt,
            d.MergedAt, d.AbandonedAt);

    private static IResult Invalid(string field, string message) =>
        Results.BadRequest(new ErrorResponse("validation_error", "Validation failed", Errors: [new FieldError(field, message)]));
}
