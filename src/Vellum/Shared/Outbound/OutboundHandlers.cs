using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Sockets;

namespace Vellum.Shared.Outbound;

/// <summary>
/// Outermost handler. One span, one counter, one duration and one log line per call. They
/// hold the provider label, the operation label, the status class and the duration. They
/// hold no URL, no header and no body.
/// </summary>
public sealed class OutboundTelemetryHandler(ILogger<OutboundTelemetryHandler> logger) : DelegatingHandler
{
    public const string Name = "Vellum.Outbound";
    private static readonly ActivitySource Source = new(Name);
    private static readonly Meter Meter = new(Name);
    private static readonly Counter<long> Requests = Meter.CreateCounter<long>("vellum.outbound.requests");
    private static readonly Histogram<double> Duration = Meter.CreateHistogram<double>("vellum.outbound.duration", "ms");

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var call = OutboundCall.From(request);
        using var activity = Source.StartActivity($"{call.Provider} {call.Operation}", ActivityKind.Client);
        var started = Stopwatch.GetTimestamp();
        var outcome = "error";
        try
        {
            var response = await base.SendAsync(request, ct);
            outcome = $"{(int)response.StatusCode / 100}xx";
            return response;
        }
        catch (OutboundException ex)
        {
            outcome = ex.Failure.ToString();
            throw;
        }
        catch (OperationCanceledException)
        {
            outcome = "cancelled";
            throw;
        }
        finally
        {
            var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            var attempts = request.Options.TryGetValue(OutboundCall.AttemptsKey, out var count) ? count : 1;
            var tags = new TagList { { "provider", call.Provider }, { "operation", call.Operation }, { "status_class", outcome } };
            Requests.Add(1, tags);
            Duration.Record(elapsed, tags);
            activity?.SetTag("vellum.provider", call.Provider).SetTag("vellum.operation", call.Operation)
                .SetTag("vellum.status_class", outcome).SetTag("vellum.attempts", attempts);
            if (outcome is not ("2xx" or "3xx" or "cancelled"))
                activity?.SetStatus(ActivityStatusCode.Error);

            logger.Log(outcome is "2xx" or "3xx" ? LogLevel.Information : LogLevel.Warning,
                "Outbound {Provider} {Operation} ended {Outcome} in {DurationMs:0} ms after {Attempts} attempt(s)",
                call.Provider, call.Operation, outcome, elapsed, attempts);
        }
    }
}

/// <summary>
/// Bounded retry. A request is sent again only when the provider did not process it:
/// 429, 503 and 529 say so for any method, and a failed connection sent nothing. 502 and 504
/// leave that open, so only a read (GET or HEAD) is sent again after them. The wait is the
/// provider's <c>Retry-After</c> when it gives one, and else a doubling backoff with jitter.
/// The attempts and the total wait are capped; past either cap the last response goes back
/// to the caller.
/// </summary>
public sealed class OutboundRetryHandler(OutboundLimits limits) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var read = request.Method == HttpMethod.Get || request.Method == HttpMethod.Head;
        var waited = TimeSpan.Zero;
        var failedConnections = 0;
        for (var attempt = 1; ; attempt++)
        {
            request.Options.Set(OutboundCall.AttemptsKey, attempt);
            request.Options.Set(OutboundCall.FailedConnectionsKey, failedConnections);
            HttpResponseMessage? response = null;
            OutboundException? failure = null;
            try
            {
                response = await base.SendAsync(request, ct);
            }
            catch (OutboundException ex) when (ex.Failure == OutboundFailure.ConnectionFailed)
            {
                failure = ex;
                failedConnections++;
            }

            var again = response is null || (int)response.StatusCode is 429 or 503 or 529
                || (read && response.StatusCode is HttpStatusCode.BadGateway or HttpStatusCode.GatewayTimeout);
            if (!again || attempt >= limits.MaxAttempts)
                return response ?? throw failure!;

            // The server's own wait is kept as it is. The pipeline's backoff is shortened by
            // up to half, at random, so clients that failed together do not return together.
            var wait = (response is null ? null : RetryAfter(response))
                       ?? limits.BaseBackoff * (1 << (attempt - 1)) * (1 - Math.Clamp(limits.Jitter(), 0, 1) / 2);
            if (waited + wait > limits.MaxTotalWait)
                return response ?? throw failure!;

            response?.Dispose();
            waited += wait;
            await limits.Delay(wait, ct);
        }
    }

    private static TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        var wait = header?.Delta ?? (header?.Date is { } date ? date - DateTimeOffset.UtcNow : null);
        return wait is null ? null : wait < TimeSpan.Zero ? TimeSpan.Zero : wait;
    }
}

