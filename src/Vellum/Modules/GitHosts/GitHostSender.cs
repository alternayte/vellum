using System.Globalization;
using System.Text.RegularExpressions;
using Vellum.Modules.GitHub;
using Vellum.Shared.Outbound;

namespace Vellum.Modules.GitHosts;

/// <summary>
/// Sends the reads of one git host adapter through the outbound pipeline and turns the answer
/// into a <see cref="GitHubResult{T}"/>. The rules are those of the GitHub reads: a redirect
/// is followed only on the configured API origin, so the token goes nowhere else; a transport
/// fault and an HTTP failure become a <see cref="GitHubFailure"/>.
/// </summary>
/// <param name="provider">The telemetry label: <c>gitlab</c>, <c>bitbucket</c> or <c>azure-devops</c>.</param>
/// <param name="follow">The adapter's Refit method that repeats a read at <see cref="OutboundCall.AbsoluteUri"/>.</param>
/// <param name="resetHeader">The header that gives the end of a rate limit as a Unix time, or null when the host sends none.</param>
/// <param name="nonAuthoritativeIsSignIn">
/// True for Azure DevOps. It answers a request it does not accept with 203 and an HTML
/// sign-in page, which is a refused token and not a value.
/// </param>
internal sealed class GitHostSender(
    GitHostContext context, string provider, Func<OutboundCall, CancellationToken, Task<HttpResponseMessage>> follow,
    string? resetHeader, bool nonAuthoritativeIsSignIn = false)
{
    private const int MaxRedirects = 3;
    private const long MaxEpochSeconds = 253402300799;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DefaultLimitWait = TimeSpan.FromMinutes(1);

    public bool HasToken => context.Credential is not null;

    /// <summary>A call for a read at a full address on the API origin, such as the next page a host links to.</summary>
    public OutboundCall At(OutboundCall call, Uri address) => call with { AbsoluteUri = address };

    /// <summary>True when <paramref name="address"/> has the scheme, the host and the port of the configured API base URL.</summary>
    public bool OnOrigin(Uri address) =>
        address.IsAbsoluteUri && address.Scheme == context.BaseUrl.Scheme && address.Port == context.BaseUrl.Port
        && string.Equals(address.IdnHost, context.BaseUrl.IdnHost, StringComparison.OrdinalIgnoreCase);

    public async Task<GitHubResult<T>> SendAsync<T>(
        string operation,
        Func<OutboundCall, Task<HttpResponseMessage>> send,
        Func<HttpResponseMessage, Task<GitHubResult<T>>> read,
        CancellationToken ct,
        long maxBytes = OutboundCall.DefaultMaxResponseBytes)
    {
        var call = new OutboundCall(provider, operation, context.BaseUrl, context.AllowPrivateNetwork, context.RequireHttps, Timeout)
        {
            Credential = context.Credential,
            MaxResponseBytes = maxBytes,
        };

        try
        {
            var response = await OutboundSend.RunAsync(() => send(call));
            for (var hop = 0; (int)response.StatusCode is 301 or 302 or 307 or 308; hop++)
            {
                // A moved project or repository is redirected. The pipeline follows nothing by
                // itself. A target on the configured API origin is followed here, and the
                // pipeline checks it again with the same private-network and https rules.
                var from = response.RequestMessage?.RequestUri;
                var location = response.Headers.Location;
                response.Dispose();
                if (hop == MaxRedirects || from is null || location is null)
                    return new GitHubFailure.Unavailable("redirect_refused");

                var target = location.IsAbsoluteUri ? location : new Uri(from, location);
                if (!OnOrigin(target))
                    return new GitHubFailure.Unavailable("redirect_refused");

                var next = At(call, target);
                response = await OutboundSend.RunAsync(() => follow(next, ct));
            }

            using (response)
            {
                if (nonAuthoritativeIsSignIn && (int)response.StatusCode == 203)
                    return new GitHubFailure.Unauthorized();
                return response.IsSuccessStatusCode ? await read(response) : Failure(response, context.Time.GetUtcNow());
            }
        }
        catch (OutboundException ex)
        {
            return ex.Failure switch
            {
                OutboundFailure.ResponseTooLarge => new GitHubFailure.TooLarge(),
                OutboundFailure.UrlRefused => new GitHubFailure.Unavailable("url_refused"),
                OutboundFailure.InvalidRequest => new GitHubFailure.Unavailable("invalid_setting"),
                OutboundFailure.Timeout => new GitHubFailure.Unavailable("timeout"),
                _ => new GitHubFailure.Unavailable("network"),
            };
        }
    }

    /// <summary>
    /// A rate limit is a 429. The wait is the one <c>Retry-After</c> gives, else the reset time
    /// of the host's own header, else one minute.
    /// </summary>
    private GitHubFailure Failure(HttpResponseMessage response, DateTimeOffset now)
    {
        var status = (int)response.StatusCode;
        if (status == 429)
        {
            var retryAfter = response.Headers.RetryAfter;
            var wait = retryAfter?.Delta ?? (retryAfter?.Date is { } date ? date - now : null);
            return new GitHubFailure.RateLimited(
                wait is { } delta ? now + (delta < TimeSpan.Zero ? TimeSpan.Zero : delta)
                : resetHeader is not null && HeaderLong(response, resetHeader) is { } epoch ? FromEpoch(epoch)
                : now + DefaultLimitWait);
        }

        return status switch
        {
            401 => new GitHubFailure.Unauthorized(),
            403 or 451 => new GitHubFailure.Forbidden(),
            404 or 410 => new GitHubFailure.NotFound(),
            >= 500 => new GitHubFailure.Unavailable("server_error"),
            _ => GitHostText.Unexpected,
        };
    }

    public static long? HeaderLong(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values)
        && long.TryParse(values.FirstOrDefault(), NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;

    private static DateTimeOffset? FromEpoch(long seconds) =>
        seconds is >= 0 and <= MaxEpochSeconds ? DateTimeOffset.FromUnixTimeSeconds(seconds) : null;

    /// <summary>
    /// The whole body as bytes. The pipeline stops a body over the cap of the call with
    /// <see cref="OutboundFailure.ResponseTooLarge"/>, which <see cref="SendAsync{T}"/> reports as
    /// <see cref="GitHubFailure.TooLarge"/>.
    /// </summary>
    public static async Task<byte[]> ReadBytesAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, ct);
        return buffer.ToArray();
    }
}

