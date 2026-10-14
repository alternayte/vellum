using System.Text.Json.Serialization;
using Deedbox;
using Microsoft.EntityFrameworkCore;
using Vellum.Modules.Trace.Engine;

namespace Vellum.Modules.Trace.Scenarios;

public static class ScenariosModule
{
    public static IServiceCollection AddScenariosModule(this IServiceCollection services)
    {
        // The connection string is read when the context is first used, so configuration that
        // a test host overrides applies.
        services.AddDbContext<ScenariosDbContext>((provider, options) =>
            options.UseNpgsql(provider.GetRequiredService<IConfiguration>().GetConnectionString("DefaultConnection"))
                   .UseSnakeCaseNamingConvention());
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
