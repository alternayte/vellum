using Deedbox;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Vellum.Modules.Promises.Checks;
using Vellum.Shared;

namespace Vellum.Modules.Promises;

/// <summary>
/// Promises with stable requirement IDs, their proof obligations and their verification runs
/// (SDD §4 and §8, the Architecture Promise Loop). Nothing in this module calls an AI.
/// </summary>
public static class PromisesModule
{
    public static IServiceCollection AddPromisesModule(this IServiceCollection services)
    {
        // The connection string is read when the context is first used, so configuration that
        // a test host overrides applies.
        services.AddDbContext<PromisesDbContext>((provider, options) =>
            options.UseNpgsql(provider.GetRequiredService<IConfiguration>().GetConnectionString("DefaultConnection"))
                   .UseSnakeCaseNamingConvention());
        services.AddScoped<PromiseReader>();
        services.TryAddSingleton(TimeProvider.System);
        return services;
    }

    /// <summary>One stream per promise, keyed by the promise ID.</summary>
    public static DeedboxBuilder AddPromiseStreams(this DeedboxBuilder deedbox) => deedbox
        .Stream<PromiseState>("promise", stream => stream.EventsNestedIn(typeof(PromiseEvent)))
        .Projection<PromiseProjection>("promise_read_model", Run.Inline);

    public static WebApplication MapPromiseEndpoints(this WebApplication app)
    {
        var promises = app.MapGroup("/api/projects/{projectId}/promises")
            .RequireAuthorization()
            .WithTags("Promises");

        promises.MapGet("/", PromiseReads.List).Produces<Page<PromiseSummaryDto>>();
        promises.MapPost("/", PromiseWrites.Create)
            .Accepts<PromiseRequest>("application/json")
            .Produces<PromiseDto>(StatusCodes.Status201Created);

        promises.MapGet("/prefix", PromiseReads.Prefix).Produces<PrefixDto>();
        promises.MapPut("/prefix", PromiseWrites.SetPrefix)
            .Accepts<SetPrefixRequest>("application/json")
            .Produces<PrefixDto>()
            .Produces<ErrorResponse>(StatusCodes.Status409Conflict);

        promises.MapGet("/{promiseId:guid}", PromiseReads.Get).Produces<PromiseDto>();
        promises.MapPut("/{promiseId:guid}", PromiseWrites.Update)
            .Accepts<PromiseRequest>("application/json")
            .Produces<PromiseDto>();
        promises.MapDelete("/{promiseId:guid}", PromiseWrites.Delete).Produces(StatusCodes.Status204NoContent);

        promises.MapPost("/{promiseId:guid}/revise", PromiseWrites.Revise)
            .Accepts<ReviseRequest>("application/json")
            .Produces<PromiseDto>();
        promises.MapPost("/{promiseId:guid}/approve", PromiseWrites.Approve)
            .Accepts<ApproveRequest>("application/json")
            .Produces<PromiseDto>()
            .Produces<ErrorResponse>(StatusCodes.Status403Forbidden);

        promises.MapGet("/{promiseId:guid}/runs", PromiseReads.Runs).Produces<Page<VerificationRun>>();

        var obligations = promises.MapGroup("/{promiseId:guid}/obligations");
        obligations.MapPost("/", ObligationWrites.Add)
            .Accepts<ObligationRequest>("application/json")
            .Produces<PromiseDto>(StatusCodes.Status201Created);
        obligations.MapPost("/generate", ObligationWrites.Generate).Produces<GenerateObligationsResponse>();
        obligations.MapPut("/{obligationKey}", ObligationWrites.Change)
            .Accepts<ObligationRequest>("application/json")
            .Produces<PromiseDto>();
        obligations.MapDelete("/{obligationKey}", ObligationWrites.Remove).Produces<PromiseDto>();
        obligations.MapPost("/{obligationKey}/run", ObligationWrites.Run)
            .Accepts<RunObligationRequest>("application/json")
            .Produces<VerificationRun>();
        obligations.MapPost("/{obligationKey}/evidence", ObligationWrites.Evidence)
            .Accepts<RecordEvidenceRequest>("application/json")
            .Produces<VerificationRun>();

        return app;
    }
}
