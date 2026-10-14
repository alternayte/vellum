using System.Security.Claims;
using Vellum.Modules.Settings.Document;
using Vellum.Modules.Workspaces.Authorization;
using Vellum.Shared;
using static Vellum.Shared.ResultExtensions;

namespace Vellum.Modules.Costs.Prices;

/// <summary>
/// The row a person picked. It names the row and carries no number: Vellum reads the row
/// again from the API and stores what that answer holds.
/// </summary>
public sealed record AzureImportRequest(string MeterId, string? SkuId, string? ArmRegionName, string Currency);

public static class AzurePriceEndpoints
{
    /// <summary>
    /// Searches the consumption meters of one Azure service. Nothing is stored and nothing is
    /// picked: the caller shows the rows and a person picks one.
    /// </summary>
    public static async Task<IResult> Search(
        Guid workspaceId, string? serviceName, string? region, string? skuName, string? meterName, string? currency,
        ClaimsPrincipal user, WorkspaceAuthorizationService auth, AzureRetailPrices azure, SettingsResolver settings, CancellationToken ct)
    {
        await auth.RequireRoleAsync(workspaceId, user.UserId(), WorkspaceRole.Owner, ct);

        // Without a currency the search uses the currency of the workspace.
        currency ??= (await settings.GetWorkspaceAsync(workspaceId, ct)).Effective.Cost.Currency;
        var errors = new List<ValidationError>();
        if (string.IsNullOrWhiteSpace(serviceName))
            errors.Add(new("serviceName", "A service name is required, for example 'Virtual Machines'. The name is case sensitive."));
        foreach (var (field, value) in new[] { ("serviceName", serviceName), ("region", region), ("skuName", skuName), ("meterName", meterName) })
        {
            if (!string.IsNullOrEmpty(value) && !AzureRetailPrices.IsFilterValue(value))
                errors.Add(new(field, $"Must be at most {AzureRetailPrices.MaxFilterValueChars} characters with no single quote, no control character and no space at either end."));
        }
        if (!AzureRetailPrices.IsCurrency(currency))
            errors.Add(new("currency", "Must be a three-letter currency code in upper case, for example USD."));
        if (errors.Count > 0)
            return Invalid(errors);

        var filter = AzureRetailPrices.Filter(new AzureMeterQuery(serviceName, region, skuName, meterName))!;
        var result = await azure.SearchAsync(workspaceId, filter, currency!, ct);
        return result.Failure is { } failure ? Refuse(failure) : Results.Ok(result.Value);
    }

