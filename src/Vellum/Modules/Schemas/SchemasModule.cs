using Deedbox;
using Microsoft.EntityFrameworkCore;
using Vellum.Shared;

namespace Vellum.Modules.Schemas;

public static class SchemasModule
{
    public static IServiceCollection AddSchemasModule(this IServiceCollection services)
    {
        services.AddScoped<Shared.IProjectRetention, SchemasRetention>();

        return services;
    }

    /// <summary>One stream per schema, keyed by the schema ID.</summary>
    public static DeedboxBuilder AddSchemaStreams(this DeedboxBuilder deedbox) => deedbox
        .Stream<SchemaState>("schema", stream => stream.EventsNestedIn(typeof(SchemaEvent)))
        .Projection<SchemaProjection>("schema_read_model", Run.Inline);
}

/// <summary>What the schema table and the schema streams keep of a deleted project: nothing.</summary>
public sealed class SchemasRetention(AppDbContext db, IEventStore store) : IProjectRetention
{
    public async Task DeleteProjectAsync(DeletedProject project, CancellationToken ct)
    {
        // A deleted schema has no row, but its stream keeps its events. The create event names
        // the project, so it finds every schema stream of the project.
        var streams = await db.Database.SqlQuery<string>($"""
            SELECT DISTINCT stream_id AS "Value" FROM deedbox.events
            WHERE stream_type = 'schema' AND payload ->> 'projectId' = {project.Id.ToString()}
            """).ToListAsync(ct);
        var rows = await db.Schemas.AsNoTracking()
            .Where(s => s.ProjectId == project.Id).Select(s => s.Id).ToListAsync(ct);
        foreach (var streamId in streams.Concat(rows.Select(StreamId.From)).Distinct())
            await store.DeleteStream(streamId, ct);

        await db.Schemas.Where(s => s.ProjectId == project.Id).ExecuteDeleteAsync(ct);
    }
}