/// <summary>The checks of names before they are sent, and the cleaning of short remote texts.</summary>
internal static partial class GitHostText
{
    public const int MaxNameChars = 300;
    public const int MaxRefChars = 255;
    public const int MaxWordChars = 40;
    public const int MaxLoginChars = 100;
    public const int MaxPathChars = 4096;

    /// <summary>The most entries a file list holds; a larger list is cut (<see cref="GitHubFailure.Truncated"/>).</summary>
    public const int MaxEntries = 100_000;

    /// <summary>The cap of one page of a file list or of a repository listing.</summary>
    public const long PageMaxBytes = 16 * 1024 * 1024;

    public const int RepositoriesPerPage = 100;
    public const int MaxRepositoryPages = 100;

    public static GitHubFailure Unexpected => new GitHubFailure.Unavailable("unexpected_response");

    public static Task<GitHubResult<T>> NotFound<T>() => Task.FromResult<GitHubResult<T>>(new GitHubFailure.NotFound());

    /// <summary>Remote text for a client: cut, without control characters, and null when nothing is left.</summary>
    public static string? Clean(string? text, int maxLength) =>
        text is null ? null : OutboundText.Clean(text, maxLength) is { Length: > 0 } clean ? clean : null;

    /// <summary>The parts of a locator of <paramref name="host"/>, or null when it is not one.</summary>
    public static string[]? Parts(GitHostInfo host, string? locator) =>
        locator is not null && !locator.Contains(':', StringComparison.Ordinal) && host.TryReadLocator(locator, "", out var read, out _)
            ? read.Split('/')
            : null;

    /// <summary>The locator a repository listing names, or null when it cannot be one.</summary>
    public static string? Locator(GitHostInfo host, string? name) =>
        Parts(host, name) is { } parts ? string.Join('/', parts) : null;

    public static bool IsSha(string? value) => value is not null && Sha().IsMatch(value);

    /// <summary>
    /// The rules of <c>git check-ref-format</c> that matter for one ref name: no control
    /// character, no space, none of <c>~ ^ : ? * [ \ "</c>, no <c>..</c>, no <c>@{</c>, no empty
    /// part between slashes, no part that starts with a dot or ends with <c>.lock</c>, and no
    /// dot at the end. A name that passes is one ref and is sent as one encoded value.
    /// </summary>
    public static bool IsRef(string? value) =>
        value is { Length: >= 1 and <= MaxRefChars } and not "@"
        && !OutboundText.HasControlCharacter(value)
        && !value.Any(c => char.IsWhiteSpace(c) || c is '~' or '^' or ':' or '?' or '*' or '[' or '\\' or '"')
        && !value.Contains("..", StringComparison.Ordinal) && !value.Contains("@{", StringComparison.Ordinal)
        && !value.EndsWith('.')
        && value.Split('/').All(part => part.Length > 0 && part[0] != '.' && !part.EndsWith(".lock", StringComparison.Ordinal));

    /// <summary>
    /// A file path that is sent as path parts: no control character, no empty part, and no
    /// <c>.</c> or <c>..</c> part that would make the address another one.
    /// </summary>
    public static bool IsPath(string? value) =>
        value is { Length: >= 1 and <= MaxPathChars }
        && !OutboundText.HasControlCharacter(value)
        && value.Split('/').All(part => part is not ("" or "." or ".."));

    // \z is the end of the text. $ also matches before a final line break.
    [GeneratedRegex(@"\A(?:[0-9a-f]{40}|[0-9a-f]{64})\z")]
    private static partial Regex Sha();
}
