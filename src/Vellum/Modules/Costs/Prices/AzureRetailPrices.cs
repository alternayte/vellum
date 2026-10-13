using System.Text.Json.Serialization;
using Refit;
using Vellum.Modules.Settings.Document;
using Vellum.Shared.Outbound;

namespace Vellum.Modules.Costs.Prices;

/// <summary>
/// The Azure Retail Prices API as Microsoft documents it at
/// https://learn.microsoft.com/en-us/rest/api/cost-management/retail-prices/azure-retail-prices
/// (read on 2026-10-13). It is public and takes no credential. Version
/// <c>2023-01-01-preview</c> is the documented one in which a filter value is case sensitive.
/// </summary>
[Headers("Accept: application/json", "User-Agent: Vellum")]
internal interface IAzureRetailPricesApi
{
    /// <param name="currencyCode">The code in single quotes, as the documented call <c>currencyCode='EUR'</c> has it.</param>
    [Get("/api/retail/prices")]
    Task<HttpResponseMessage> List(
        [AliasAs("api-version")] string apiVersion, string currencyCode, [AliasAs("$filter")] string filter,
        [Property(OutboundCall.PropertyName)] OutboundCall call, CancellationToken ct);

    /// <summary>Reads the next page at the address in <see cref="OutboundCall.AbsoluteUri"/>.</summary>
    [Get("/")]
    Task<HttpResponseMessage> Follow([Property(OutboundCall.PropertyName)] OutboundCall call, CancellationToken ct);
}

/// <summary>The documented response: <c>Items</c> and <c>NextPageLink</c>.</summary>
internal sealed record AzurePageDto(
    [property: JsonPropertyName("Items")] List<AzureItemDto?>? Items,
    [property: JsonPropertyName("NextPageLink")] string? NextPageLink);

/// <summary>The documented properties of one record. <c>effectiveStartDate</c> is optional.</summary>
internal sealed record AzureItemDto(
    [property: JsonPropertyName("currencyCode")] string? CurrencyCode,
    [property: JsonPropertyName("tierMinimumUnits")] decimal? TierMinimumUnits,
    [property: JsonPropertyName("reservationTerm")] string? ReservationTerm,
    [property: JsonPropertyName("retailPrice")] decimal? RetailPrice,
    [property: JsonPropertyName("armRegionName")] string? ArmRegionName,
    [property: JsonPropertyName("location")] string? Location,
    [property: JsonPropertyName("effectiveStartDate")] DateTimeOffset? EffectiveStartDate,
    [property: JsonPropertyName("meterId")] string? MeterId,
    [property: JsonPropertyName("meterName")] string? MeterName,
    [property: JsonPropertyName("productId")] string? ProductId,
    [property: JsonPropertyName("skuId")] string? SkuId,
    [property: JsonPropertyName("productName")] string? ProductName,
    [property: JsonPropertyName("skuName")] string? SkuName,
    [property: JsonPropertyName("serviceName")] string? ServiceName,
    [property: JsonPropertyName("serviceFamily")] string? ServiceFamily,
    [property: JsonPropertyName("unitOfMeasure")] string? UnitOfMeasure,
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("isPrimaryMeterRegion")] bool? IsPrimaryMeterRegion,
    [property: JsonPropertyName("armSkuName")] string? ArmSkuName);

/// <summary>
/// One record of the price API, cleaned: every text is cut to a fixed length and holds no
/// control character. <paramref name="NotImportable"/> says why Vellum does not store the
/// row as a price, or is null.
/// </summary>
public sealed record AzureMeter(
    string MeterId, string MeterName, string ProductId, string ProductName, string SkuId, string SkuName, string ArmSkuName,
    string ServiceName, string ServiceFamily, string ArmRegionName, string Location,
    string UnitOfMeasure, decimal RetailPrice, string CurrencyCode, DateOnly? EffectiveStartDate,
    string Type, decimal TierMinimumUnits, string? ReservationTerm, bool? IsPrimaryMeterRegion, string? NotImportable);

/// <param name="More">True when the API has more records than Vellum read. Narrow the search.</param>
/// <param name="SourceUrl">The address of the first page.</param>
public sealed record AzureMeterPage(IReadOnlyList<AzureMeter> Items, bool More, string SourceUrl);

/// <summary>Why a read of the price API gave no value. Callers see these and no HTTP type.</summary>
public abstract record AzurePricesFailure
{
    private AzurePricesFailure() { }

