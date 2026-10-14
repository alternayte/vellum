using System.Text.Json.Serialization;
using Deedbox;
using Microsoft.EntityFrameworkCore;
using Vellum.Modules.Trace.Engine;
using Vellum.Shared;

namespace Vellum.Modules.Trace;

public static class TraceModule
{
    public static IServiceCollection AddTraceModule(this IServiceCollection services)
    {
        // The connection string is read when the context is first used, so configuration that
        // a test host overrides applies.
        services.AddScoped<Shared.IProjectRetention, ScenariosRetention>();

        // Scenario documents and trace results name their enum members (`Succeeded`,
        // `RequestSent`) in JSON. A member name in any letter case, or its number, is read.
        services.ConfigureHttpJsonOptions(json =>
        {
            json.SerializerOptions.Converters.Add(new JsonStringEnumConverter<StepOutcome>());
            json.SerializerOptions.Converters.Add(new JsonStringEnumConverter<FaultKind>());
            json.SerializerOptions.Converters.Add(new JsonStringEnumConverter<TraceEventKind>());

            // A step or an assertion names its kind in `type`. A client may write that key at
            // any place in the object, not only first.
            json.SerializerOptions.AllowOutOfOrderMetadataProperties = true;
            json.SerializerOptions.MaxDepth = ScenarioShape.MaxJsonDepth;
        });

        return services;
    }

    /// <summary>One stream per scenario, keyed by the scenario ID.</summary>
    public static DeedboxBuilder AddScenarioStreams(this DeedboxBuilder deedbox) => deedbox
        // A stored event wraps the document, so the store must write and read as deep as the
        // deepest document a save accepts. Deedbox reads `type` at any place in a step.
        .ConfigureJson(json => json.MaxDepth = ScenarioShape.MaxJsonDepth)
        .Stream<ScenarioState>("scenario", stream => stream.EventsNestedIn(typeof(ScenarioEvent)))
        .Projection<ScenarioProjection>("scenario_read_model", Run.Inline);
}

/// <summary>What the scenario table and the scenario streams keep of a deleted project: nothing.</summary>
public sealed class ScenariosRetention(AppDbContext db, IEventStore store) : IProjectRetention
{
    public async Task DeleteProjectAsync(DeletedProject project, CancellationToken ct)
    {
        // A deleted scenario has no row, but its stream keeps its events. Each save names the
        // project, so the saves find every scenario stream of the project.
        var streams = await db.Database.SqlQuery<string>($"""
            SELECT DISTINCT stream_id AS "Value" FROM deedbox.events
            WHERE stream_type = 'scenario' AND payload ->> 'projectId' = {project.Id.ToString()}
            """).ToListAsync(ct);
        var rows = await db.Scenarios.AsNoTracking()
            .Where(s => s.ProjectId == project.Id).Select(s => s.Id).ToListAsync(ct);
        foreach (var streamId in streams.Concat(rows.Select(StreamId.From)).Distinct())
            await store.DeleteStream(streamId, ct);

        await db.Scenarios.Where(s => s.ProjectId == project.Id).ExecuteDeleteAsync(ct);
    }
}
