using Vellum.Kernel.CommandHandling;
using Vellum.Kernel.EventTypes;
using Vellum.Kernel.Projections;
using Vellum.Modules.Schemas.Entities;

namespace Vellum.Modules.Schemas;

public sealed class SchemaProjection : IInlineProjection
{
    private readonly SchemasDbContext _db;
    private readonly IEventTypeRegistry _registry;

    public SchemaProjection(SchemasDbContext db, IEventTypeRegistry registry)
    {
        _db = db;
        _registry = registry;
    }

    public async Task ProjectAsync(IReadOnlyList<CollectedEvent> events, CancellationToken ct = default)
    {
        foreach (var e in events)
        {
            var deserialized = _registry.DeserializeEvent(e.EventType, e.Payload);
            if (deserialized is not SchemaEvent schemaEvent) continue;

            switch (schemaEvent)
            {
                case SchemaEvent.SchemaCreated created:
                    _db.Schemas.Add(new SchemaEntity
                    {
                        Id = created.Id,
                        ProjectId = created.ProjectId,
                        Name = created.Name,
                        Description = created.Description,
                        Content = created.Content,
                        Version = created.Version
                    });
                    break;

                case SchemaEvent.SchemaUpdated updated:
                    var entity = await _db.Schemas.FindAsync([updated.SchemaId], ct);
                    if (entity is not null)
                    {
                        if (updated.Name is not null) entity.Name = updated.Name;
                        if (updated.Description is not null) entity.Description = updated.Description;
                        if (updated.Content is not null) entity.Content = updated.Content;
                        if (updated.Version is not null) entity.Version = updated.Version.Value;
                    }
                    break;

                case SchemaEvent.SchemaDeleted deleted:
                    var toDelete = await _db.Schemas.FindAsync([deleted.SchemaId], ct);
                    if (toDelete is not null) _db.Schemas.Remove(toDelete);
                    break;
            }
        }

        if (events.Count > 0)
            await _db.SaveChangesAsync(ct);
    }
}
