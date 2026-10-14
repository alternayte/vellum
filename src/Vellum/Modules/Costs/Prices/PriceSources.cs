using Vellum.Shared;
using Vellum.Shared.Outbound;

namespace Vellum.Modules.Costs.Prices;

/// <summary>Why a read of a provider's price API gave no value. Callers see these and no HTTP type.</summary>
public abstract record PriceSourceFailure
{
    private PriceSourceFailure() { }

    /// <summary>The workspace has no usable address or no credential for the price API. <paramref name="Reason"/> says what to set.</summary>
    public sealed record NotConfigured(string? Reason) : PriceSourceFailure;
    /// <summary>The API refused the search or the credential.</summary>
    public sealed record BadFilter : PriceSourceFailure;
    public sealed record RateLimited : PriceSourceFailure;
    /// <summary>The API has no such service, region or offer.</summary>
    public sealed record NotFound : PriceSourceFailure;
    /// <param name="Reason">
    /// One of <c>url_refused</c>, <c>invalid_setting</c>, <c>timeout</c>, <c>network</c>,
    /// <c>server_error</c>, <c>too_large</c>, <c>next_page_refused</c>, <c>secret_unavailable</c>,
    /// <c>unexpected_response</c>.
    /// </param>
    public sealed record Unavailable(string Reason) : PriceSourceFailure;

    public static PriceSourceFailure FromStatus(int status) => status switch
    {
        400 or 401 or 403 => new BadFilter(),
        404 => new NotFound(),
        429 => new RateLimited(),
        >= 500 => new Unavailable("server_error"),
        _ => new Unavailable("unexpected_response"),
    };

    public static PriceSourceFailure From(OutboundException ex) => new Unavailable(ex.Failure switch
    {
        OutboundFailure.ResponseTooLarge => "too_large",
        OutboundFailure.UrlRefused => "url_refused",
        OutboundFailure.InvalidRequest => "invalid_setting",
        OutboundFailure.Timeout => "timeout",
        _ => "network",
    });
}

public readonly record struct PriceSourceResult<T>(T? Value, PriceSourceFailure? Failure) where T : class
{
    public static implicit operator PriceSourceResult<T>(T value) => new(value, null);
    public static implicit operator PriceSourceResult<T>(PriceSourceFailure failure) => new(null, failure);
}

/// <summary>What the price sources share: the answer to a failure and the cleaning of remote text.</summary>
public static class PriceSources
{
    /// <summary>Remote text for a client: cut, without control characters, and null when nothing is left.</summary>
    public static string? Clean(string? text, int maxLength) =>
        text is null ? null : OutboundText.Clean(text, maxLength).Trim() is { Length: > 0 } clean ? clean : null;

    /// <summary>
    /// A failure of a price API is a 4xx, 502 or 503 with a fixed sentence. No text of the
    /// remote answer is passed on.
    /// </summary>
    /// <param name="source">The name of the API in a sentence, for example <c>Azure</c>.</param>
    /// <param name="refusedHint">What to check when the API refused the request.</param>
    public static IResult Refuse(string source, PriceSourceFailure failure, string refusedHint) => failure switch
    {
        PriceSourceFailure.NotConfigured notConfigured => Results.Conflict(new ErrorResponse(
            "price_source_not_configured", $"The {source} price API is not set up for this workspace", notConfigured.Reason)),
        PriceSourceFailure.BadFilter => Results.BadRequest(new ErrorResponse(
            "price_source_refused", $"The {source} price API refused the request", refusedHint)),
        PriceSourceFailure.NotFound => Results.BadRequest(new ErrorResponse(
            "price_source_refused", $"The {source} price API has no such service or region", refusedHint)),
        PriceSourceFailure.RateLimited => Results.Json(new ErrorResponse(
            "price_source_rate_limited", $"The {source} price API limits the requests of this server", "Try again in a minute."),
            statusCode: StatusCodes.Status503ServiceUnavailable),
        PriceSourceFailure.Unavailable unavailable => Results.Json(new ErrorResponse(
            "price_source_unavailable", $"The {source} price API gave no usable answer", Describe(unavailable.Reason)),
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
        "secret_unavailable" => "The stored key cannot be decrypted with the loaded master key. Save the key again.",
        _ => "The answer of the price API is not the documented format.",
    };

    /// <summary>A refusal of an import: a picked row is gone, has tiers or cannot be stored. Nothing was stored.</summary>
    public static IResult NotImportable(string title, string detail) =>
        Results.Conflict(new ErrorResponse("conflict", title, detail));

    public static bool IsCurrency(string? code) => code is { Length: 3 } && code.All(c => c is >= 'A' and <= 'Z');
}
