using Deedbox;
using Microsoft.EntityFrameworkCore;
using Vellum.Modules.Schemas.Entities;

namespace Vellum.Modules.Schemas;

/// <summary>
/// The schema read model. It runs inline: the rows commit in the same transaction as the
/// events, so a read after a command sees the command's effect.
/// </summary>
public sealed class SchemaProjection : Projection<SchemasDbContext>
{
    public SchemaProjection()
    {
        On<SchemaEvent.SchemaCreated>((created, ctx) =>
        {
            ctx.Db.Schemas.Add(new SchemaEntity
            {
                Id = created.Id,
                ProjectId = created.ProjectId,
                Name = created.Name,
                Description = created.Description,
                Content = created.Content,
                Version = created.Version
            });
            return Task.CompletedTask;
        });

        On<SchemaEvent.SchemaUpdated>(async (updated, ctx) =>
        {
            var entity = await ctx.Db.Schemas.FindAsync([updated.SchemaId], ctx.CancellationToken);
            if (entity is null) return;

            if (updated.Name is not null) entity.Name = updated.Name;
            if (updated.Description is not null) entity.Description = updated.Description;
            if (updated.Content is not null) entity.Content = updated.Content;
            if (updated.Version is not null) entity.Version = updated.Version.Value;
        });

        On<SchemaEvent.SchemaDeleted>(async (deleted, ctx) =>
        {
            var entity = await ctx.Db.Schemas.FindAsync([deleted.SchemaId], ctx.CancellationToken);
            if (entity is not null) ctx.Db.Schemas.Remove(entity);
        });
    }

    protected override Task ResetAsync(WriteContext<SchemasDbContext> context) =>
        context.Db.Schemas.ExecuteDeleteAsync(context.CancellationToken);
}
