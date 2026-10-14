using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Refit;
using Vellum.Modules.Settings;
using Vellum.Shared.Outbound;

namespace Vellum.Modules.Costs.Prices;

/// <summary>
/// The AWS Price List Bulk API as AWS documents it at
/// https://docs.aws.amazon.com/awsaccountbilling/latest/aboutv2/using-the-aws-price-list-bulk-api.html
/// (read on 2026-10-14). It is public and takes no credential: the service index, the region
/// index of one service and the offer file of one service in one region.
/// </summary>
[Headers("Accept: application/json", "User-Agent: Vellum")]
internal interface IAwsPriceListApi
{
    [Get("/offers/v1.0/aws/index.json")]
    Task<HttpResponseMessage> Offers([Property(OutboundCall.PropertyName)] OutboundCall call, CancellationToken ct);

    [Get("/offers/v1.0/aws/{offerCode}/current/region_index.json")]
    Task<HttpResponseMessage> Regions(string offerCode, [Property(OutboundCall.PropertyName)] OutboundCall call, CancellationToken ct);

    /// <summary>Reads the offer file at the address in <see cref="OutboundCall.AbsoluteUri"/>.</summary>
    [Get("/")]
    Task<HttpResponseMessage> Follow([Property(OutboundCall.PropertyName)] OutboundCall call, CancellationToken ct);
}

/// <summary>The documented service index: <c>offers</c> by offer code.</summary>
internal sealed record AwsOfferIndexDto([property: JsonPropertyName("offers")] Dictionary<string, AwsOfferDto?>? Offers);

internal sealed record AwsOfferDto([property: JsonPropertyName("offerCode")] string? OfferCode);

/// <summary>The documented region index of one service: <c>regions</c> by region code.</summary>
internal sealed record AwsRegionIndexDto([property: JsonPropertyName("regions")] Dictionary<string, AwsRegionDto?>? Regions);

internal sealed record AwsRegionDto(
    [property: JsonPropertyName("regionCode")] string? RegionCode,
    [property: JsonPropertyName("currentVersionUrl")] string? CurrentVersionUrl);

/// <summary>The offer codes of the services, for example <c>AmazonEC2</c> or <c>AWSLambda</c>.</summary>
public sealed record AwsServiceList(IReadOnlyList<string> Services);

/// <summary>The region codes that a service has an offer file for, for example <c>eu-west-1</c>.</summary>
public sealed record AwsRegionList(string Service, IReadOnlyList<string> Regions);

/// <summary>
/// One price dimension of an on-demand term of a product, cleaned: every text is cut to a fixed
/// length and holds no control character. <paramref name="RateCode"/> is the provider's ID of
/// the price. <paramref name="Unit"/> is the unit as Vellum stores it; <paramref name="UnitOfMeasure"/>
/// is the unit as AWS writes it. <paramref name="NotImportable"/> says why Vellum does not
/// store the row as a price, or is null.
/// </summary>
public sealed record AwsPrice(
    string Sku, string RateCode, string ProductFamily, IReadOnlyDictionary<string, string> Attributes,
    string Description, string Unit, string UnitOfMeasure, decimal PricePerUnit, string Currency,
    string BeginRange, string EndRange, DateOnly? EffectiveDate, string? NotImportable);

/// <param name="More">True when the offer file has more matching rows than Vellum keeps. Narrow the search.</param>
/// <param name="SourceUrl">The address of the offer file.</param>
public sealed record AwsPricePage(
    string Service, string Region, IReadOnlyList<AwsPrice> Items, bool More, string SourceUrl, string? PublicationDate);

/// <summary>What a read of an offer file keeps: the products whose text holds every search word, or the products with the given SKUs.</summary>
public sealed record AwsOfferFilter(IReadOnlyList<string> Words, IReadOnlySet<string>? Skus = null);

/// <summary>
/// Reads prices from the AWS Price List Bulk API for one workspace. Everything the API returns
/// is untrusted data. Vellum sends no credential and stores no number that the offer file does
/// not hold. An offer file can be large: it is read as a stream, only the products that the
/// filter keeps and their on-demand terms are held, and the read stops after the on-demand terms.
/// </summary>
public sealed partial class AwsPriceList(IServiceProvider services, SettingsResolver settings)
{
    public static readonly Uri BaseUrl = new("https://pricing.us-east-1.amazonaws.com");

