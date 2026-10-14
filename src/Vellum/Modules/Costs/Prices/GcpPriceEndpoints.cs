using System.Security.Claims;
using Vellum.Modules.Settings;
using Vellum.Modules.Workspaces;
using Vellum.Shared;
using static Vellum.Shared.ResultExtensions;

namespace Vellum.Modules.Costs.Prices;

/// <summary>
/// The SKUs a person picked. It names the SKUs and carries no number: Vellum reads the catalog
/// again and stores what it holds. <paramref name="Region"/> is the region of the entries; it
/// must be a region of each SKU. Leave it out for SKUs of one region or of no region.
/// </summary>
public sealed record GcpImportRequest(string ServiceId, string? Region, string Currency, IReadOnlyList<string>? SkuIds);

public static class GcpPriceEndpoints
{
    private const string RefusedHint =
        "Check the API key in the workspace secret gcp:apiKey, that the Cloud Billing API is turned on for its Google Cloud project, and the service.";

    public static async Task<IResult> Services(
        Guid workspaceId, ClaimsPrincipal user, WorkspaceAuthorizationService auth, GcpCloudBilling gcp, CancellationToken ct)
    {
        await auth.RequireRoleAsync(workspaceId, user.UserId(), WorkspaceRole.Owner, ct);
        var result = await gcp.ServicesAsync(workspaceId, ct);
        return result.Failure is { } failure ? PriceSources.Refuse("Google Cloud", failure, RefusedHint) : Results.Ok(result.Value);
    }

    /// <summary>
    /// The SKUs of one service in one region whose description, resource family, resource group
    /// or usage type holds every word of <paramref name="search"/>. Nothing is stored and nothing is picked.
    /// </summary>
    public static async Task<IResult> Search(
        Guid workspaceId, string? serviceId, string? region, string? search, string? currency,
        ClaimsPrincipal user, WorkspaceAuthorizationService auth, GcpCloudBilling gcp, SettingsResolver settings, CancellationToken ct)
    {
        await auth.RequireRoleAsync(workspaceId, user.UserId(), WorkspaceRole.Owner, ct);

        // Without a currency the search uses the currency of the workspace.
        currency ??= (await settings.GetWorkspaceAsync(workspaceId, ct)).Effective.Cost.Currency;
        var errors = Check(serviceId, region, currency);
        if (TextRule.Line("search", search, 100) is { } error)
            errors.Add(error);
        if (errors.Count > 0)
            return Invalid(errors);

        var words = (search ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var filter = new GcpSkuFilter(string.IsNullOrEmpty(region) ? null : region, words);
        var result = await gcp.SkusAsync(workspaceId, serviceId!, currency, filter, ct);
        return result.Failure is { } failure ? PriceSources.Refuse("Google Cloud", failure, RefusedHint) : Results.Ok(result.Value);
    }

    /// <summary>Stores the picked SKUs as fetched price entries, all or none.</summary>
    public static async Task<IResult> Import(
        Guid workspaceId, GcpImportRequest request,
        ClaimsPrincipal user, WorkspaceAuthorizationService auth, GcpCloudBilling gcp, AppDbContext db, TimeProvider time, CancellationToken ct)
    {
        var userId = user.UserId();
        await auth.RequireRoleAsync(workspaceId, userId, WorkspaceRole.Owner, ct);

        var errors = Check(request.ServiceId, request.Region, request.Currency);
        var skuIds = (request.SkuIds ?? []).Distinct(StringComparer.Ordinal).ToList();
        if (skuIds.Count is 0 or > GcpCloudBilling.MaxImport)
            errors.Add(new("skuIds", $"Pick 1 to {GcpCloudBilling.MaxImport} SKUs."));
        else if (skuIds.FirstOrDefault(id => !GcpCloudBilling.IsSkuId(id)) is not null)
            errors.Add(new("skuIds", "Each must be the SKU ID of a row from the search."));
        if (errors.Count > 0)
            return Invalid(errors);

        var result = await gcp.SkusAsync(workspaceId, request.ServiceId, request.Currency,
            new GcpSkuFilter(null, [], skuIds.ToHashSet(StringComparer.Ordinal)), ct);
        if (result.Failure is { } failure)
            return PriceSources.Refuse("Google Cloud", failure, RefusedHint);

        var page = result.Value!;
        var picked = new List<(GcpSku Sku, string Region)>();
        foreach (var id in skuIds)
        {
            var sku = page.Items.FirstOrDefault(s => s.SkuId == id);
            if (sku is null)
                return PriceSources.NotImportable($"The catalog has no SKU {id} with a price in {request.Currency}", "Nothing was stored. Search again and pick a row.");
            if (sku.NotImportable is { } why)
                return PriceSources.NotImportable($"The SKU {id} cannot be stored", $"Nothing was stored. {why}");

            string region;
            if (!string.IsNullOrEmpty(request.Region))
            {
                if (!sku.Regions.Contains(request.Region, StringComparer.OrdinalIgnoreCase))
                    return PriceSources.NotImportable($"The SKU {id} is not sold in {request.Region}", "Nothing was stored. Pick the region of the SKU.");
                region = request.Region;
            }
            else
            {
                region = sku.Regions is [var only] ? only : "";
            }
            picked.Add((sku, region));
        }

        var now = time.GetUtcNow();
        var entries = picked.Select(p => new PriceEntryEntity
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            Provider = CostProvider.Gcp,
            Region = p.Region,
            Sku = p.Sku.Description,
            Description = string.Join(" · ", new[] { p.Sku.ServiceDisplayName, p.Sku.ResourceGroup, p.Sku.UsageType }.Where(s => s.Length > 0)) is { Length: > 0 } d ? d : null,
            Unit = p.Sku.Unit,
            UnitPrice = p.Sku.UnitPrice,
            Currency = p.Sku.Currency,
            EffectiveDate = p.Sku.EffectiveDate ?? DateOnly.FromDateTime(now.UtcDateTime),
            Source = PriceSourceKind.Fetched,
            AcceptedBy = userId,
            AcceptedAt = now,
            SourceUrl = page.SourceUrl,
            FetchedAt = now,
            MeterId = p.Sku.SkuId,
            ProviderEffectiveDate = p.Sku.EffectiveDate,
        }).ToList();

        if (await PriceBook.AddManyAsync(db, workspaceId, entries, PriceAudit.Imported, ct) is { } refusal)
            return refusal;
        return Results.Created($"/api/workspaces/{workspaceId}/prices", entries.Select(e => PriceEntryDto.From(e)).ToList());
    }

    private static List<ValidationError> Check(string? serviceId, string? region, string? currency)
    {
        var errors = new List<ValidationError>();
        if (!GcpCloudBilling.IsServiceId(serviceId))
            errors.Add(new("serviceId", "Must be the ID of a service from the list, for example 6F81-5844-456A."));
        if (!string.IsNullOrEmpty(region) && !GcpCloudBilling.IsRegion(region))
            errors.Add(new("region", "Must be a region, for example europe-west1."));
        if (!PriceSources.IsCurrency(currency))
            errors.Add(new("currency", "Must be a three-letter currency code in upper case, for example USD."));
        return errors;
    }
}
