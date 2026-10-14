using Deedbox;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.OpenApi;
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
        services.AddScoped<PromiseReader>();
        services.AddScoped<IProjectRetention, PromiseRetention>();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(PromiseLimits.Default);

        services.TryAddSingleton<PromiseRunRate>();
        services.TryAddSingleton<PromiseDraftRate>();
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
            .Produces<ErrorResponse>(StatusCodes.Status413PayloadTooLarge)
            .Produces<ErrorResponse>(StatusCodes.Status429TooManyRequests)
            .AddEndpointFilter(CallerRate.Filter<PromiseDraftRate>);

        promises.MapGet("/prefix", PromiseReads.Prefix).AllowAgent(AgentScope.Read).Produces<PrefixDto>();
        promises.MapPut("/prefix", PromiseWrites.SetPrefix).RequireHuman()
            .Accepts<SetPrefixRequest>("application/json")
            .Produces<PrefixDto>()
            .Produces<ErrorResponse>(StatusCodes.Status409Conflict);

        promises.MapGet("/{promiseId:guid}", PromiseReads.Get).AllowAgent(AgentScope.Read).Produces<PromiseDto>();
        promises.MapPut("/{promiseId:guid}", PromiseWrites.Update).AllowAgent(AgentScope.Propose)
            .Accepts<PromiseRequest>("application/json")
            .Produces<PromiseDto>()
            .Produces<ErrorResponse>(StatusCodes.Status413PayloadTooLarge)
            .Produces<ErrorResponse>(StatusCodes.Status429TooManyRequests)
            .AddEndpointFilter(CallerRate.Filter<PromiseDraftRate>);
        // An agent token deletes only a draft promise that agent tokens alone wrote (403 human_only otherwise).
        promises.MapDelete("/{promiseId:guid}", PromiseWrites.Delete).AllowAgent(AgentScope.Propose)
            .Produces(StatusCodes.Status204NoContent)
            .Produces<ErrorResponse>(StatusCodes.Status403Forbidden);

        // A new revision is a draft, also one that retires: it does nothing until a person approves it.
        promises.MapPost("/{promiseId:guid}/revise", PromiseWrites.Revise).AllowAgent(AgentScope.Propose)
            .Accepts<ReviseRequest>("application/json")
            .Produces<PromiseDto>();
        promises.MapPost("/{promiseId:guid}/approve", PromiseWrites.Approve).RequireHuman()
            .Accepts<ApproveRequest>("application/json")
            .Produces<PromiseDto>()
            .Produces<RevisionChangedResponse>(StatusCodes.Status409Conflict)
            .Produces<ErrorResponse>(StatusCodes.Status403Forbidden);

        promises.MapGet("/{promiseId:guid}/runs", PromiseReads.Runs).AllowAgent(AgentScope.Read).Produces<RunsPage>();

        var obligations = promises.MapGroup("/{promiseId:guid}/obligations");
        obligations.MapPost("/", ObligationWrites.Add).AllowAgent(AgentScope.Propose)
            .Accepts<ObligationRequest>("application/json")
            .Produces<PromiseDto>(StatusCodes.Status201Created)
            .Produces<ErrorResponse>(StatusCodes.Status429TooManyRequests)
            .AddEndpointFilter(CallerRate.Filter<PromiseDraftRate>);
        obligations.MapPost("/generate", ObligationWrites.Generate).AllowAgent(AgentScope.Propose)
            .Produces<GenerateObligationsResponse>()
            .Produces<ErrorResponse>(StatusCodes.Status429TooManyRequests)
            .AddEndpointFilter(CallerRate.Filter<PromiseDraftRate>);
        // An agent token changes or removes only an obligation that an agent token added to the
        // draft and that no person changed since (403 human_only otherwise).
        obligations.MapPut("/{obligationKey}", ObligationWrites.Change).AllowAgent(AgentScope.Propose)
            .Accepts<ObligationRequest>("application/json")
            .Produces<PromiseDto>()
            .Produces<ErrorResponse>(StatusCodes.Status403Forbidden)
            .Produces<ErrorResponse>(StatusCodes.Status429TooManyRequests)
            .AddEndpointFilter(CallerRate.Filter<PromiseDraftRate>);
        obligations.MapDelete("/{obligationKey}", ObligationWrites.Remove).AllowAgent(AgentScope.Propose)
            .Produces<PromiseDto>()
            .Produces<ErrorResponse>(StatusCodes.Status403Forbidden);
        obligations.MapPost("/{obligationKey}/run", ObligationWrites.Run).AllowAgent(AgentScope.Propose)
            .Accepts<RunObligationRequest>("application/json")
            .Produces<VerificationRun>()
            .Produces<ErrorResponse>(StatusCodes.Status429TooManyRequests)
            .AddEndpointFilter(CallerRate.Filter<PromiseRunRate>);
        obligations.MapPost("/{obligationKey}/evidence", ObligationWrites.Evidence).AllowAgent(AgentScope.Propose)
            .Accepts<RecordEvidenceRequest>("application/json")
            .Produces<VerificationRun>()
            .Produces<ErrorResponse>(StatusCodes.Status429TooManyRequests)
            .AddEndpointFilter(CallerRate.Filter<PromiseRunRate>);

        // The change impact of a scan reads the import, the scenarios and the promises. It
        // is with the imports in the API, and for a signed-in session like every import read.
        app.MapGet("/api/projects/{projectId}/imports/{importId:guid}/impact", ChangeImpact.Handle)
            .RequireAuthorization()
            .WithTags("Imports")
            .Produces<ChangeImpactDto>()
            .Produces<ErrorResponse>(StatusCodes.Status404NotFound)
            .Produces<ErrorResponse>(StatusCodes.Status409Conflict);

        // The compact report for a pull request or a CI job: Markdown, or JSON for Accept:
        // application/json. A read token reads it; Vellum sends it nowhere.
        app.MapGet("/api/projects/{projectId}/imports/{importId:guid}/impact/report", ImpactReport.Handle)
            .RequireAuthorization()
            .AllowAgent(AgentScope.Read)
            .WithTags("Imports")
            .Produces<ImpactReportDto>(StatusCodes.Status200OK, "application/json")
            // The same 200 is Markdown without Accept: application/json.
            .AddOpenApiOperationTransformer((operation, _, _) =>
            {
                if (operation.Responses?.TryGetValue("200", out var ok) == true && ok.Content is { } content)
                    content["text/markdown"] = new OpenApiMediaType { Schema = new OpenApiSchema { Type = JsonSchemaType.String } };
                return Task.CompletedTask;
            })
            .Produces<ErrorResponse>(StatusCodes.Status404NotFound)
            .Produces<ErrorResponse>(StatusCodes.Status409Conflict);

        // The CI recipe runs the checks of approved promises against the scan it started, then reads the report.
        app.MapPost("/api/projects/{projectId}/imports/{importId:guid}/impact/checks", ObligationWrites.RunForScan)
            .RequireAuthorization()
            .AllowAgent(AgentScope.Propose, AgentScope.ScanPullRequest)
            .WithBodyLimit(RequestBodyLimit.Default)
            .WithTags("Imports")
            .Produces<ScanChecksResponse>()
            .Produces<ErrorResponse>(StatusCodes.Status403Forbidden)
            .Produces<ErrorResponse>(StatusCodes.Status404NotFound)
            .Produces<ErrorResponse>(StatusCodes.Status409Conflict)
            .Produces<ErrorResponse>(StatusCodes.Status429TooManyRequests);

        var gate = app.MapGroup("/api/projects/{projectId}/impact-gate")
            .RequireAuthorization()
            .WithBodyLimit(RequestBodyLimit.Default)
            .WithTags("Promises");
        gate.MapGet("/", ImpactGate.Get).AllowAgent(AgentScope.Read).Produces<ImpactGateSettingDto>();
        // A policy that can fail a build is for an owner, never for a token.
        gate.MapPut("/", ImpactGate.Set).RequireHuman()
            .Accepts<SetImpactGateRequest>("application/json")
            .Produces<ImpactGateSettingDto>()
            .Produces<ErrorResponse>(StatusCodes.Status400BadRequest);

        return app;
    }
}

/// <summary>
/// The rate limit of runs and evidence. Every run and every piece of evidence is an event of
/// a promise stream, so one caller records a bounded number of them in a minute, whichever
/// promise they are for.
/// </summary>
public sealed class PromiseRunRate(PromiseLimits limits) : CallerRate(limits.RunsPerMinute);

/// <summary>
/// The rate limit of draft writes: creating a promise, changing a draft, adding an obligation
/// and generating obligations. Each is an event of a promise stream, and a draft holds no
/// bound on how often it changes, so one caller makes a bounded number of them in a minute,
/// a person and an agent token alike.
/// </summary>
internal sealed class PromiseDraftRate(PromiseLimits limits) : CallerRate(limits.DraftWritesPerMinute);
