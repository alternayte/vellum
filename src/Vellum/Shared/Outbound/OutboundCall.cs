using System.Net;

namespace Vellum.Shared.Outbound;

/// <summary>
/// A credential for one outbound request. It is read from the secret store by the caller and
/// put on the request by the pipeline. It prints as a fixed word, so it cannot reach a log
/// through string formatting.
/// </summary>
public sealed class OutboundCredential(string header, string? scheme, string secret)
{
    public string Header { get; } = header;

    internal void Apply(HttpRequestMessage request)
    {
        request.Headers.Remove(Header);
        request.Headers.TryAddWithoutValidation(Header, scheme is null ? secret : $"{scheme} {secret}");
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
/// <param name="Timeout">
/// The limit for the whole response. For a streamed response it is the limit for the wait
/// for headers and for each wait for more data.
/// </param>
public sealed record OutboundCall(string Provider, string Operation, Uri BaseUrl, bool AllowPrivateNetwork, TimeSpan Timeout)
{
    public const string PropertyName = "vellum.outbound.call";
    public const long DefaultMaxResponseBytes = 4 * 1024 * 1024;

    internal static readonly HttpRequestOptionsKey<OutboundCall> Key = new(PropertyName);
    internal static readonly HttpRequestOptionsKey<IReadOnlyList<IPAddress>> PinnedAddressesKey = new("vellum.outbound.pinned");
    internal static readonly HttpRequestOptionsKey<string> PathKey = new("vellum.outbound.path");
    internal static readonly HttpRequestOptionsKey<int> AttemptsKey = new("vellum.outbound.attempts");

    public OutboundCredential? Credential { get; init; }
    public long MaxResponseBytes { get; init; } = DefaultMaxResponseBytes;
    public bool Streaming { get; init; }

    /// <summary>A full address that replaces the base URL and the Refit path. Used to follow a redirect.</summary>
    public Uri? AbsoluteUri { get; init; }

    internal static OutboundCall From(HttpRequestMessage request) =>
        request.Options.TryGetValue(Key, out var call)
            ? call
            : throw new InvalidOperationException("An outbound request needs an OutboundCall. Nothing was sent.");
}

public enum OutboundFailure
{
    /// <summary>The outbound URL policy refused the destination.</summary>
    UrlRefused,
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
    public OutboundFailure Failure { get; } = failure;

    /// <summary>The policy's reason when the URL was refused. A fixed sentence.</summary>
    public string? Reason { get; } = reason;
}

/// <summary>The bounds of automatic retry. Tests replace <see cref="Delay"/> so that no test waits.</summary>
public sealed record OutboundLimits(int MaxAttempts, TimeSpan MaxTotalWait, TimeSpan BaseBackoff, Func<TimeSpan, CancellationToken, Task> Delay)
{
    public static readonly OutboundLimits Default = new(3, TimeSpan.FromSeconds(20), TimeSpan.FromMilliseconds(500), Task.Delay);
}
