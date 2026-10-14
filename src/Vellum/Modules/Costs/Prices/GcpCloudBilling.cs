using System.Globalization;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Refit;
using Vellum.Modules.Settings.Document;
using Vellum.Modules.Settings.Secrets;
using Vellum.Shared.Outbound;

namespace Vellum.Modules.Costs.Prices;

/// <summary>
/// The Google Cloud Billing Catalog API (v1) as Google documents it at
/// https://cloud.google.com/billing/docs/reference/rest/v1/services/list and
/// https://cloud.google.com/billing/docs/reference/rest/v1/services.skus/list (read on
/// 2026-10-14). It takes an API key. The key goes in the <c>X-Goog-Api-Key</c> header, never
/// in the address, so no source URL, log line or audit entry holds it.
/// </summary>
[Headers("Accept: application/json", "User-Agent: Vellum")]
internal interface IGcpCloudBillingApi
{
    [Get("/v1/services")]
    Task<HttpResponseMessage> Services(
        int pageSize, string? pageToken, [Property(OutboundCall.PropertyName)] OutboundCall call, CancellationToken ct);

    [Get("/v1/services/{serviceId}/skus")]
    Task<HttpResponseMessage> Skus(
        string serviceId, string currencyCode, int pageSize, string? pageToken,
        [Property(OutboundCall.PropertyName)] OutboundCall call, CancellationToken ct);
}

internal sealed record GcpServicesDto(
    [property: JsonPropertyName("services")] List<GcpServiceDto?>? Services,
    [property: JsonPropertyName("nextPageToken")] string? NextPageToken);

internal sealed record GcpServiceDto(
    [property: JsonPropertyName("serviceId")] string? ServiceId,
    [property: JsonPropertyName("displayName")] string? DisplayName);

internal sealed record GcpSkusDto(
    [property: JsonPropertyName("skus")] List<GcpSkuDto?>? Skus,
    [property: JsonPropertyName("nextPageToken")] string? NextPageToken);

internal sealed record GcpSkuDto(
    [property: JsonPropertyName("skuId")] string? SkuId,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("category")] GcpCategoryDto? Category,
    [property: JsonPropertyName("serviceRegions")] List<string?>? ServiceRegions,
    [property: JsonPropertyName("pricingInfo")] List<GcpPricingInfoDto?>? PricingInfo);

internal sealed record GcpCategoryDto(
    [property: JsonPropertyName("serviceDisplayName")] string? ServiceDisplayName,
    [property: JsonPropertyName("resourceFamily")] string? ResourceFamily,
    [property: JsonPropertyName("resourceGroup")] string? ResourceGroup,
    [property: JsonPropertyName("usageType")] string? UsageType);

internal sealed record GcpPricingInfoDto(
    [property: JsonPropertyName("pricingExpression")] GcpPricingExpressionDto? PricingExpression,
    [property: JsonPropertyName("effectiveTime")] DateTimeOffset? EffectiveTime);

internal sealed record GcpPricingExpressionDto(
    [property: JsonPropertyName("usageUnit")] string? UsageUnit,
    [property: JsonPropertyName("usageUnitDescription")] string? UsageUnitDescription,
    [property: JsonPropertyName("tieredRates")] List<GcpTierDto?>? TieredRates);

internal sealed record GcpTierDto(
    [property: JsonPropertyName("startUsageAmount")] decimal? StartUsageAmount,
    [property: JsonPropertyName("unitPrice")] GcpMoneyDto? UnitPrice);

/// <summary>A google.type.Money: <c>units</c> is an int64 in a string, <c>nanos</c> the billionths.</summary>
internal sealed record GcpMoneyDto(
    [property: JsonPropertyName("currencyCode")] string? CurrencyCode,
    [property: JsonPropertyName("units")] string? Units,
    [property: JsonPropertyName("nanos")] long? Nanos);

public sealed record GcpService(string Id, string DisplayName);

/// <param name="More">True when the catalog has more services than Vellum read.</param>
public sealed record GcpServiceList(IReadOnlyList<GcpService> Services, bool More);

