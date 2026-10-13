using System.Net;

namespace Vellum.Shared.Outbound;

/// <summary>
/// The rules for text that the pipeline puts into a request. A stored secret, a model name or
/// a path part comes from a user or from a remote system; none of it can add a header or
/// change the request line.
/// </summary>
public static class OutboundText
{
    /// <summary>True when the text holds a control character: C0, DEL, C1, or a line or paragraph separator.</summary>
    public static bool HasControlCharacter(ReadOnlySpan<char> text)
    {
        foreach (var c in text)
        {
            if (char.IsControl(c) || c is '\u2028' or '\u2029')
                return true;
        }

        return false;
    }

    /// <summary>True when every character is printable ASCII (space to tilde), which is what one header value can hold.</summary>
    public static bool IsHeaderValue(ReadOnlySpan<char> text) => text.IndexOfAnyExceptInRange(' ', '~') < 0;

    /// <summary>The text without control characters, cut to <paramref name="maxLength"/>. For remote text that a client sees.</summary>
    public static string Clean(string text, int maxLength)
    {
        var clean = HasControlCharacter(text) ? string.Concat(text.Where(c => !char.IsControl(c) && c is not ('\u2028' or '\u2029'))) : text;
        if (clean.Length <= maxLength)
            return clean;
        // A surrogate pair is not cut in two.
        return char.IsHighSurrogate(clean[maxLength - 1]) ? clean[..(maxLength - 1)] : clean[..maxLength];
    }
}

/// <summary>
/// A credential for one outbound request. It is read from the secret store by the caller and
/// put on the request by the pipeline. It prints as a fixed word, so it cannot reach a log
/// through string formatting.
/// </summary>
public sealed class OutboundCredential(string header, string? scheme, string secret)
{
    public string Header { get; } = header;

    /// <summary>
    /// Adds the credential through the validating header API. A value that is not one header
    /// value (a line break, another control character, a character outside ASCII) is refused
    /// before anything is sent.
    /// </summary>
    internal void Apply(HttpRequestMessage request, OutboundCall call)
    {
        if (secret.Length == 0 || !OutboundText.IsHeaderValue(secret))
            throw new OutboundException(OutboundFailure.InvalidRequest, call, OutboundException.BadCredential);

        request.Headers.Remove(Header);
        try
        {
            request.Headers.Add(Header, scheme is null ? secret : $"{scheme} {secret}");
        }
        catch (FormatException)
        {
            // The header's own parser refused the value. Its message can repeat the value, so it is not kept.
            throw new OutboundException(OutboundFailure.InvalidRequest, call, OutboundException.BadCredential);
        }
    }

    public override string ToString() => "[credential]";
}

/// <summary>
/// What the outbound pipeline needs to know about one request. Every Refit method takes one
/// as a <c>[Property]</c> parameter; a request without it is not sent.
/// </summary>
/// <param name="Provider">A short fixed label for telemetry, for example <c>github</c>.</param>
/// <param name="Operation">A short fixed label for telemetry, for example <c>get-tree</c>.</param>
/// <param name="BaseUrl">The address from settings. The Refit path is appended to it.</param>
/// <param name="AllowPrivateNetwork">The instance policy <c>allowPrivateNetworkUrls</c>.</param>
/// <param name="RequireHttps">
/// <c>WorkspaceSettings.RequireHttps</c>: true on a hosted instance. Every attempt, a retry
/// and a followed redirect included, then refuses plain http unless every address of the host
/// is private and <paramref name="AllowPrivateNetwork"/> is true.
/// </param>
/// <param name="Timeout">
/// The limit for the whole response. For a streamed response it is the limit for the wait
/// for headers and for each wait for more data from the server; the time the reader spends
/// between reads does not count.
/// </param>
public sealed record OutboundCall(string Provider, string Operation, Uri BaseUrl, bool AllowPrivateNetwork, bool RequireHttps, TimeSpan Timeout)
{
    public const string PropertyName = "vellum.outbound.call";
    public const long DefaultMaxResponseBytes = 4 * 1024 * 1024;

    /// <summary>
    /// The longest a streamed response stays open, measured from its headers. Keep-alive
    /// lines reset the wait for data, so only this limit ends a stream that never finishes.
    /// </summary>
    public static readonly TimeSpan DefaultMaxStreamDuration = TimeSpan.FromMinutes(30);