    /// <summary>The largest offer file Vellum reads. The file of EC2 in a large region is larger; enter such a price as a custom price.</summary>
    public const long MaxOfferBytes = 200L * 1024 * 1024;
    public const int MaxItems = 500;
    public const int MaxImport = 50;
    private const long MaxIndexBytes = 4 * 1024 * 1024;
    private const int MaxTextChars = 200;
    private const int MaxAttributes = 40;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MaxReadTime = TimeSpan.FromMinutes(3);

    public const string Tiered = "The price of this product has tiers. Vellum does not model price tiers. Enter the price that you pay as a custom price.";

    public static bool IsServiceCode(string? value) => value is not null && ServiceCode().IsMatch(value);

    public static bool IsRegionCode(string? value) => value is not null && RegionCode().IsMatch(value);

    public static bool IsRateCode(string? value) => value is not null && RateCode().IsMatch(value);

    public async Task<PriceSourceResult<AwsServiceList>> ServicesAsync(Guid workspaceId, CancellationToken ct)
    {
        var (api, call) = await ConnectAsync(workspaceId, "list-services", ct);
        try
        {
            using var response = await OutboundSend.RunAsync(() => api.Offers(call, ct));
            if (!response.IsSuccessStatusCode)
                return PriceSourceFailure.FromStatus((int)response.StatusCode);
            if (await OutboundBody.ReadJsonAsync<AwsOfferIndexDto>(response, ct) is not { Offers: { } offers })
                return new PriceSourceFailure.Unavailable("unexpected_response");
            return new AwsServiceList(offers.Keys.Where(IsServiceCode).Order(StringComparer.OrdinalIgnoreCase).ToList());
        }
        catch (OutboundException ex)
        {
            return PriceSourceFailure.From(ex);
        }
    }

    public async Task<PriceSourceResult<AwsRegionList>> RegionsAsync(Guid workspaceId, string service, CancellationToken ct)
    {
        var (api, call) = await ConnectAsync(workspaceId, "list-regions", ct);
        try
        {
            var index = await RegionIndexAsync(api, call, service, ct);
            if (index.Failure is { } failure)
                return failure;
            return new AwsRegionList(service, index.Value!.Keys.Order(StringComparer.Ordinal).ToList());
        }
        catch (OutboundException ex)
        {
            return PriceSourceFailure.From(ex);
        }
    }

    /// <summary>The on-demand prices of the products of one service in one region that the filter keeps.</summary>
    public async Task<PriceSourceResult<AwsPricePage>> PricesAsync(
        Guid workspaceId, string service, string region, AwsOfferFilter filter, CancellationToken ct)
    {
        var (api, call) = await ConnectAsync(workspaceId, "list-regions", ct);
        try
        {
            var index = await RegionIndexAsync(api, call, service, ct);
            if (index.Failure is { } failure)
                return failure;
            if (!index.Value!.TryGetValue(region, out var path))
                return new PriceSourceFailure.NotFound();
            // The offer file is read only from the origin of the API, at a path of this service.
            if (!IsOfferPath(path, service))
                return new PriceSourceFailure.Unavailable("next_page_refused");

            var offer = call with
            {
                Operation = "read-offer",
                AbsoluteUri = new Uri(BaseUrl, path),
                Streaming = true,
                MaxResponseBytes = MaxOfferBytes,
                MaxStreamDuration = MaxReadTime,
            };
            using var response = await OutboundSend.RunAsync(() => api.Follow(offer, ct));
            if (!response.IsSuccessStatusCode)
                return PriceSourceFailure.FromStatus((int)response.StatusCode);
            var sourceUrl = response.RequestMessage?.RequestUri?.AbsoluteUri ?? offer.AbsoluteUri.AbsoluteUri;

            var parser = new AwsOfferParser(filter);
            await using (var stream = await response.Content.ReadAsStreamAsync(ct))
            {
                if (!await parser.ReadAsync(stream, ct))
                    return new PriceSourceFailure.Unavailable("unexpected_response");
            }

            return new AwsPricePage(service, region, parser.Rows, parser.More, sourceUrl, parser.PublicationDate);
        }
        catch (OutboundException ex)
        {
            return PriceSourceFailure.From(ex);
        }
    }

