using Deedbox;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Vellum.Modules.Identity.AgentTokens;
using Vellum.Shared;

namespace Vellum.Modules.LoadTests;

/// <summary>
/// k6 evidence (SDD §8): load-test targets with a person's authorisation, k6 scripts generated
/// from valid scenarios, and the import of measured results. Vellum writes the script and
/// reads the result. It does not run k6 and sends no request to a target.
/// </summary>
public static class LoadTestsModule
{
    public static IServiceCollection AddLoadTestsModule(this IServiceCollection services)
    {
        // The connection string is read when the context is first used, so configuration that
        // a test host overrides applies.
        services.AddDbContext<LoadTestsDbContext>((provider, options) =>
            options.UseNpgsql(provider.GetRequiredService<IConfiguration>().GetConnectionString("DefaultConnection"))
                   .UseSnakeCaseNamingConvention());
        services.TryAddSingleton(TimeProvider.System);
        return services;
    }

    /// <summary>One stream per target, keyed by the target ID.</summary>
    public static DeedboxBuilder AddLoadTestStreams(this DeedboxBuilder deedbox) => deedbox
        .Stream<TargetState>("load_test_target", stream => stream.EventsNestedIn(typeof(TargetEvent)))
        .Projection<TargetProjection>("load_test_target_read_model", Run.Inline);

    public static WebApplication MapLoadTestEndpoints(this WebApplication app)
    {
        var loadTests = app.MapGroup("/api/projects/{projectId}/load-tests")
            .RequireAuthorization()
            .WithBodyLimit(RequestBodyLimit.Default)
            .WithTags("LoadTests");

        loadTests.MapGet("/targets", Targets.List).AllowAgent(AgentScope.Read, AgentScope.Export).Produces<IReadOnlyList<TargetDto>>();
        loadTests.MapPost("/targets", Targets.Add).Produces<TargetDto>(StatusCodes.Status201Created);
        loadTests.MapPut("/targets/{targetId:guid}", Targets.Change).Produces<TargetDto>();
        loadTests.MapDelete("/targets/{targetId:guid}", Targets.Remove).Produces(StatusCodes.Status204NoContent);

        // A person with the Owner role allows load against a system. No agent token does, with
        // any scope: a token that could authorise a target could aim a script at any URL.
        loadTests.MapPost("/targets/{targetId:guid}/authorise", Targets.Authorise).RequireHuman()
            .Produces<TargetDto>()
            .Produces<ErrorResponse>(StatusCodes.Status409Conflict);
        loadTests.MapDelete("/targets/{targetId:guid}/authorisation", Targets.Revoke).RequireHuman().Produces<TargetDto>();
        loadTests.MapPut("/targets/{targetId:guid}/override", Targets.SetOverride).RequireHuman().Produces<TargetDto>();

        // The script is an export of the model: text that the caller takes away and runs.
        loadTests.MapPost("/scripts", Scripts.Generate).AllowAgent(AgentScope.Export)
            .Produces<ScriptDto>()
            .Produces<ScriptDto>(StatusCodes.Status201Created)
            .Produces<ScriptRefusedResponse>(StatusCodes.Status422UnprocessableEntity);
        loadTests.MapGet("/scripts", Scripts.List).AllowAgent(AgentScope.Read, AgentScope.Export).Produces<IReadOnlyList<ScriptDto>>();
        loadTests.MapGet("/scripts/{scriptId:guid}", Scripts.Get).AllowAgent(AgentScope.Read, AgentScope.Export).Produces<ScriptDto>();

        // An agent may submit evidence (SDD §6), so a token with the propose scope may send a
        // result file, as it may record evidence for a promise. The token gets no way around
        // the checks: the same identity check runs for every caller, a result is bound to a
        // script for a target that a person authorised, and the row names the token. Sending a
        // file marks no value of a profile as measured: only an Editor's save of the profile does,
        // and that save takes the numbers from the result (MeasuredServiceTimes).
        loadTests.MapPost("/results", MeasuredResults.Import).AllowAgent(AgentScope.Propose)
            .Accepts<object>("application/json")
            .Produces<MeasuredResultDto>(StatusCodes.Status201Created)
            .Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
            .Produces<ErrorResponse>(StatusCodes.Status409Conflict)
            .Produces<ErrorResponse>(StatusCodes.Status413PayloadTooLarge)
            .Produces<ResultRefusedResponse>(StatusCodes.Status422UnprocessableEntity)
            .WithBodyLimit(ResultFile.MaxBytes);
        loadTests.MapGet("/results", MeasuredResults.List).AllowAgent(AgentScope.Read).Produces<IReadOnlyList<MeasuredResultSummaryDto>>();
        loadTests.MapGet("/results/{resultId:guid}", MeasuredResults.Get).AllowAgent(AgentScope.Read).Produces<MeasuredResultDto>();
        loadTests.MapDelete("/results/{resultId:guid}", MeasuredResults.Delete)
            .Produces(StatusCodes.Status204NoContent)
            .Produces<ErrorResponse>(StatusCodes.Status409Conflict);

        loadTests.MapGet("/compare/estimate", Comparisons.Estimate).AllowAgent(AgentScope.Read).Produces<EstimateComparisonDto>();
        loadTests.MapGet("/compare/results", Comparisons.Regression).AllowAgent(AgentScope.Read).Produces<RegressionDto>();

        return app;
    }
}

/// <summary>Load-test data does not outlive its project.</summary>
public static class LoadTestRetention
{
    /// <summary>
    /// Deletes the results, the scripts and the targets of a project. The row of a target goes
    /// with its stream. A delete that is cut short leaves rows and streams that no request
    /// reaches: every load-test endpoint resolves the project first.
    /// </summary>
    public static async Task DeleteProjectAsync(LoadTestsDbContext db, IEventStore store, Guid projectId, CancellationToken ct)
    {
        // One unit under the lock of the load-test writes: an import that started before the project went
        // cannot add a result between the two deletes, which would keep its script from being deleted.
        await using (var transaction = await db.Database.BeginTransactionAsync(ct))
        {
            await Targets.LockProjectAsync(db.Database, projectId, ct);
            await db.Results.Where(r => r.ProjectId == projectId).ExecuteDeleteAsync(ct);
            await db.Scripts.Where(s => s.ProjectId == projectId).ExecuteDeleteAsync(ct);
            await transaction.CommitAsync(ct);
        }

        foreach (var targetId in await db.Targets.AsNoTracking().Where(t => t.ProjectId == projectId).Select(t => t.Id).ToListAsync(ct))
            await store.DeleteStream(StreamId.From(targetId), ct);
        await db.Targets.Where(t => t.ProjectId == projectId).ExecuteDeleteAsync(ct);
    }
}
