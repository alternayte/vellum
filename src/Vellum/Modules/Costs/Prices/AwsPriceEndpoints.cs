using System.Security.Claims;
using Vellum.Modules.Workspaces;
using Vellum.Shared;
using static Vellum.Shared.ResultExtensions;

namespace Vellum.Modules.Costs.Prices;

/// <summary>
/// The rows a person picked, by rate code. It names the rows and carries no number: Vellum
/// reads the offer file again and stores what that file holds.
/// </summary>
public sealed record AwsImportRequest(string Service, string Region, IReadOnlyList<string>? RateCodes);

public static class AwsPriceEndpoints
{
    private const string RefusedHint = "Pick a service and a region from the lists. The codes are case sensitive.";

    public static async Task<IResult> Services(
        Guid workspaceId, ClaimsPrincipal user, WorkspaceAuthorizationService auth, AwsPriceList aws, CancellationToken ct)
    {
        await auth.RequireRoleAsync(workspaceId, user.UserId(), WorkspaceRole.Owner, ct);
        var result = await aws.ServicesAsync(workspaceId, ct);
        return result.Failure is { } failure ? PriceSources.Refuse("AWS", failure, RefusedHint) : Results.Ok(result.Value);
    }

    public static async Task<IResult> Regions(
        Guid workspaceId, string? service, ClaimsPrincipal user, WorkspaceAuthorizationService auth, AwsPriceList aws, CancellationToken ct)
    {
        await auth.RequireRoleAsync(workspaceId, user.UserId(), WorkspaceRole.Owner, ct);
        if (!AwsPriceList.IsServiceCode(service))
            return Invalid("service", "Must be the offer code of a service, for example AmazonEC2 or AWSLambda.");
        var result = await aws.RegionsAsync(workspaceId, service!, ct);
        return result.Failure is { } failure ? PriceSources.Refuse("AWS", failure, RefusedHint) : Results.Ok(result.Value);
    }

    /// <summary>
    /// The on-demand prices of one service in one region whose product holds every word of
    /// <paramref name="search"/> (in the SKU, the product family or an attribute). Nothing is
    /// stored and nothing is picked.
    /// </summary>
    public static async Task<IResult> Search(
        Guid workspaceId, string? service, string? region, string? search,
        ClaimsPrincipal user, WorkspaceAuthorizationService auth, AwsPriceList aws, CancellationToken ct)
    {
        await auth.RequireRoleAsync(workspaceId, user.UserId(), WorkspaceRole.Owner, ct);
        if (Check(service, region) is { Count: > 0 } errors)
            return Invalid(errors);
        if (TextRule.Line("search", search, 100) is { } error)
            return Invalid(error);

        var words = (search ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var result = await aws.PricesAsync(workspaceId, service!, region!, new AwsOfferFilter(words), ct);
        return result.Failure is { } failure ? PriceSources.Refuse("AWS", failure, RefusedHint) : Results.Ok(result.Value);
    }

    /// <summary>
    /// Stores the picked rows as fetched price entries, all or none. Each rate code must name
    /// a row of the offer file that Vellum can store; a row with tiers cannot be stored.
    /// </summary>
    public static async Task<IResult> Import(
        Guid workspaceId, AwsImportRequest request,
        ClaimsPrincipal user, WorkspaceAuthorizationService auth, AwsPriceList aws, AppDbContext db, TimeProvider time, CancellationToken ct)
    {
        var userId = user.UserId();
        await auth.RequireRoleAsync(workspaceId, userId, WorkspaceRole.Owner, ct);

        var errors = Check(request.Service, request.Region);
        var rateCodes = (request.RateCodes ?? []).Distinct(StringComparer.Ordinal).ToList();
        if (rateCodes.Count is 0 or > AwsPriceList.MaxImport)
            errors.Add(new("rateCodes", $"Pick 1 to {AwsPriceList.MaxImport} rows."));
        else if (rateCodes.FirstOrDefault(code => !AwsPriceList.IsRateCode(code)) is not null)
            errors.Add(new("rateCodes", "Each must be the rate code of a row from the search."));
        if (errors.Count > 0)
            return Invalid(errors);

        var skus = rateCodes.Select(code => code[..code.IndexOf('.', StringComparison.Ordinal)]).ToHashSet(StringComparer.Ordinal);
        var result = await aws.PricesAsync(workspaceId, request.Service, request.Region, new AwsOfferFilter([], skus), ct);
        if (result.Failure is { } failure)
            return PriceSources.Refuse("AWS", failure, RefusedHint);

        var page = result.Value!;
        var picked = new List<AwsPrice>();
        foreach (var code in rateCodes)
        {
            var row = page.Items.FirstOrDefault(r => r.RateCode == code);
            if (row is null)
                return PriceSources.NotImportable($"The offer file has no row with the rate code {code}", "Nothing was stored. Search again and pick a row.");
            if (row.NotImportable is { } why)
                return PriceSources.NotImportable($"The row {code} cannot be stored", $"Nothing was stored. {why}");
            picked.Add(row);
        }

        var now = time.GetUtcNow();
        var entries = picked.Select(row => new PriceEntryEntity
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            Provider = CostProvider.Aws,
            Region = page.Region,
            Sku = Name(row),
            Description = row.Description.Length > 0 ? row.Description : null,
            Unit = row.Unit,
            UnitPrice = row.PricePerUnit,
            Currency = row.Currency,
            // The provider's date when it gives one. Else the price is known to hold on the day it was read.
            EffectiveDate = row.EffectiveDate ?? DateOnly.FromDateTime(now.UtcDateTime),
            Source = PriceSourceKind.Fetched,
            AcceptedBy = userId,
            AcceptedAt = now,
            SourceUrl = page.SourceUrl,
            FetchedAt = now,
            MeterId = row.RateCode,
            ProviderEffectiveDate = row.EffectiveDate,
        }).ToList();

        if (await PriceBook.AddManyAsync(db, workspaceId, entries, PriceAudit.Imported, ct) is { } refusal)
            return refusal;
        return Results.Created($"/api/workspaces/{workspaceId}/prices", entries.Select(e => PriceEntryDto.From(e)).ToList());
    }

    /// <summary>The name a person knows a product by: the instance type, else the usage type, else the SKU.</summary>
    private static string Name(AwsPrice row) =>
        row.Attributes.GetValueOrDefault("instanceType") ?? row.Attributes.GetValueOrDefault("usagetype") ?? row.Sku;

    private static List<ValidationError> Check(string? service, string? region)
    {
        var errors = new List<ValidationError>();
        if (!AwsPriceList.IsServiceCode(service))
            errors.Add(new("service", "Must be the offer code of a service, for example AmazonEC2 or AWSLambda."));
        if (!AwsPriceList.IsRegionCode(region))
            errors.Add(new("region", "Must be a region code, for example eu-west-1."));
        return errors;
    }
}
