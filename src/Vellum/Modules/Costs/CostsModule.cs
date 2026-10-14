using System.Threading.RateLimiting;
using Deedbox;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Vellum.Modules.Costs.Prices;
using Vellum.Shared;
using Vellum.Shared.Outbound;

namespace Vellum.Modules.Costs;

/// <summary>
/// Deployment resources, workload assumptions, the price book and monthly infrastructure
/// estimates (SDD §3 Cost and Compare, §8). Vellum holds no price of its own: a number in an
/// estimate comes from a price entry or from an assumption, and a line without both is
/// unknown. Engineering cost is not modelled.
/// </summary>
public static class CostsModule
{
    /// <summary>The rate limit policy for calls that make Vellum call a provider's price API.</summary>
    public const string PriceFetchPolicy = "price-fetch";
    public const int PriceFetchPermits = 30;
    public static readonly TimeSpan PriceFetchWindow = TimeSpan.FromMinutes(1);

    public static IServiceCollection AddCostsModule(this IServiceCollection services)
    {
        // The connection string is read when the context is first used, so configuration that
        // a test host overrides applies.
        services.AddOutboundClient<IAzureRetailPricesApi>();
        services.AddScoped<AzureRetailPrices>();
        services.AddScoped<EstimateReader>();
        services.AddScoped<IProjectRetention, CostRetention>();
        services.TryAddSingleton(TimeProvider.System);

        // One window for each workspace: the price API is a shared public service, and every
        // search is a request from this server's address.
        services.Configure<RateLimiterOptions>(options => options.AddPolicy(PriceFetchPolicy, http =>
            RateLimitPartition.GetFixedWindowLimiter(
                "workspace:" + http.GetRouteValue("workspaceId"),
                _ => new FixedWindowRateLimiterOptions { PermitLimit = PriceFetchPermits, Window = PriceFetchWindow, QueueLimit = 0 })));
        return services;
    }

    /// <summary>One stream per cost sheet: the sheet of one model branch of one project.</summary>
    public static DeedboxBuilder AddCostStreams(this DeedboxBuilder deedbox) => deedbox
        .Stream<CostSheetState>("cost_sheet", stream => stream
            .EventsNestedIn(typeof(CostEvent))
            // Version 2 of the state added what a sheet held right after its last copy.
            .StateVersion(2))
        .Projection<CostSheetProjection>("cost_sheet_index", Run.Inline);

    public static WebApplication MapCostEndpoints(this WebApplication app)
    {
        // Like contracts: a viewer reads, an editor writes, and ?branchId= names a draft or a variant.
        var costs = app.MapGroup("/api/projects/{projectId}/costs")
            .RequireAuthorization()
            .WithTags("Costs");

        costs.MapGet("/", CostSheets.Get).Produces<CostSheetDto>();
        costs.MapPost("/copy", CostSheets.Copy).Produces<CostSheetDto>();

        costs.MapPost("/resources", CostSheets.AddResource).Produces<CostSheetDto>(StatusCodes.Status201Created);
        costs.MapPut("/resources/{resourceId:guid}", CostSheets.ChangeResource).Produces<CostSheetDto>();
        costs.MapDelete("/resources/{resourceId:guid}", CostSheets.RemoveResource).Produces<CostSheetDto>();

        costs.MapPost("/assumptions", CostSheets.AddAssumption).Produces<CostSheetDto>(StatusCodes.Status201Created);
        costs.MapPut("/assumptions/{assumptionId:guid}", CostSheets.ChangeAssumption).Produces<CostSheetDto>();
        costs.MapDelete("/assumptions/{assumptionId:guid}", CostSheets.RemoveAssumption).Produces<CostSheetDto>();

        costs.MapGet("/estimate", Estimates.Live).Produces<EstimateDto>();
        costs.MapGet("/compare", Estimates.Compare).Produces<ComparisonDto>();

        costs.MapGet("/estimates", Estimates.List).Produces<IReadOnlyList<SavedEstimateDto>>();
        costs.MapPost("/estimates", Estimates.Save).Produces<SavedEstimateDto>(StatusCodes.Status201Created);
        costs.MapGet("/estimates/{estimateId:guid}", Estimates.Get).Produces<SavedEstimateDto>();
        costs.MapDelete("/estimates/{estimateId:guid}", Estimates.Delete).Produces(StatusCodes.Status204NoContent);

        costs.MapGet("/prices", Pins.Prices).Produces<ProjectPricesDto>();
        costs.MapPut("/pins/{priceId:guid}", Pins.Pin).Produces(StatusCodes.Status204NoContent);
        costs.MapDelete("/pins/{priceId:guid}", Pins.Unpin).Produces(StatusCodes.Status204NoContent);

        var prices = app.MapGroup("/api/workspaces/{workspaceId}/prices")
            .RequireAuthorization()
            .WithTags("Prices");

        prices.MapGet("/", PriceBook.List).Produces<Page<PriceEntryDto>>();
        prices.MapPost("/", PriceBook.AddCustom).Produces<PriceEntryDto>(StatusCodes.Status201Created);
        prices.MapDelete("/{priceId:guid}", PriceBook.Delete).Produces(StatusCodes.Status204NoContent);

        prices.MapGet("/azure/meters", AzurePriceEndpoints.Search)
            .RequireRateLimiting(PriceFetchPolicy)
            .Produces<AzureMeterPage>()
            .Produces<ErrorResponse>(StatusCodes.Status502BadGateway);
        prices.MapPost("/azure", AzurePriceEndpoints.Import)
            .RequireRateLimiting(PriceFetchPolicy)
            .Produces<PriceEntryDto>(StatusCodes.Status201Created)
            .Produces<ErrorResponse>(StatusCodes.Status502BadGateway);

        return app;
    }
}

/// <summary>Cost data does not outlive its project.</summary>
public sealed class CostRetention(AppDbContext db, IEventStore store) : IProjectRetention
{
    /// <summary>
    /// Deletes the saved estimates, the pins and the cost sheets of a project. The sheet and
    /// the saved estimates of a draft go earlier, when the draft is merged or abandoned (see
    /// <see cref="CostBranches.CloseAsync"/>). The index row
    /// of a sheet goes after its stream. A delete that is cut short leaves rows and streams
    /// that no request reaches: every cost endpoint resolves the project first.
    /// </summary>
    public async Task DeleteProjectAsync(DeletedProject project, CancellationToken ct)
    {
        var projectId = project.Id;
        await db.Estimates.Where(e => e.ProjectId == projectId).ExecuteDeleteAsync(ct);
        await db.Pins.Where(p => p.ProjectId == projectId).ExecuteDeleteAsync(ct);

        foreach (var sheetId in await db.Sheets.AsNoTracking().Where(s => s.ProjectId == projectId).Select(s => s.Id).ToListAsync(ct))
        {
            await store.DeleteStream(StreamId.From(sheetId), ct);
            await db.Sheets.Where(s => s.Id == sheetId).ExecuteDeleteAsync(ct);
        }
    }
}