    /// <summary>
    /// Stores the picked row as a fetched price entry. The row must be the only importable
    /// row the API returns for the meter, the SKU and the region; else nothing is stored.
    /// A meter with price tiers has no importable row: its first tier is not its price.
    /// </summary>
    public static async Task<IResult> Import(
        Guid workspaceId, AzureImportRequest request,
        ClaimsPrincipal user, WorkspaceAuthorizationService auth, AzureRetailPrices azure, AppDbContext db, TimeProvider time, CancellationToken ct)
    {
        var userId = user.UserId();
        await auth.RequireRoleAsync(workspaceId, userId, WorkspaceRole.Owner, ct);

        var errors = new List<ValidationError>();
        if (string.IsNullOrEmpty(request.MeterId) || !AzureRetailPrices.IsFilterValue(request.MeterId))
            errors.Add(new("meterId", "The meter ID of a row from the search is required."));
        if (!string.IsNullOrEmpty(request.SkuId) && !AzureRetailPrices.IsFilterValue(request.SkuId))
            errors.Add(new("skuId", "Must be the SKU ID of a row from the search."));
        if (!string.IsNullOrEmpty(request.ArmRegionName) && !AzureRetailPrices.IsFilterValue(request.ArmRegionName))
            errors.Add(new("armRegionName", "Must be the region of a row from the search."));
        if (!AzureRetailPrices.IsCurrency(request.Currency))
            errors.Add(new("currency", "Must be a three-letter currency code in upper case, for example USD."));
        if (errors.Count > 0)
            return Invalid(errors);

        var filter = AzureRetailPrices.Filter(new AzureMeterQuery(
            ArmRegionName: request.ArmRegionName, MeterId: request.MeterId, SkuId: request.SkuId))!;
        var result = await azure.SearchAsync(workspaceId, filter, request.Currency, ct);
        if (result.Failure is { } failure)
            return Refuse(failure);

        var page = result.Value!;
        var matching = page.Items
            .Where(m => m.MeterId == request.MeterId
                        && (string.IsNullOrEmpty(request.SkuId) || m.SkuId == request.SkuId)
                        && (string.IsNullOrEmpty(request.ArmRegionName) || m.ArmRegionName == request.ArmRegionName))
            .ToList();
        var rows = matching.Where(m => m.NotImportable is null).ToList();
        if (rows.Count == 0 && matching.Any(m => m.NotImportable == AzureRetailPrices.Tiered))
        {
            return Results.Conflict(new ErrorResponse("price_tiered",
                "The price of this meter has tiers",
                "The price depends on the amount that is used, and Vellum does not model price tiers. Nothing was stored. Enter the price that you pay as a custom price."));
        }
        if (page.More || rows is not [var meter])
        {
            return Results.Conflict(new ErrorResponse("conflict",
                rows.Count == 0 ? "The price API returns no row that Vellum can store for this meter" : "The price API returns more than one row for this meter",
                "Nothing was stored. Search again and pick a row."));
        }

        var now = time.GetUtcNow();
        return await PriceBook.AddAsync(db, new PriceEntryEntity
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            Provider = CostProvider.Azure,
            Region = meter.ArmRegionName,
            Sku = meter.SkuName.Length > 0 ? meter.SkuName : meter.MeterName,
            Description = string.Join(" · ", new[] { meter.ProductName, meter.MeterName }.Where(part => part.Length > 0)) is { Length: > 0 } description ? description : null,
            Unit = meter.UnitOfMeasure,
            UnitPrice = meter.RetailPrice,
            Currency = meter.CurrencyCode,
            // The provider's date when it gives one. Else the price is known to hold on the day it was read.
            EffectiveDate = meter.EffectiveStartDate ?? DateOnly.FromDateTime(now.UtcDateTime),
            Source = PriceSourceKind.Fetched,
            AcceptedBy = userId,
            AcceptedAt = now,
            SourceUrl = page.SourceUrl,
            FetchedAt = now,
            MeterId = meter.MeterId,
            ProviderEffectiveDate = meter.EffectiveStartDate,
        }, PriceAudit.Imported, ct);
    }

    /// <summary>A failure of the price API is a 502 or a 409 with a fixed sentence. No text of the remote answer is passed on.</summary>
    private static IResult Refuse(AzurePricesFailure failure) => failure switch
    {
        AzurePricesFailure.NotConfigured notConfigured => Results.Conflict(new ErrorResponse(
            "price_source_not_configured", "The Azure price API has no usable address in the settings of this workspace",
            notConfigured.Reason ?? "Set cost.azureRetailPricesBaseUrl in the settings.")),
        AzurePricesFailure.BadFilter => Results.BadRequest(new ErrorResponse(
            "price_source_refused", "The Azure price API refused the search", "Check the service name, the region and the SKU name. The values are case sensitive.")),
        AzurePricesFailure.RateLimited => Results.Json(new ErrorResponse(
            "price_source_rate_limited", "The Azure price API limits the requests of this server", "Try again in a minute."),
            statusCode: StatusCodes.Status503ServiceUnavailable),
        AzurePricesFailure.Unavailable unavailable => Results.Json(new ErrorResponse(
            "price_source_unavailable", "The Azure price API gave no usable answer", Describe(unavailable.Reason)),
            statusCode: StatusCodes.Status502BadGateway),
        _ => throw new InvalidOperationException($"Unknown failure {failure.GetType().Name}"),
    };

    private static string Describe(string reason) => reason switch
    {
        "url_refused" => "The instance policy refuses the address of the price API.",
        "invalid_setting" => "The address of the price API in the settings cannot be used.",
        "timeout" => "The price API did not answer in time.",
        "network" => "No connection to the price API.",
        "server_error" => "The price API reported an error on its side.",
        "too_large" => "The answer of the price API is too large. Narrow the search.",
        "next_page_refused" => "The price API pointed at another server for the next page. Vellum did not follow it.",
        _ => "The answer of the price API is not the documented format.",
    };
}
