using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Vellum.Modules.Stress.Engine;
using Vellum.Shared;

namespace Vellum.Modules.Stress;

/// <summary>
/// Stress profiles, stored runs and their comparison (SDD §8, the stress engine). A run is an
/// estimate from a seeded model of the declared flow. It never measures a running system.
/// </summary>
public static class StressModule
{
    /// <summary>The most bytes of a profile document: 200 entries of each list fit with room.</summary>
    private const long MaxProfileBytes = 256 * 1024;

    public static IServiceCollection AddStressModule(this IServiceCollection services)
    {
        // The connection string is read when the context is first used, so configuration that
        // a test host overrides applies.
        services.TryAddSingleton(StressLimits.Default);
        services.TryAddSingleton(new StressRunPolicy());
        services.AddSingleton<StressRunGate>();
        services.AddScoped<IProjectRetention, StressRetention>();
        services.TryAddSingleton(TimeProvider.System);

        // Labels, arrival kinds and distribution kinds are lower-case words in JSON (`declared`, `poisson`).
        services.ConfigureHttpJsonOptions(json => StressJson.AddConverters(json.SerializerOptions.Converters));
        return services;
    }

    public static WebApplication MapStressEndpoints(this WebApplication app)
    {
        var stress = app.MapGroup("/api/projects/{projectId}/stress")
            .RequireAuthorization()
            .WithBodyLimit(MaxProfileBytes)
            .WithTags("Stress");

        stress.MapGet("/requirements", StressProfiles.Requirements).Produces<StressRequirements>();

        stress.MapGet("/defaults", StressProfiles.Defaults).Produces<StressProfileDefaults>();
        stress.MapGet("/profiles", StressProfiles.List).Produces<IReadOnlyList<StressProfileSummaryDto>>();
        stress.MapPost("/profiles", StressProfiles.Create)
            .Accepts<StressProfile>("application/json")
            .Produces<StressProfileDto>(StatusCodes.Status201Created);
        stress.MapGet("/profiles/{profileId:guid}", StressProfiles.Get).Produces<StressProfileDto>();
        stress.MapPut("/profiles/{profileId:guid}", StressProfiles.Update)
            .Accepts<StressProfile>("application/json")
            .Produces<StressProfileDto>();
        stress.MapDelete("/profiles/{profileId:guid}", StressProfiles.Delete).Produces(StatusCodes.Status204NoContent);

        // A run stores a result, so it needs an Editor. A Viewer reads results.
        stress.MapPost("/profiles/{profileId:guid}/runs", StressRuns.Run)
            .Produces<StressRunDto>(StatusCodes.Status201Created)
            .Produces<StressRefusedResponse>(StatusCodes.Status422UnprocessableEntity)
            .Produces<ErrorResponse>(StatusCodes.Status429TooManyRequests);
        stress.MapGet("/profiles/{profileId:guid}/runs", StressRuns.List).Produces<IReadOnlyList<StressRunSummaryDto>>();
        stress.MapGet("/runs/{runId:guid}", StressRuns.Get).Produces<StressRunDto>();
        stress.MapDelete("/runs/{runId:guid}", StressRuns.Delete).Produces(StatusCodes.Status204NoContent);

        stress.MapGet("/compare", StressCompare.Handle).Produces<StressComparisonDto>();

        return app;
    }
}

/// <summary>What the stress tables keep of a deleted project: nothing.</summary>
public sealed class StressRetention(AppDbContext db) : IProjectRetention
{
    // The runs of a profile go with its row (ON DELETE CASCADE).
    public Task DeleteProjectAsync(DeletedProject project, CancellationToken ct) =>
        db.Profiles.Where(p => p.ProjectId == project.Id).ExecuteDeleteAsync(ct);
}