    internal static readonly HttpRequestOptionsKey<OutboundCall> Key = new(PropertyName);
    internal static readonly HttpRequestOptionsKey<IReadOnlyList<IPAddress>> PinnedAddressesKey = new("vellum.outbound.pinned");
    internal static readonly HttpRequestOptionsKey<IPAddress> ChosenAddressKey = new("vellum.outbound.address");
    internal static readonly HttpRequestOptionsKey<string> PathKey = new("vellum.outbound.path");
    internal static readonly HttpRequestOptionsKey<int> AttemptsKey = new("vellum.outbound.attempts");

    public OutboundCredential? Credential { get; init; }
    public long MaxResponseBytes { get; init; } = DefaultMaxResponseBytes;
    public bool Streaming { get; init; }
    public TimeSpan MaxStreamDuration { get; init; } = DefaultMaxStreamDuration;

    /// <summary>A full address that replaces the base URL and the Refit path. Used to follow a redirect.</summary>
    public Uri? AbsoluteUri { get; init; }

    internal static OutboundCall From(HttpRequestMessage request) =>
        request.Options.TryGetValue(Key, out var call)
            ? call
            : throw new InvalidOperationException("An outbound request needs an OutboundCall. Nothing was sent.");

    /// <summary>
    /// True when the response came from an address on the public internet. False when any
    /// address the request was checked for is private, and when the addresses are not known.
    /// A caller uses it to decide whether text from the response may be shown to a user.
    /// </summary>
    public static bool CameFromPublicNetwork(HttpResponseMessage response) =>
        response.RequestMessage is { } request
        && request.Options.TryGetValue(PinnedAddressesKey, out var addresses)
        && addresses.Count > 0
        && addresses.All(OutboundUrlPolicy.IsPublic);
}

public enum OutboundFailure
{
    /// <summary>The outbound URL policy refused the destination.</summary>
    UrlRefused,
    /// <summary>
    /// A configured value cannot be sent safely: the credential, a header value or a part of
    /// the address holds a control character or text that is not one header value. Nothing was sent.
    /// </summary>
    InvalidRequest,
    Timeout,
    /// <summary>No connection was made, so the request was not sent.</summary>
    ConnectionFailed,
    /// <summary>The connection broke after the request was sent.</summary>
    ConnectionLost,
    ResponseTooLarge,
}

/// <summary>
/// The only exception the pipeline raises for a transport fault. Its message holds the
/// provider label, the operation label and the failure. It never holds a URL, a header or
/// any part of a body.
/// </summary>
public sealed class OutboundException(OutboundFailure failure, OutboundCall call, string? reason = null)
    : Exception($"Outbound call {call.Provider}/{call.Operation} failed: {failure}{(reason is null ? "" : $" ({reason})")}.")
{
    internal const string BadCredential =
        "The stored credential holds a line break, another control character or a character outside ASCII. Save the credential again.";
    internal const string BadHeader = "A request header holds a line break or another control character.";
    internal const string BadAddress =
        "The address holds a line break or another control character. Check the base URL, the model name and the names in the request.";

    public OutboundFailure Failure { get; } = failure;

    /// <summary>The policy's reason when the URL was refused, or what to change when a value cannot be sent. A fixed sentence.</summary>
    public string? Reason { get; } = reason;
}

/// <summary>
/// The bounds of automatic retry. Tests replace <see cref="Delay"/> so that no test waits and
/// <see cref="Jitter"/> so that the waits are known.
/// </summary>
/// <param name="Jitter">
/// A number from 0 up to 1 for each wait. The pipeline's own backoff is shortened by up to
/// half with it, so clients that failed together do not come back together. A wait that the
/// server asked for with <c>Retry-After</c> is kept as it is.
/// </param>
public sealed record OutboundLimits(
    int MaxAttempts, TimeSpan MaxTotalWait, TimeSpan BaseBackoff, Func<TimeSpan, CancellationToken, Task> Delay, Func<double> Jitter)
{
    public static readonly OutboundLimits Default =
        new(3, TimeSpan.FromSeconds(20), TimeSpan.FromMilliseconds(500), Task.Delay, Random.Shared.NextDouble);
}
