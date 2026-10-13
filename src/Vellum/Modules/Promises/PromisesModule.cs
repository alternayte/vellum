using System.Security.Claims;
using System.Threading.RateLimiting;
using Deedbox;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Vellum.Modules.Identity.AgentTokens;
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
        services.TryAddSingleton(PromiseLimits.Default);

        // Every run and every piece of evidence is an event of a promise stream. One caller
        // records a bounded number of them in a minute, whichever promise they are for.
        services.Configure<RateLimiterOptions>(options => options.AddPolicy(PromiseLimits.RunPolicy, http =>
            RateLimitPartition.GetFixedWindowLimiter(
                http.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = http.RequestServices.GetRequiredService<PromiseLimits>().RunsPerMinute,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                })));
        return services;
    }

    /// <summary>One stream per promise, keyed by the promise ID.</summary>
    public static DeedboxBuilder AddPromiseStreams(this DeedboxBuilder deedbox) => deedbox
        .Stream<PromiseState>("promise", stream => stream.EventsNestedIn(typeof(PromiseEvent)))
        .Projection<PromiseProjection>("promise_read_model", Run.Inline);

    public static WebApplication MapPromiseEndpoints(this WebApplication app)
    {
        // Every body here is a command of bounded text; none carries a document.
        var promises = app.MapGroup("/api/projects/{projectId}/promises")
            .RequireAuthorization()
            .WithBodyLimit(RequestBodyLimit.Default)
            .WithTags("Promises");

        promises.MapGet("/", PromiseReads.List).AllowAgent(AgentScope.Read).Produces<Page<PromiseSummaryDto>>();
        promises.MapPost("/", PromiseWrites.Create).AllowAgent(AgentScope.Propose)
            .Accepts<PromiseRequest>("application/json")
            .Produces<PromiseDto>(StatusCodes.Status201Created)
            .Produces<ErrorResponse>(StatusCodes.Status413PayloadTooLarge);

        promises.MapGet("/prefix", PromiseReads.Prefix).AllowAgent(AgentScope.Read).Produces<PrefixDto>();
        promises.MapPut("/prefix", PromiseWrites.SetPrefix).RequireHuman()
            .Accepts<SetPrefixRequest>("application/json")
            .Produces<PrefixDto>()
            .Produces<ErrorResponse>(StatusCodes.Status409Conflict);

        promises.MapGet("/{promiseId:guid}", PromiseReads.Get).AllowAgent(AgentScope.Read).Produces<PromiseDto>();
        promises.MapPut("/{promiseId:guid}", PromiseWrites.Update).AllowAgent(AgentScope.Propose)
            .Accepts<PromiseRequest>("application/json")
            .Produces<PromiseDto>()
            .Produces<ErrorResponse>(StatusCodes.Status413PayloadTooLarge);
        promises.MapDelete("/{promiseId:guid}", PromiseWrites.Delete).AllowAgent(AgentScope.Propose).Produces(StatusCodes.Status204NoContent);

        promises.MapPost("/{promiseId:guid}/revise", PromiseWrites.Revise).AllowAgent(AgentScope.Propose)
            .Accepts<ReviseRequest>("application/json")
            .Produces<PromiseDto>();
        promises.MapPost("/{promiseId:guid}/approve", PromiseWrites.Approve).RequireHuman()
            .Accepts<ApproveRequest>("application/json")
            .Produces<PromiseDto>()
            .Produces<RevisionChangedResponse>(StatusCodes.Status409Conflict)
            .Produces<ErrorResponse>(StatusCodes.Status403Forbidden);

        promises.MapGet("/{promiseId:guid}/runs", PromiseReads.Runs).AllowAgent(AgentScope.Read).Produces<Page<VerificationRun>>();

        var obligations = promises.MapGroup("/{promiseId:guid}/obligations");
        obligations.MapPost("/", ObligationWrites.Add).AllowAgent(AgentScope.Propose)
            .Accepts<ObligationRequest>("application/json")
            .Produces<PromiseDto>(StatusCodes.Status201Created);
        obligations.MapPost("/generate", ObligationWrites.Generate).AllowAgent(AgentScope.Propose).Produces<GenerateObligationsResponse>();
        obligations.MapPut("/{obligationKey}", ObligationWrites.Change).AllowAgent(AgentScope.Propose)
            .Accepts<ObligationRequest>("application/json")
            .Produces<PromiseDto>();
        obligations.MapDelete("/{obligationKey}", ObligationWrites.Remove).AllowAgent(AgentScope.Propose).Produces<PromiseDto>();
        obligations.MapPost("/{obligationKey}/run", ObligationWrites.Run).AllowAgent(AgentScope.Propose)
            .Accepts<RunObligationRequest>("application/json")
            .Produces<VerificationRun>()
            .Produces<ErrorResponse>(StatusCodes.Status429TooManyRequests)
            .RequireRateLimiting(PromiseLimits.RunPolicy);
        obligations.MapPost("/{obligationKey}/evidence", ObligationWrites.Evidence).AllowAgent(AgentScope.Propose)
            .Accepts<RecordEvidenceRequest>("application/json")
            .Produces<VerificationRun>()
            .Produces<ErrorResponse>(StatusCodes.Status429TooManyRequests)
            .RequireRateLimiting(PromiseLimits.RunPolicy);

        return app;
    }
}