/// <summary>
/// Innermost handler; it runs for every attempt, so a retry and a followed redirect pass the
/// same checks. It builds the address from the base URL in settings, refuses an address or a
/// header that holds a control character, checks the address with
/// <see cref="OutboundUrlPolicy"/> (private networks and https as the call demands), fixes
/// the addresses the connection may use, adds the credential for the send only, applies the
/// timeouts and caps the response size.
/// </summary>
public sealed class OutboundAttemptHandler(OutboundUrlPolicy policy, HostResolver resolve, TimeProvider time) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var call = OutboundCall.From(request);

        // The Refit client has a placeholder base address. The path it built is kept, because
        // a retry sees the request after its address was replaced.
        if (!request.Options.TryGetValue(OutboundCall.PathKey, out var path))
        {
            path = request.RequestUri!.PathAndQuery;
            request.Options.Set(OutboundCall.PathKey, path);
        }

        var target = call.AbsoluteUri ?? new Uri(call.BaseUrl.GetLeftPart(UriPartial.Path).TrimEnd('/') + path);
        RefuseUnsafeText(request, target, call);

        var verdict = await policy.CheckAsync(target.AbsoluteUri, call.AllowPrivateNetwork, call.RequireHttps, ct);
        if (!verdict.Allowed)
            throw new OutboundException(OutboundFailure.UrlRefused, call, verdict.Reason);

        // The policy does not resolve a name that it allows anyway. The name is resolved here
        // once, so the connection goes to these addresses and a later answer of the name
        // cannot move it.
        var addresses = verdict.Addresses.Count > 0 ? verdict.Addresses : await ResolveAsync(target, call, ct);
        var failedConnections = request.Options.TryGetValue(OutboundCall.FailedConnectionsKey, out var count) ? count : 0;

        request.RequestUri = target;
        request.Options.Set(OutboundCall.PinnedAddressesKey, addresses);
        // One address per attempt. A retry after a failed connection takes the next one. A
        // retry after an answer stays on the address that answered.
        request.Options.Set(OutboundCall.ChosenAddressKey, addresses[failedConnections % addresses.Count]);

        // Until the headers arrive the limit is the timeout. After them, a streamed response
        // gets its total limit and a whole response keeps the time that is left.
        var limit = new CancellationTokenSource(call.Timeout, time);
        HttpResponseMessage response;
        try
        {
            using var sending = CancellationTokenSource.CreateLinkedTokenSource(ct, limit.Token);
            call.Credential?.Apply(request, call);
            response = await base.SendAsync(request, sending.Token);
        }
        catch (Exception ex)
        {
            limit.Dispose();
            if (ex is OutboundException || (ex is OperationCanceledException && ct.IsCancellationRequested))
                throw;

            throw ex switch
            {
                OperationCanceledException => new OutboundException(OutboundFailure.Timeout, call),
                HttpRequestException
                {
                    HttpRequestError: HttpRequestError.ConnectionError or HttpRequestError.NameResolutionError
                        or HttpRequestError.SecureConnectionError or HttpRequestError.ProxyTunnelError
                } => new OutboundException(OutboundFailure.ConnectionFailed, call),
                HttpRequestException or IOException => new OutboundException(OutboundFailure.ConnectionLost, call),
                _ => ex,
            };
        }
        finally
        {
            // The request object outlives the send. The credential does not stay on it.
            if (call.Credential is { } credential)
                request.Headers.Remove(credential.Header);
        }

        if (response.Content.Headers.ContentLength > call.MaxResponseBytes)
        {
            response.Dispose();
            limit.Dispose();
            throw new OutboundException(OutboundFailure.ResponseTooLarge, call);
        }

        if (call.Streaming)
        {
            limit.Dispose();
            limit = new CancellationTokenSource(call.MaxStreamDuration, time);
        }

        var guarded = new GuardedContent(response.Content, call, time, limit, ct);
        foreach (var header in response.Content.Headers)
            guarded.Headers.TryAddWithoutValidation(header.Key, header.Value);
        response.Content = guarded;
        return response;
    }

    /// <summary>
    /// A header value or an address with a control character is never sent. The address is
    /// checked in its decoded form, so an encoded line break in a path part is found too.
    /// </summary>
    private static void RefuseUnsafeText(HttpRequestMessage request, Uri target, OutboundCall call)
    {
        string decoded;
        try
        {
            decoded = Uri.UnescapeDataString(target.GetComponents(UriComponents.PathAndQuery, UriFormat.UriEscaped));
        }
        catch (UriFormatException)
        {
            throw new OutboundException(OutboundFailure.InvalidRequest, call, OutboundException.BadAddress);
        }

        if (OutboundText.HasControlCharacter(target.OriginalString) || OutboundText.HasControlCharacter(decoded))
            throw new OutboundException(OutboundFailure.InvalidRequest, call, OutboundException.BadAddress);

        var headers = request.Content is null
            ? request.Headers.NonValidated.AsEnumerable()
            : request.Headers.NonValidated.Concat(request.Content.Headers.NonValidated);
        foreach (var header in headers)
        {
            foreach (var value in header.Value)
            {
                if (OutboundText.HasControlCharacter(value))
                    throw new OutboundException(OutboundFailure.InvalidRequest, call, OutboundException.BadHeader);
            }
        }
    }

    private async Task<IReadOnlyList<IPAddress>> ResolveAsync(Uri target, OutboundCall call, CancellationToken ct)
    {
        try
        {
            var addresses = await resolve(target.IdnHost.TrimEnd('.'), ct);
            if (addresses.Length > 0)
                return addresses;
        }
        catch (Exception ex) when (ex is SocketException or ArgumentException)
        {
            // The name does not resolve: no connection can be made. The failure below says so.
        }

        throw new OutboundException(OutboundFailure.ConnectionFailed, call);
    }

    /// <summary>The response body behind the size cap and the time limits.</summary>
    private sealed class GuardedContent(HttpContent inner, OutboundCall call, TimeProvider time, CancellationTokenSource limit, CancellationToken caller)
        : HttpContent
    {
        protected override async Task<Stream> CreateContentReadStreamAsync() =>
            new GuardedStream(await inner.ReadAsStreamAsync(caller), call, time, limit, caller);

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            await using var source = await CreateContentReadStreamAsync();
            await source.CopyToAsync(stream, caller);
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
                limit.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// <paramref name="limit"/> ends the whole body: the time left of the timeout for a whole
    /// response, the total duration for a stream. A stream has a second limit for each read:
    /// the timeout, counted only while the read waits for the server. Bytes that already
    /// arrived are returned at once, so a slow reader never causes a timeout.
    /// </summary>
    private sealed class GuardedStream(Stream inner, OutboundCall call, TimeProvider time, CancellationTokenSource limit, CancellationToken caller) : Stream
    {
        private long _read;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _read; set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            using var idle = call.Streaming ? new CancellationTokenSource(call.Timeout, time) : null;
            try
            {
                using var waiting = CancellationTokenSource.CreateLinkedTokenSource(ct, caller, limit.Token, idle?.Token ?? CancellationToken.None);
                // A limit that already passed ends the body even when bytes are buffered.
                waiting.Token.ThrowIfCancellationRequested();
                var count = await inner.ReadAsync(buffer, waiting.Token);
                _read += count;
                return _read > call.MaxResponseBytes
                    ? throw new OutboundException(OutboundFailure.ResponseTooLarge, call)
                    : count;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested && !caller.IsCancellationRequested)
            {
                throw new OutboundException(OutboundFailure.Timeout, call);
            }
            catch (ObjectDisposedException) when (!ct.IsCancellationRequested && !caller.IsCancellationRequested)
            {
                throw new OutboundException(OutboundFailure.ConnectionLost, call);
            }
            catch (Exception ex) when (ex is IOException or HttpRequestException)
            {
                throw new OutboundException(OutboundFailure.ConnectionLost, call);
            }
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
