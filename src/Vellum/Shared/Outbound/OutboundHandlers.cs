using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;

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
/// provider's <c>Retry-After</c> when it gives one. The attempts and the total wait are capped;
/// past either cap the last response goes back to the caller.
/// </summary>
public sealed class OutboundRetryHandler(OutboundLimits limits) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var read = request.Method == HttpMethod.Get || request.Method == HttpMethod.Head;
        var waited = TimeSpan.Zero;
        for (var attempt = 1; ; attempt++)
        {
            request.Options.Set(OutboundCall.AttemptsKey, attempt);
            HttpResponseMessage? response = null;
            OutboundException? failure = null;
            try
            {
                response = await base.SendAsync(request, ct);
            }
            catch (OutboundException ex) when (ex.Failure == OutboundFailure.ConnectionFailed)
            {
                failure = ex;
            }

            var again = response is null || (int)response.StatusCode is 429 or 503 or 529
                || (read && response.StatusCode is HttpStatusCode.BadGateway or HttpStatusCode.GatewayTimeout);
            var wait = (response is null ? null : RetryAfter(response)) ?? limits.BaseBackoff * (1 << (attempt - 1));

            if (!again || attempt >= limits.MaxAttempts || waited + wait > limits.MaxTotalWait)
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
/// Innermost handler; it runs for every attempt. It builds the address from the base URL in
/// settings, checks it with <see cref="OutboundUrlPolicy"/> and passes the checked addresses
/// to the connection, adds the credential for the send only, applies the timeout and caps the
/// response size.
/// </summary>
public sealed class OutboundAttemptHandler(OutboundUrlPolicy policy) : DelegatingHandler
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
        var verdict = await policy.CheckAsync(target.AbsoluteUri, call.AllowPrivateNetwork, ct);
        if (!verdict.Allowed)
            throw new OutboundException(OutboundFailure.UrlRefused, call, verdict.Reason);

        request.RequestUri = target;
        request.Options.Set(OutboundCall.PinnedAddressesKey, verdict.Addresses);

        var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(call.Timeout);
        HttpResponseMessage response;
        try
        {
            call.Credential?.Apply(request);
            response = await base.SendAsync(request, deadline.Token);
        }
        catch (Exception ex)
        {
            deadline.Dispose();
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
            deadline.Dispose();
            throw new OutboundException(OutboundFailure.ResponseTooLarge, call);
        }

        var guarded = new GuardedContent(response.Content, call, deadline, ct);
        foreach (var header in response.Content.Headers)
            guarded.Headers.TryAddWithoutValidation(header.Key, header.Value);
        response.Content = guarded;
        return response;
    }

    /// <summary>The response body behind the size cap and the timeout.</summary>
    private sealed class GuardedContent(HttpContent inner, OutboundCall call, CancellationTokenSource deadline, CancellationToken caller)
        : HttpContent
    {
        protected override async Task<Stream> CreateContentReadStreamAsync() =>
            new GuardedStream(await inner.ReadAsStreamAsync(caller), call, deadline, caller);

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
                deadline.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    private sealed class GuardedStream(Stream inner, OutboundCall call, CancellationTokenSource deadline, CancellationToken caller) : Stream
    {
        private long _read;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _read; set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            using var linked = ct.CanBeCanceled && ct != caller
                ? CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token)
                : null;
            try
            {
                if (call.Streaming)
                    deadline.CancelAfter(call.Timeout);

                var count = await inner.ReadAsync(buffer, linked?.Token ?? deadline.Token);
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
