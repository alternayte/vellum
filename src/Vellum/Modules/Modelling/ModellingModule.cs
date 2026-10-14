using Deedbox;
using Microsoft.EntityFrameworkCore;
using Vellum.Shared;

namespace Vellum.Modules.Modelling;

public static class ModellingModule
{
    public static IServiceCollection AddModellingModule(this IServiceCollection services)
    {
        services.AddScoped<Shared.IProjectRetention, ModellingRetention>();

        return services;
    }

    /// <summary>
    /// One stream per model branch: a project's main model, a draft or a variant. The stream
    /// ID is the branch ID.
    /// </summary>
    public static DeedboxBuilder AddModelStreams(this DeedboxBuilder deedbox) => deedbox
        .Stream<ModelState>("model", stream => stream
            .EventsNestedIn(typeof(ModelEvent))
            // Version 2 of the state added operations, channels and subscriptions.
            // Version 3 added the stale mark of a fact.
            // Version 4 added the bindings between channels.
            .StateVersion(4))
        .Projection<ModelProjection>("model_read_model", Run.Inline);
}

/// <summary>
/// What the modelling tables and the model streams keep of a deleted project: nothing. The
/// main model, every draft and variant branch, the named revisions and the variants go.
/// </summary>
public sealed class ModellingRetention(AppDbContext db, IEventStore store) : IProjectRetention
{
    public async Task DeleteProjectAsync(DeletedProject project, CancellationToken ct)
    {
        // Every model event carries the project header (see ModelStore.ForProject), so the
        // events name each branch of the project: main, drafts and variants. The stream of the
        // main model and the variant rows name them too, for a stream whose events are gone.
        var tagged = await db.Database.SqlQuery<string>($"""
            SELECT DISTINCT stream_id AS "Value" FROM deedbox.events
            WHERE stream_type = 'model' AND metadata -> 'headers' ->> {ModelStore.ProjectHeader} = {project.Id.ToString()}
            """).ToListAsync(ct);
        var variants = await db.Variants.AsNoTracking()
            .Where(v => v.ProjectId == project.Id).Select(v => v.StreamId).ToListAsync(ct);

        var streams = tagged
            .Concat(variants.Select(StreamId.From))
            .Append(StreamId.From(project.ModelStreamId))
            .Distinct();
        // A deleted stream is left as it is.
        foreach (var streamId in streams)
            await store.DeleteStream(streamId, ct);

        await db.Elements.Where(e => e.ProjectId == project.Id).ExecuteDeleteAsync(ct);
        await db.Relationships.Where(r => r.ProjectId == project.Id).ExecuteDeleteAsync(ct);
        await db.Messages.Where(m => m.ProjectId == project.Id).ExecuteDeleteAsync(ct);
        await db.Revisions.Where(r => r.ProjectId == project.Id).ExecuteDeleteAsync(ct);
        await db.Variants.Where(v => v.ProjectId == project.Id).ExecuteDeleteAsync(ct);
    }
}