    /// <summary>The workspace has no usable address for the price API.</summary>
    public sealed record NotConfigured(string? Reason) : AzurePricesFailure;
    /// <summary>The API refused the filter.</summary>
    public sealed record BadFilter : AzurePricesFailure;
    public sealed record RateLimited : AzurePricesFailure;
    /// <param name="Reason">
    /// One of <c>url_refused</c>, <c>invalid_setting</c>, <c>timeout</c>, <c>network</c>,
    /// <c>server_error</c>, <c>too_large</c>, <c>next_page_refused</c>, <c>unexpected_response</c>.
    /// </param>
    public sealed record Unavailable(string Reason) : AzurePricesFailure;
}

public readonly record struct AzurePricesResult(AzureMeterPage? Value, AzurePricesFailure? Failure)
{
    public static implicit operator AzurePricesResult(AzureMeterPage value) => new(value, null);
    public static implicit operator AzurePricesResult(AzurePricesFailure failure) => new(null, failure);
}

/// <param name="ServiceName">Exact and case sensitive, for example <c>Virtual Machines</c>.</param>
/// <param name="ArmRegionName">Exact, for example <c>westeurope</c>.</param>
/// <param name="SkuName">Exact, for example <c>D2s v5</c>.</param>
/// <param name="MeterNameContains">A part of the meter name.</param>
public sealed record AzureMeterQuery(
    string? ServiceName = null, string? ArmRegionName = null, string? SkuName = null, string? MeterNameContains = null,
    string? MeterId = null, string? SkuId = null);

/// <summary>
/// Reads consumption prices from the Azure Retail Prices API for one workspace. Everything
/// the API returns is untrusted data. Vellum sends no credential and stores no number that
/// the response does not hold.
/// </summary>
public sealed class AzureRetailPrices(IServiceProvider services, SettingsResolver settings)
{
    public const string ApiVersion = "2023-01-01-preview";
    public const int MaxFilterValueChars = 100;

    /// <summary>The API returns at most 1,000 records for one request.</summary>
    public const int MaxPages = 2;
    public const int MaxItems = 500;
    private const long MaxPageBytes = 4 * 1024 * 1024;
    private const int MaxTextChars = 200;
    private const int MaxIdChars = 100;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    /// <summary>The types of the documented <c>type</c> property that are a price per unit of use.</summary>
    private const string Consumption = "Consumption";

    /// <summary>
    /// The filter of a query, or null when a value cannot be put into a filter. The
    /// documentation shows a value in single quotes and does not say how a quote inside a
    /// value is written, so a value with a quote is refused. Only consumption prices are
    /// asked for: the price of a reservation is the price of a whole term, not of a unit.
    /// </summary>
    public static string? Filter(AzureMeterQuery query)
    {
        var parts = new List<string>();
        foreach (var (field, value) in new[]
                 {
                     ("serviceName", query.ServiceName), ("armRegionName", query.ArmRegionName), ("skuName", query.SkuName),
                     ("meterId", query.MeterId), ("skuId", query.SkuId),
                 })
        {
            if (string.IsNullOrEmpty(value))
                continue;
            if (!IsFilterValue(value))
                return null;
            parts.Add($"{field} eq '{value}'");
        }

        if (!string.IsNullOrEmpty(query.MeterNameContains))
        {
            if (!IsFilterValue(query.MeterNameContains))
                return null;
            parts.Add($"contains(meterName, '{query.MeterNameContains}')");
        }

        if (parts.Count == 0)
            return null;
        parts.Add($"priceType eq '{Consumption}'");
        return string.Join(" and ", parts);
    }

    public static bool IsFilterValue(string value) =>
        value.Length <= MaxFilterValueChars && !value.Contains('\'') && !OutboundText.HasControlCharacter(value)
        && value.Trim().Length == value.Length;

    public static bool IsCurrency(string? code) => code is { Length: 3 } && code.All(c => c is >= 'A' and <= 'Z');