/// <summary>
/// One SKU of the catalog, cleaned. <paramref name="Unit"/> is the unit as Vellum stores it;
/// <paramref name="UsageUnit"/> is the unit as Google writes it. <paramref name="NotImportable"/>
/// says why Vellum does not store the SKU as a price, or is null.
/// </summary>
public sealed record GcpSku(
    string SkuId, string Description, string ServiceDisplayName, string ResourceFamily, string ResourceGroup, string UsageType,
    IReadOnlyList<string> Regions, string Unit, string UsageUnit, string UsageUnitDescription,
    decimal UnitPrice, string Currency, int Tiers, DateOnly? EffectiveDate, string? NotImportable);

/// <param name="More">True when the catalog has more matching SKUs than Vellum read. Narrow the search.</param>
/// <param name="SourceUrl">The address of the first page, without the key.</param>
public sealed record GcpSkuPage(string ServiceId, IReadOnlyList<GcpSku> Items, bool More, string SourceUrl);

/// <summary>Which SKUs a read keeps: those in <paramref name="Region"/> (or in every region when it is null) whose text holds every word, or the given SKU IDs.</summary>
public sealed record GcpSkuFilter(string? Region, IReadOnlyList<string> Words, IReadOnlySet<string>? SkuIds = null);

/// <summary>
/// Reads prices from the Google Cloud Billing Catalog API for one workspace, with the API key
/// of the workspace. Everything the API returns is untrusted data. Vellum stores no number
/// that the response does not hold.
/// </summary>
public sealed partial class GcpCloudBilling(IServiceProvider services, SettingsResolver settings, SecretReader secrets)
{
    public const int PageSize = 5000;
    public const int MaxServicePages = 3;
    /// <summary>A service such as Compute Engine has tens of thousands of SKUs.</summary>
    public const int MaxSkuPages = 10;
    public const int MaxItems = 500;
    public const int MaxImport = 50;
    private const long MaxPageBytes = 16 * 1024 * 1024;
    private const int MaxTextChars = 200;
    private const int MaxRegions = 40;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    public const string Tiered = "The price of this SKU has tiers. Vellum does not model price tiers. Enter the price that you pay as a custom price.";
    public const string KeyHint = "Save the API key of a Google Cloud project with the Cloud Billing API turned on as the workspace secret gcp:apiKey (Settings, Prices).";

    public static bool IsServiceId(string? value) => value is not null && ServiceId().IsMatch(value);

    public static bool IsSkuId(string? value) => value is not null && ServiceId().IsMatch(value);

    public static bool IsRegion(string? value) => value is not null && Region().IsMatch(value);

    public async Task<PriceSourceResult<GcpServiceList>> ServicesAsync(Guid workspaceId, CancellationToken ct)
    {
        var connection = await ConnectAsync(workspaceId, "list-services", ct);
        if (connection.Failure is { } refused)
            return refused;
        var (api, call) = connection.Value!;

        var found = new List<GcpService>();
        string? token = null;
        try
        {
            for (var page = 1; ; page++)
            {
                GcpServicesDto? body;
                using (var response = await OutboundSend.RunAsync(() => api.Services(PageSize, token, call, ct)))
                {
                    if (!response.IsSuccessStatusCode)
                        return PriceSourceFailure.FromStatus((int)response.StatusCode);
                    body = await OutboundBody.ReadJsonAsync<GcpServicesDto>(response, ct);
                }
                if (body?.Services is not { } list)
                    return new PriceSourceFailure.Unavailable("unexpected_response");

                found.AddRange(list.Where(s => IsServiceId(s?.ServiceId))
                    .Select(s => new GcpService(s!.ServiceId!, PriceSources.Clean(s.DisplayName, MaxTextChars) ?? s.ServiceId!)));
                token = NextToken(body.NextPageToken);
                if (token is null || page == MaxServicePages)
                {
                    return new GcpServiceList(
                        found.OrderBy(s => s.DisplayName, StringComparer.OrdinalIgnoreCase).ThenBy(s => s.Id, StringComparer.Ordinal).ToList(),
                        token is not null);
                }
            }
        }
        catch (OutboundException ex)
        {
            return PriceSourceFailure.From(ex);
        }
    }