    /// <summary>The path of the offer file of each region, from the region index of the service.</summary>
    private static async Task<PriceSourceResult<Dictionary<string, string>>> RegionIndexAsync(
        IAwsPriceListApi api, OutboundCall call, string service, CancellationToken ct)
    {
        using var response = await OutboundSend.RunAsync(() => api.Regions(service, call, ct));
        // The API answers a service that it does not have with 403 or 404.
        if ((int)response.StatusCode is 403 or 404)
            return new PriceSourceFailure.NotFound();
        if (!response.IsSuccessStatusCode)
            return PriceSourceFailure.FromStatus((int)response.StatusCode);
        if (await OutboundBody.ReadJsonAsync<AwsRegionIndexDto>(response, ct) is not { Regions: { } regions })
            return new PriceSourceFailure.Unavailable("unexpected_response");

        var paths = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (code, entry) in regions)
        {
            if (IsRegionCode(code) && entry?.CurrentVersionUrl is { } url)
                paths[code] = url;
        }

        return paths;
    }

    private async Task<(IAwsPriceListApi Api, OutboundCall Call)> ConnectAsync(Guid workspaceId, string operation, CancellationToken ct)
    {
        var workspace = await settings.GetWorkspaceAsync(workspaceId, ct);
        return (services.GetRequiredService<IAwsPriceListApi>(),
            new OutboundCall("aws-price-list", operation, BaseUrl, workspace.Policy.AllowPrivateNetworkUrls, workspace.RequireHttps, Timeout)
            {
                MaxResponseBytes = MaxIndexBytes,
            });
    }

    private static bool IsOfferPath(string path, string service) =>
        path.StartsWith($"/offers/v1.0/aws/{service}/", StringComparison.Ordinal)
        && path.EndsWith("/index.json", StringComparison.Ordinal)
        && OfferPath().IsMatch(path) && !path.Contains("..", StringComparison.Ordinal);

    /// <summary>The unit of an AWS price as Vellum reads units: <c>Hrs</c> is <c>hour</c>, <c>GB-Mo</c> is <c>GB/month</c>.</summary>
    public static string Unit(string unit) => unit switch
    {
        "Hrs" or "Hr" or "Hours" or "Hour" or "hours" or "hour" => "hour",
        _ when unit.EndsWith("-Mo", StringComparison.Ordinal) && unit.Length > 3 => unit[..^3] + "/month",
        _ when unit.EndsWith("-Month", StringComparison.OrdinalIgnoreCase) && unit.Length > 6 => unit[..^6] + "/month",
        _ => unit,
    };

    internal static string? Clean(string? text, int maxLength = MaxTextChars) => PriceSources.Clean(text, maxLength);

    [GeneratedRegex(@"\A[A-Za-z0-9]{1,64}\z")]
    private static partial Regex ServiceCode();

    [GeneratedRegex(@"\A[a-z]{2}(-[a-z0-9]{1,20}){1,4}\z")]
    private static partial Regex RegionCode();

    [GeneratedRegex(@"\A[A-Z0-9]{1,40}\.[A-Z0-9]{1,40}\.[A-Z0-9]{1,40}\z")]
    private static partial Regex RateCode();

    [GeneratedRegex(@"\A/offers/v1\.0/aws/[A-Za-z0-9]{1,64}/[A-Za-z0-9-]{1,40}/[a-z0-9-]{1,40}/index\.json\z")]
    private static partial Regex OfferPath();

    /// <summary>
    /// Reads an offer file as a stream of tokens. A product and the on-demand terms of one
    /// product are small and are parsed whole; the rest is skipped token by token, so the
    /// memory a read holds does not grow with the file.
    /// </summary>
    internal sealed class AwsOfferParser(AwsOfferFilter filter)
    {
        private const int FirstBuffer = 64 * 1024;
        /// <summary>The largest product or term that is parsed whole.</summary>
        private const int MaxValueBytes = 2 * 1024 * 1024;

        private enum Section { Root, Products, Terms, OnDemand, Done }

        private sealed record Product(string Sku, string Family, IReadOnlyDictionary<string, string> Attributes);

        private readonly Dictionary<string, Product> _products = new(StringComparer.Ordinal);
        private Section _section = Section.Root;
        private string? _property;
        private bool _onDemandNext;
        private bool _skipNext;
        private int _skipDepth = -1;
        private bool _malformed;

        public List<AwsPrice> Rows { get; } = [];
        public bool More { get; private set; }
        public string? PublicationDate { get; private set; }

        /// <returns>False when the stream is not an offer file.</returns>
        public async Task<bool> ReadAsync(Stream stream, CancellationToken ct)
        {
            var buffer = new byte[FirstBuffer];
            int start = 0, end = 0;
            var final = false;
            var state = new JsonReaderState(new JsonReaderOptions { MaxDepth = 64 });
            while (true)
            {
                if (start == end)
                    start = end = 0;
                if (end == buffer.Length)
                {
                    if (start > 0)
                    {
                        Buffer.BlockCopy(buffer, start, buffer, 0, end - start);
                        end -= start;
                        start = 0;
                    }
                    else if (buffer.Length >= MaxValueBytes)
                    {
                        return false;
                    }
                    else
                    {
                        Array.Resize(ref buffer, buffer.Length * 2);
                    }
                }

                var read = await stream.ReadAsync(buffer.AsMemory(end), ct);
                if (read == 0)
                    final = true;
                end += read;

                start += Process(buffer.AsSpan(start, end - start), final, ref state);
                if (_section == Section.Done)
                    return !_malformed;
                if (final)
                    return false;
            }
        }

        private int Process(ReadOnlySpan<byte> data, bool final, ref JsonReaderState state)
        {
            var reader = new Utf8JsonReader(data, final, state);
            try
            {
                while (_section != Section.Done)
                {
                    var checkpoint = reader;
                    if (!reader.Read())
                        break;
                    if (!Step(ref reader))
                    {
                        // The value is not all in the buffer yet. It is read again with more data.
                        reader = checkpoint;
                        break;
                    }
                }
            }
            catch (JsonException)
            {
                _malformed = true;
                _section = Section.Done;
            }

            state = reader.CurrentState;
            return (int)reader.BytesConsumed;
        }

        /// <returns>False when the token starts a value that needs more data.</returns>
        private bool Step(ref Utf8JsonReader reader)
        {
            var token = reader.TokenType;
            var depth = reader.CurrentDepth;
            if (_skipDepth >= 0)
            {
                if (token is JsonTokenType.EndObject or JsonTokenType.EndArray && depth == _skipDepth)
                    _skipDepth = -1;
                return true;
            }
            if (_skipNext)
            {
                _skipNext = false;
                if (token is JsonTokenType.StartObject or JsonTokenType.StartArray)
                    _skipDepth = depth;
                return true;
            }

            switch (_section)
            {
                case Section.Root when depth == 0:
                    if (token == JsonTokenType.EndObject)
                        _section = Section.Done;
                    else if (token != JsonTokenType.StartObject)
                        Malformed();
                    return true;
                case Section.Root when token == JsonTokenType.PropertyName:
                    _property = reader.GetString();
                    if (_property is not ("products" or "terms" or "publicationDate"))
                        _skipNext = true;
                    return true;
                case Section.Root:
                    if (_property == "products" && token == JsonTokenType.StartObject)
                        _section = Section.Products;
                    else if (_property == "terms" && token == JsonTokenType.StartObject)
                        _section = Section.Terms;
                    else if (_property == "publicationDate" && token == JsonTokenType.String)
                        PublicationDate = Clean(reader.GetString(), 40);
                    else
                        Malformed();
                    _property = null;
                    return true;

                case Section.Products when token == JsonTokenType.EndObject && depth == 1:
                    _section = Section.Root;
                    return true;
                case Section.Products when token == JsonTokenType.PropertyName && depth == 2:
                    if (!JsonDocument.TryParseValue(ref reader, out var product))
                        return false;
                    using (product)
                        Keep(product!.RootElement);
                    return true;

                case Section.Terms when token == JsonTokenType.EndObject && depth == 1:
                    _section = Section.Root;
                    return true;
                case Section.Terms when token == JsonTokenType.PropertyName && depth == 2:
                    _onDemandNext = reader.ValueTextEquals("OnDemand"u8);
                    _skipNext = !_onDemandNext;
                    return true;
                case Section.Terms when token == JsonTokenType.StartObject && depth == 2 && _onDemandNext:
                    _onDemandNext = false;
                    _section = Section.OnDemand;
                    return true;

                // Everything that a price needs is read once the on-demand terms end.
                case Section.OnDemand when token == JsonTokenType.EndObject && depth == 2:
                    _section = Section.Done;
                    return true;
                case Section.OnDemand when token == JsonTokenType.PropertyName && depth == 3:
                    if (reader.GetString() is not { } sku || !_products.TryGetValue(sku, out var kept))
                    {
                        _skipNext = true;
                        return true;
                    }
                    if (!JsonDocument.TryParseValue(ref reader, out var terms))
                        return false;
                    using (terms)
                        Price(kept, terms!.RootElement);
                    return true;

                default:
                    Malformed();
                    return true;
            }
        }

        private void Malformed()
        {
            _malformed = true;
            _section = Section.Done;
        }

        private void Keep(JsonElement product)
        {
            if (product.ValueKind != JsonValueKind.Object || Text(product, "sku") is not { } sku)
                return;
            var family = Text(product, "productFamily") ?? "";
            var attributes = new SortedDictionary<string, string>(StringComparer.Ordinal);
            if (product.TryGetProperty("attributes", out var values) && values.ValueKind == JsonValueKind.Object)
            {
                foreach (var attribute in values.EnumerateObject())
                {
                    if (attributes.Count == MaxAttributes)
                        break;
                    if (attribute.Value.ValueKind == JsonValueKind.String && Clean(attribute.Name, 60) is { } name
                        && Clean(attribute.Value.GetString()) is { } value)
                        attributes[name] = value;
                }
            }

            if (filter.Skus is { } skus ? !skus.Contains(sku) : !Matches(sku, family, attributes))
                return;
            if (_products.Count == MaxItems)
            {
                More = true;
                return;
            }

            _products[sku] = new Product(sku, family, attributes);
        }

        private bool Matches(string sku, string family, IReadOnlyDictionary<string, string> attributes) =>
            filter.Words.All(word =>
                sku.Contains(word, StringComparison.OrdinalIgnoreCase)
                || family.Contains(word, StringComparison.OrdinalIgnoreCase)
                || attributes.Values.Any(v => v.Contains(word, StringComparison.OrdinalIgnoreCase)));

        /// <summary>
        /// The rows of the on-demand terms of one product. A term with more than one price
        /// dimension, or with a range that does not run from 0 to no end, has tiers.
        /// </summary>
        private void Price(Product product, JsonElement terms)
        {
            if (terms.ValueKind != JsonValueKind.Object)
                return;
            foreach (var term in terms.EnumerateObject().Select(t => t.Value))
            {
                if (term.ValueKind != JsonValueKind.Object
                    || !term.TryGetProperty("priceDimensions", out var dimensions) || dimensions.ValueKind != JsonValueKind.Object)
                    continue;
                DateOnly? effective = Text(term, "effectiveDate") is { } date
                                      && DateTimeOffset.TryParse(date, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)
                    ? DateOnly.FromDateTime(at.UtcDateTime)
                    : null;

                var all = dimensions.EnumerateObject().Select(d => d.Value).Where(d => d.ValueKind == JsonValueKind.Object).ToList();
                var tiered = all.Count > 1 || all.Any(d => Text(d, "beginRange") != "0" || Text(d, "endRange") != "Inf");
                foreach (var dimension in all)
                {
                    if (Rows.Count == MaxItems)
                    {
                        More = true;
                        return;
                    }
                    if (Row(product, dimension, effective, tiered) is { } row)
                        Rows.Add(row);
                }
            }
        }

        /// <summary>A dimension as a row, or null when it lacks what identifies a price: the rate code, the unit or one price in a currency.</summary>
        private static AwsPrice? Row(Product product, JsonElement dimension, DateOnly? effective, bool tiered)
        {
            if (Text(dimension, "rateCode") is not { } rateCode || !IsRateCode(rateCode)
                || Clean(Text(dimension, "unit"), UnitExpression.MaxChars) is not { } unit
                || !dimension.TryGetProperty("pricePerUnit", out var prices) || prices.ValueKind != JsonValueKind.Object)
                return null;

            var price = prices.EnumerateObject()
                .Where(p => PriceSources.IsCurrency(p.Name) && p.Value.ValueKind == JsonValueKind.String)
                .Select(p => (Currency: p.Name, Ok: decimal.TryParse(p.Value.GetString(), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value), Value: value))
                .FirstOrDefault(p => p.Ok);
            if (price.Currency is null)
                return null;

            var notImportable =
                tiered ? Tiered
                : PriceRules.UnitPrice("pricePerUnit", price.Value) is not null ? "The price is outside the range that Vellum stores."
                : null;
            return new AwsPrice(
                product.Sku, rateCode, product.Family, product.Attributes,
                Clean(Text(dimension, "description")) ?? "", Unit(unit), unit, price.Value, price.Currency,
                Clean(Text(dimension, "beginRange"), 40) ?? "", Clean(Text(dimension, "endRange"), 40) ?? "", effective, notImportable);
        }

        private static string? Text(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }
}