    /// <param name="filter">A filter from <see cref="Filter"/>.</param>
    public async Task<AzurePricesResult> SearchAsync(Guid workspaceId, string filter, string currency, CancellationToken ct)
    {
        var workspace = await settings.GetWorkspaceAsync(workspaceId, ct);
        if (workspace.AzureRetailPricesBaseUrl is not SettingsUrl.Usable destination)
        {
            return new AzurePricesFailure.NotConfigured(
                workspace.AzureRetailPricesBaseUrl is SettingsUrl.BlockedByPolicy blocked ? blocked.Message : null);
        }

        var baseUrl = destination.Url;
        var api = services.GetRequiredService<IAzureRetailPricesApi>();
        var call = new OutboundCall(
            "azure-retail-prices", "list-prices", baseUrl, workspace.Policy.AllowPrivateNetworkUrls, workspace.RequireHttps, Timeout)
        {
            MaxResponseBytes = MaxPageBytes,
        };

        var items = new List<AzureMeter>();
        string? sourceUrl = null;
        try
        {
            var response = await OutboundSend.RunAsync(() => api.List(ApiVersion, $"'{currency}'", filter, call, ct));
            for (var page = 1; ; page++)
            {
                AzurePageDto? body;
                using (response)
                {
                    if (!response.IsSuccessStatusCode)
                        return Failure((int)response.StatusCode);
                    // The address is the one Vellum built. A base URL with credentials never passes the URL policy.
                    sourceUrl ??= response.RequestMessage?.RequestUri?.AbsoluteUri;
                    body = await OutboundBody.ReadJsonAsync<AzurePageDto>(response, ct);
                }

                if (body?.Items is not { } records || sourceUrl is null)
                    return new AzurePricesFailure.Unavailable("unexpected_response");

                foreach (var record in records)
                {
                    if (items.Count == MaxItems)
                        return new AzureMeterPage(items, true, sourceUrl);
                    if (record is not null && Read(record, currency) is { } meter)
                        items.Add(meter);
                }

                if (string.IsNullOrEmpty(body.NextPageLink))
                    return new AzureMeterPage(items, false, sourceUrl);
                if (page == MaxPages)
                    return new AzureMeterPage(items, true, sourceUrl);

                // The next page is read only from the origin in settings. The pipeline checks
                // the address again like any other, with the same private-network and https rules.
                if (!Uri.TryCreate(body.NextPageLink, UriKind.Absolute, out var next)
                    || next.Scheme != baseUrl.Scheme || next.Port != baseUrl.Port
                    || !string.Equals(next.IdnHost, baseUrl.IdnHost, StringComparison.OrdinalIgnoreCase))
                    return new AzurePricesFailure.Unavailable("next_page_refused");

                var follow = call with { Operation = "list-prices-next", AbsoluteUri = next };
                response = await OutboundSend.RunAsync(() => api.Follow(follow, ct));
            }
        }
        catch (OutboundException ex)
        {
            return new AzurePricesFailure.Unavailable(ex.Failure switch
            {
                OutboundFailure.ResponseTooLarge => "too_large",
                OutboundFailure.UrlRefused => "url_refused",
                OutboundFailure.InvalidRequest => "invalid_setting",
                OutboundFailure.Timeout => "timeout",
                _ => "network",
            });
        }
    }

    private static AzurePricesFailure Failure(int status) => status switch
    {
        400 => new AzurePricesFailure.BadFilter(),
        429 => new AzurePricesFailure.RateLimited(),
        >= 500 => new AzurePricesFailure.Unavailable("server_error"),
        _ => new AzurePricesFailure.Unavailable("unexpected_response"),
    };

    /// <summary>
    /// A record as a meter, or null when it lacks what identifies a price: the meter ID, the
    /// unit, the price or the currency that was asked for. Such a record is not shown.
    /// </summary>
    private static AzureMeter? Read(AzureItemDto item, string currency)
    {
        if (Clean(item.MeterId, MaxIdChars) is not { } meterId || Clean(item.UnitOfMeasure, UnitExpression.MaxChars) is not { } unit
            || item.RetailPrice is not { } price || !string.Equals(item.CurrencyCode, currency, StringComparison.Ordinal))
            return null;

        var type = Clean(item.Type, MaxIdChars) ?? "";
        var tier = item.TierMinimumUnits ?? 0m;
        var term = Clean(item.ReservationTerm, MaxIdChars);
        var notImportable =
            type != Consumption || term is not null ? "Vellum stores consumption prices only."
            : tier != 0m ? "The price applies above a minimum amount. Vellum does not model price tiers."
            : PriceRules.UnitPrice("retailPrice", price) is not null ? "The price is outside the range that Vellum stores."
            : null;

        return new AzureMeter(
            meterId, Clean(item.MeterName, MaxTextChars) ?? "", Clean(item.ProductId, MaxIdChars) ?? "", Clean(item.ProductName, MaxTextChars) ?? "",
            Clean(item.SkuId, MaxIdChars) ?? "", Clean(item.SkuName, MaxTextChars) ?? "", Clean(item.ArmSkuName, MaxTextChars) ?? "",
            Clean(item.ServiceName, MaxTextChars) ?? "", Clean(item.ServiceFamily, MaxTextChars) ?? "",
            Clean(item.ArmRegionName, CostRules.MaxRegionChars) ?? "", Clean(item.Location, MaxTextChars) ?? "",
            unit, price, currency, item.EffectiveStartDate is { } start ? DateOnly.FromDateTime(start.UtcDateTime) : null,
            type, tier, term, item.IsPrimaryMeterRegion, notImportable);
    }

    /// <summary>Remote text for a client: cut, without control characters, and null when nothing is left.</summary>
    private static string? Clean(string? text, int maxLength) =>
        text is null ? null : OutboundText.Clean(text, maxLength).Trim() is { Length: > 0 } clean ? clean : null;
}