    public async Task<PriceSourceResult<GcpSkuPage>> SkusAsync(
        Guid workspaceId, string serviceId, string currency, GcpSkuFilter filter, CancellationToken ct)
    {
        var connection = await ConnectAsync(workspaceId, "list-skus", ct);
        if (connection.Failure is { } refused)
            return refused;
        var (api, call) = connection.Value!;

        var items = new List<GcpSku>();
        string? token = null;
        string? sourceUrl = null;
        try
        {
            for (var page = 1; ; page++)
            {
                GcpSkusDto? body;
                using (var response = await OutboundSend.RunAsync(() => api.Skus(serviceId, currency, PageSize, token, call, ct)))
                {
                    if (!response.IsSuccessStatusCode)
                        return PriceSourceFailure.FromStatus((int)response.StatusCode);
                    // The address that Vellum built. The key is in a header, not in it.
                    sourceUrl ??= response.RequestMessage?.RequestUri?.AbsoluteUri;
                    body = await OutboundBody.ReadJsonAsync<GcpSkusDto>(response, ct);
                }
                if (body?.Skus is not { } skus || sourceUrl is null)
                    return new PriceSourceFailure.Unavailable("unexpected_response");

                foreach (var dto in skus)
                {
                    if (dto is null || Read(dto, currency) is not { } sku || !Keeps(filter, sku))
                        continue;
                    if (items.Count == MaxItems)
                        return new GcpSkuPage(serviceId, items, true, sourceUrl);
                    items.Add(sku);
                }

                token = NextToken(body.NextPageToken);
                // An import stops when it has every SKU that it asked for.
                if (token is null || (filter.SkuIds is { } wanted && items.Count == wanted.Count))
                    return new GcpSkuPage(serviceId, items, false, sourceUrl);
                if (page == MaxSkuPages)
                    return new GcpSkuPage(serviceId, items, true, sourceUrl);
            }
        }
        catch (OutboundException ex)
        {
            return PriceSourceFailure.From(ex);
        }
    }

    /// <summary>
    /// The client and the call for one workspace, with its key. The key is read for the fixed
    /// address of the API: a key that was saved for another origin is not sent.
    /// </summary>
    private async Task<PriceSourceResult<Tuple<IGcpCloudBillingApi, OutboundCall>>> ConnectAsync(
        Guid workspaceId, string operation, CancellationToken ct)
    {
        var workspace = await settings.GetWorkspaceAsync(workspaceId, ct);
        var destination = WorkspaceSettings.GcpCloudBillingUrl;
        string key;
        try
        {
            if (await secrets.ReadAsync(workspaceId, SecretNames.GcpApiKey, destination.Url, ct) is not SecretRead.Found found)
                return new PriceSourceFailure.NotConfigured(KeyHint);
            key = found.Value;
        }
        catch (SecretUnavailableException)
        {
            return new PriceSourceFailure.Unavailable("secret_unavailable");
        }

        var call = new OutboundCall("gcp-cloud-billing", operation, destination.Url, workspace.Policy.AllowPrivateNetworkUrls, workspace.RequireHttps, Timeout)
        {
            MaxResponseBytes = MaxPageBytes,
            Credential = new OutboundCredential("X-Goog-Api-Key", null, key),
        };
        return Tuple.Create(services.GetRequiredService<IGcpCloudBillingApi>(), call);
    }

    private static string? NextToken(string? token) =>
        string.IsNullOrEmpty(token) ? null : token.Length <= 1000 && !OutboundText.HasControlCharacter(token) ? token : null;

    private static bool Keeps(GcpSkuFilter filter, GcpSku sku)
    {
        if (filter.SkuIds is { } ids)
            return ids.Contains(sku.SkuId);
        if (filter.Region is { } region && !sku.Regions.Contains(region, StringComparer.OrdinalIgnoreCase))
            return false;
        return filter.Words.All(word =>
            sku.Description.Contains(word, StringComparison.OrdinalIgnoreCase)
            || sku.ResourceFamily.Contains(word, StringComparison.OrdinalIgnoreCase)
            || sku.ResourceGroup.Contains(word, StringComparison.OrdinalIgnoreCase)
            || sku.UsageType.Contains(word, StringComparison.OrdinalIgnoreCase)
            || sku.SkuId.Contains(word, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// A SKU with its current price, or null when it lacks what identifies a price: the ID,
    /// the unit, or a rate in the currency that was asked for. Such a SKU is not shown.
    /// </summary>
    private static GcpSku? Read(GcpSkuDto dto, string currency)
    {
        if (!IsSkuId(dto.SkuId) || dto.PricingInfo is not [{ PricingExpression: { } expression } info, ..]
            || PriceSources.Clean(expression.UsageUnit, UnitExpression.MaxChars) is not { } usageUnit
            || expression.TieredRates is not { Count: > 0 } rates
            || rates[0]?.UnitPrice is not { } money || !string.Equals(money.CurrencyCode, currency, StringComparison.Ordinal)
            || Amount(money) is not { } price)
            return null;

        var notImportable =
            rates.Count > 1 || rates[0]!.StartUsageAmount is > 0 ? Tiered
            : PriceRules.UnitPrice("unitPrice", price) is not null ? "The price is outside the range that Vellum stores."
            : null;
        var category = dto.Category;
        var description = PriceSources.Clean(expression.UsageUnitDescription, MaxTextChars) ?? "";
        return new GcpSku(
            dto.SkuId!, PriceSources.Clean(dto.Description, MaxTextChars) ?? dto.SkuId!,
            PriceSources.Clean(category?.ServiceDisplayName, MaxTextChars) ?? "", PriceSources.Clean(category?.ResourceFamily, MaxTextChars) ?? "",
            PriceSources.Clean(category?.ResourceGroup, MaxTextChars) ?? "", PriceSources.Clean(category?.UsageType, MaxTextChars) ?? "",
            (dto.ServiceRegions ?? []).Select(r => PriceSources.Clean(r, CostRules.MaxRegionChars)).OfType<string>().Take(MaxRegions).ToList(),
            Unit(usageUnit, description), usageUnit, description, price, currency, rates.Count,
            info.EffectiveTime is { } at ? DateOnly.FromDateTime(at.UtcDateTime) : null, notImportable);
    }

    /// <summary>The amount of a google.type.Money, or null when it is not a number Vellum reads.</summary>
    private static decimal? Amount(GcpMoneyDto money)
    {
        if (!long.TryParse(money.Units ?? "0", NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var units)
            || money.Nanos is not { } nanos || nanos is <= -1_000_000_000 or >= 1_000_000_000)
            return null;
        return units + nanos / 1_000_000_000m;
    }

    /// <summary>
    /// The unit of a SKU as Vellum reads units: <c>h</c> is <c>hour</c>, <c>GiBy.mo</c> is
    /// <c>GiB/month</c>. A unit with no such form keeps Google's description.
    /// </summary>
    public static string Unit(string usageUnit, string description)
    {
        static string? Amount(string part) => part switch
        {
            "h" => "hour",
            "d" => "day",
            "mo" => "month",
            "s" => "second",
            "By" => "byte",
            "KiBy" => "KiB",
            "MiBy" => "MiB",
            "GiBy" => "GiB",
            "TiBy" => "TiB",
            "count" => "count",
            "request" or "requests" => "requests",
            _ => null,
        };

        var parts = usageUnit.Split('.');
        if (parts is [var one] && Amount(one) is { } unit)
            return unit;
        if (parts is [var amount, var period] && Amount(amount) is { } of && period is "h" or "d" or "mo")
            return $"{of}/{Amount(period)}";
        return description.Length > 0 ? description : usageUnit;
    }

    [GeneratedRegex(@"\A[A-Za-z0-9-]{1,40}\z")]
    private static partial Regex ServiceId();

    [GeneratedRegex(@"\A[a-z0-9-]{1,40}\z")]
    private static partial Regex Region();
}
