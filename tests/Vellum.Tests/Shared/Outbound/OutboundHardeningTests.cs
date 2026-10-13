using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Vellum.Modules.GitHub;
using Vellum.Shared;
using Vellum.Shared.Outbound;

namespace Vellum.Tests.Shared.Outbound;

/// <summary>A clock that moves only when a test moves it. Timers fire inside <see cref="Advance"/>.</summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<ManualTimer> _timers = [];
    private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
            return _now;
    }

    public override long GetTimestamp() => GetUtcNow().Ticks;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }

    public void Advance(TimeSpan by)
    {
        List<ManualTimer> due;
        lock (_gate)
        {
            _now += by;
            due = _timers.Where(t => t.Due <= _now).ToList();
            foreach (var timer in due)
            {
                timer.Due = null;
                _timers.Remove(timer);
            }
        }

        foreach (var timer in due)
            timer.Fire();
    }

    private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        public DateTimeOffset? Due { get; set; }

        public void Fire() => callback(state);

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner._gate)
            {
                owner._timers.Remove(this);
                Due = dueTime == Timeout.InfiniteTimeSpan ? null : owner._now + dueTime;
                if (Due is not null)
                    owner._timers.Add(this);
            }

            return true;
        }

        public void Dispose() => Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>A response body that gives a chunk only when the test sends one, and says when a read waits.</summary>
internal sealed class GatedStream : Stream
{
    private readonly Channel<byte[]> _chunks = Channel.CreateUnbounded<byte[]>();
    private readonly SemaphoreSlim _waiting = new(0);

    public void Send(int bytes) => _chunks.Writer.TryWrite(new byte[bytes]);

    /// <summary>Completes when a read found no chunk and waits for the server.</summary>
    public Task ReadIsWaitingAsync() => _waiting.WaitAsync(TimeSpan.FromSeconds(30));

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        if (!_chunks.Reader.TryRead(out var chunk))
        {
            _waiting.Release();
            chunk = await _chunks.Reader.ReadAsync(ct);
        }

        chunk.CopyTo(buffer);
        return chunk.Length;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
        ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>
/// The pipeline against hostile configuration and a hostile network: values that would add a
/// header, plain http on an instance that needs https, a pooled connection to an address the
/// request was not checked for, a slow reader, a stream that never ends and retry timing.
/// </summary>
public class OutboundHardeningTests
{
    private const string AddressA = "93.184.216.34";
    private const string AddressB = "93.184.216.35";
    private const string PrivateAddress = "10.0.0.5";

    private static ServiceProvider Build(HostResolver resolver, Action<IServiceCollection>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(resolver);
        services.AddSingleton<OutboundUrlPolicy>();
        services.AddSingleton(OutboundLimits.Default with { Delay = (_, _) => Task.CompletedTask, Jitter = () => 0 });
        configure?.Invoke(services);
        services.AddOutbound();
        services.AddOutboundClient<IGitHubApi>();
        return services.BuildServiceProvider();
    }

    private static ServiceProvider BuildOver(StubTransport stub, HostResolver resolver, Action<IServiceCollection>? configure = null) =>
        Build(resolver, services =>
        {
            services.AddSingleton<OutboundPrimaryHandlerFactory>(_ => () => stub);
            configure?.Invoke(services);
        });

    private static HostResolver Fixed(params string[] addresses) => (_, _) => Task.FromResult(addresses.Select(IPAddress.Parse).ToArray());

    private static OutboundCall Call(string baseUrl, string? secret, bool allowPrivateNetwork = false, bool requireHttps = false) =>
        new("github", "rate-limit", new Uri(baseUrl), allowPrivateNetwork, requireHttps, TimeSpan.FromSeconds(60))
        {
            Credential = secret is null ? null : new OutboundCredential("Authorization", "Bearer", secret),
        };

    private static Task<OutboundException> RefusedAsync(Func<Task<HttpResponseMessage>> send) =>
        Assert.ThrowsAsync<OutboundException>(() => OutboundSend.RunAsync(send));

    // ---- Header injection ----

    [Theory]
    [InlineData("tok\r\nX-Evil: 1")]
    [InlineData("tok\nX-Evil: 1")]
    [InlineData("tok\0")]
    [InlineData("tok\u007f")]
    [InlineData("tok\u0085next")]
    [InlineData("tök")]
    [InlineData("")]
    public async Task A_credential_that_cannot_be_one_header_value_is_refused_and_nothing_is_sent(string secret)
    {
        var stub = new StubTransport().Json("{}");
        await using var provider = BuildOver(stub, Fixed(AddressA));

        var refused = await RefusedAsync(() => provider.GetRequiredService<IGitHubApi>().GetRateLimit(Call("https://api.example", secret), default));

        // A typed failure with a clear reason. It is not a network failure, so nothing retries it.
        Assert.Equal(OutboundFailure.InvalidRequest, refused.Failure);
        Assert.Contains("Save the credential again", refused.Reason);
        Assert.Empty(stub.Requests);
        Assert.DoesNotContain("X-Evil", refused.ToString());
        Assert.DoesNotContain("tok", refused.ToString());
    }

    [Fact]
    public async Task A_path_part_or_a_base_url_with_a_control_character_is_refused_and_nothing_is_sent()
    {
        var stub = new StubTransport().Json("{}");
        await using var provider = BuildOver(stub, Fixed(AddressA));
        var api = provider.GetRequiredService<IGitHubApi>();
        var call = Call("https://api.example", "secret-value");

        // Refit encodes a path part. The pipeline reads the address decoded, so the encoded line break is found.
        var byOwner = await RefusedAsync(() => api.GetRepository("acme\r\nX-Evil: 1", "orders", call, default));
        var byNul = await RefusedAsync(() => api.GetRepository("acme", "ord\0ers", call, default));
        var byPrefix = await RefusedAsync(() => api.GetRateLimit(Call("https://api.example/pre%0D%0Afix", "secret-value"), default));
        var byRedirect = await RefusedAsync(() => api.Follow(call with { AbsoluteUri = new Uri("https://api.example/a%00b") }, default));
        var byQuery = await RefusedAsync(() => api.ListCommits("acme", "orders", "main\nx", 1, call, default));

        Assert.All(new[] { byOwner, byNul, byPrefix, byRedirect, byQuery }, refused =>
        {
            Assert.Equal(OutboundFailure.InvalidRequest, refused.Failure);
            Assert.Contains("control character", refused.Reason);
        });
        Assert.Empty(stub.Requests);
    }

    // ---- https on a hosted instance ----

    [Theory]
    [InlineData("http://public.example", false, AddressA)]
    [InlineData("http://public.example", true, AddressA)]
    // One public address among private ones is enough: the connection could use it.
    [InlineData("http://mixed.example", true, PrivateAddress + "," + AddressA)]
    [InlineData("http://93.184.216.34:8080", true, AddressA)]
    public async Task With_https_required_a_request_is_never_sent_over_http_to_a_public_address(string baseUrl, bool allowPrivateNetwork, string resolvesTo)
    {
        var stub = new StubTransport().Json("{}");
        await using var provider = BuildOver(stub, Fixed(resolvesTo.Split(',')));
        var api = provider.GetRequiredService<IGitHubApi>();

        var withCredential = await RefusedAsync(() => api.GetRateLimit(Call(baseUrl, "hosted-secret", allowPrivateNetwork, requireHttps: true), default));
        var anonymous = await RefusedAsync(() => api.GetRateLimit(Call(baseUrl, null, allowPrivateNetwork, requireHttps: true), default));

        Assert.Equal((OutboundFailure.UrlRefused, OutboundFailure.UrlRefused), (withCredential.Failure, anonymous.Failure));
        Assert.Contains("Must use https", withCredential.Reason);
        Assert.Empty(stub.Requests);

        // The same call is sent where https is not required, which is the rule of a local instance.
        using var local = await api.GetRateLimit(Call(baseUrl, "hosted-secret", allowPrivateNetwork: true), default);
        Assert.Equal(HttpStatusCode.OK, local.StatusCode);
    }

    [Fact]
    public async Task With_https_required_http_reaches_a_private_address_only_when_the_policy_allows_private_networks()
    {
        var stub = new StubTransport().Json("{}");
        await using var provider = BuildOver(stub, Fixed(PrivateAddress));
        var api = provider.GetRequiredService<IGitHubApi>();

        using var allowed = await api.GetRateLimit(Call("http://ollama.internal:11434", "local-key", allowPrivateNetwork: true, requireHttps: true), default);
        var refused = await RefusedAsync(() => api.GetRateLimit(Call("http://ollama.internal:11434", "local-key", allowPrivateNetwork: false, requireHttps: true), default));
        var overHttps = await RefusedAsync(() => api.GetRateLimit(Call("https://ollama.internal", "local-key", allowPrivateNetwork: false, requireHttps: true), default));

        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        Assert.Equal(OutboundFailure.UrlRefused, refused.Failure);
        // https to a private address is still refused without the policy: the two rules are separate.
        Assert.Equal(OutboundFailure.UrlRefused, overHttps.Failure);
        Assert.Single(stub.Requests);
    }

    [Fact]
    public async Task With_https_required_a_retry_and_a_followed_redirect_are_checked_like_the_first_attempt()
    {
        var lookups = 0;
        var stub = new StubTransport().Json(HttpStatusCode.ServiceUnavailable, "{}").Json("{}");
        // The name is private for the first attempt and public for the retry.
        await using var provider = BuildOver(stub,
            (_, _) => Task.FromResult(new[] { IPAddress.Parse(Interlocked.Increment(ref lookups) == 1 ? PrivateAddress : AddressA) }));
        var api = provider.GetRequiredService<IGitHubApi>();
        var call = Call("http://moving.example", "hosted-secret", allowPrivateNetwork: true, requireHttps: true);

        var onRetry = await RefusedAsync(() => api.GetRateLimit(call, default));

        Assert.Equal(OutboundFailure.UrlRefused, onRetry.Failure);
        Assert.Contains("Must use https", onRetry.Reason);
        // The first attempt went out, to the private address. The retry did not.
        Assert.Single(stub.Requests);

        // A redirect target is a new address for the same call and passes the same check.
        var https = Call("https://api.example", "hosted-secret", requireHttps: true);
        var onRedirect = await RefusedAsync(() => api.Follow(https with { AbsoluteUri = new Uri("http://api.example/moved") }, default));
        Assert.Equal(OutboundFailure.UrlRefused, onRedirect.Failure);
        Assert.Single(stub.Requests);
    }

    // ---- Connection reuse ----

    /// <summary>A keep-alive HTTP/1.1 server on this machine that records each request and the connection it came on.</summary>
    private sealed class Listener : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private int _connections;

        public Listener(string name)
        {
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = Task.Run(async () =>
            {
                try
                {
                    while (true)
                    {
                        var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                        var connection = Interlocked.Increment(ref _connections);
                        _ = Task.Run(() => ServeAsync(client, name, connection));
                    }
                }
                catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
                {
                    // The test ended.
                }
            });
        }

        public int Port { get; }
        public int Connections => _connections;
        public ConcurrentQueue<(int Connection, string Text)> Requests { get; } = new();

        private async Task ServeAsync(TcpClient client, string name, int connection)
        {
            using var owned = client;
            await using var stream = client.GetStream();
            var buffer = new byte[8192];
            var pending = new StringBuilder();
            try
            {
                while (true)
                {
                    while (!pending.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
                    {
                        var read = await stream.ReadAsync(buffer, _stop.Token);
                        if (read == 0)
                            return;
                        pending.Append(Encoding.ASCII.GetString(buffer, 0, read));
                    }

                    Requests.Enqueue((connection, pending.ToString()));
                    pending.Clear();
                    var body = "{\"server\":\"" + name + "\"}";
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(
                        $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\n\r\n{body}"), _stop.Token);
                }
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException)
            {
                // The client closed the connection or the test ended.
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
        }
    }

    [Fact]
    public async Task A_pooled_connection_is_used_only_for_the_address_the_request_was_checked_for()
    {
        using var first = new Listener("first");
        using var second = new Listener("second");
        var answer = AddressA;
        var dialed = new ConcurrentQueue<IPEndPoint>();
        // The real transport with its process-wide pools. Only the socket is redirected to this machine.
        await using var provider = Build(
            (_, _) => Task.FromResult(new[] { IPAddress.Parse(answer) }),
            services => services.AddSingleton<SocketDialer>((endpoint, ct) =>
            {
                dialed.Enqueue(endpoint);
                return PinnedConnector.DialSocketAsync(
                    new IPEndPoint(IPAddress.Loopback, endpoint.Address.ToString() == AddressA ? first.Port : second.Port), ct);
            }));
        var api = provider.GetRequiredService<IGitHubApi>();

        async Task<string> SendAsync(string? secret)
        {
            using var response = await api.GetRateLimit(Call("http://pool.example:8080", secret), default);
            return await response.Content.ReadAsStringAsync();
        }

        // Workspace A's request is checked for the first address and opens a connection to it.
        Assert.Contains("first", await SendAsync("credential-one"));
        // The same host name now resolves to another address. The request is checked for that
        // address and must not travel on the connection that is still open to the first one.
        answer = AddressB;
        Assert.Contains("second", await SendAsync("credential-two"));
        // Back on the first address: the open connection is used again, and it carries only
        // what this request carries.
        answer = AddressA;
        Assert.Contains("first", await SendAsync(null));

        Assert.Equal([new IPEndPoint(IPAddress.Parse(AddressA), 8080), new IPEndPoint(IPAddress.Parse(AddressB), 8080)], dialed.ToArray());
        Assert.Equal((1, 1), (first.Connections, second.Connections));
        var onFirst = first.Requests.ToArray();
        Assert.Equal(2, onFirst.Length);
        Assert.Equal((1, 1), (onFirst[0].Connection, onFirst[1].Connection));
        Assert.Contains("Authorization: Bearer credential-one", onFirst[0].Text);
        // A credential belongs to a request. The connection that carried one carries none for the next request.
        Assert.DoesNotContain("Authorization", onFirst[1].Text);
        Assert.DoesNotContain("credential-two", string.Concat(onFirst.Select(r => r.Text)));
        var onSecond = Assert.Single(second.Requests);
        Assert.Contains("Authorization: Bearer credential-two", onSecond.Text);
        // The server sees the name, not the address the connection was fixed to.
        Assert.All(onFirst.Append(onSecond), request => Assert.Contains("Host: pool.example:8080", request.Text));
    }

    [Fact]
    public async Task A_connection_that_fails_moves_the_retry_to_the_next_checked_address()
    {
        using var listener = new Listener("second");
        var dialed = new ConcurrentQueue<IPAddress>();
        await using var provider = Build(Fixed(AddressA, AddressB), services => services.AddSingleton<SocketDialer>((endpoint, ct) =>
        {
            dialed.Enqueue(endpoint.Address);
            return endpoint.Address.ToString() == AddressA
                ? throw new SocketException((int)SocketError.ConnectionRefused)
                : PinnedConnector.DialSocketAsync(new IPEndPoint(IPAddress.Loopback, listener.Port), ct);
        }));

        using var response = await provider.GetRequiredService<IGitHubApi>().GetRateLimit(Call("http://two.example:8080", null), default);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal([IPAddress.Parse(AddressA), IPAddress.Parse(AddressB)], dialed.ToArray());
    }

    [Fact]
    public async Task Tls_names_the_host_of_the_url_although_the_connection_is_fixed_to_an_address()
    {
        // The certificate is self-signed, so the client ends the handshake. The server has
        // read the name by then: that is the name the client asks for and checks the certificate against.
        using var key = RSA.Create(2048);
        using var selfSigned = new CertificateRequest("CN=pinned.example", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        using var certificate = X509CertificateLoader.LoadPkcs12(selfSigned.Export(X509ContentType.Pfx), null);
        using var tcp = new TcpListener(IPAddress.Loopback, 0);
        tcp.Start();
        var port = ((IPEndPoint)tcp.LocalEndpoint).Port;
        var names = new ConcurrentQueue<string?>();
        using var stop = new CancellationTokenSource();
        var serving = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    using var client = await tcp.AcceptTcpClientAsync(stop.Token);
                    await using var tls = new SslStream(client.GetStream());
                    try
                    {
                        await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                        {
                            ServerCertificateSelectionCallback = (_, name) =>
                            {
                                names.Enqueue(name);
                                return certificate;
                            },
                        }, stop.Token);
                    }
                    catch (Exception ex) when (ex is IOException or System.Security.Authentication.AuthenticationException)
                    {
                        // The client refused the certificate.
                    }
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                // The test ended.
            }
        });

        var dialed = new ConcurrentQueue<IPEndPoint>();
        await using var provider = Build(Fixed(AddressA), services => services.AddSingleton<SocketDialer>((endpoint, ct) =>
        {
            dialed.Enqueue(endpoint);
            return PinnedConnector.DialSocketAsync(new IPEndPoint(IPAddress.Loopback, port), ct);
        }));

        var refused = await RefusedAsync(() => provider.GetRequiredService<IGitHubApi>().GetRateLimit(Call("https://pinned.example", "tls-secret"), default));
        await stop.CancelAsync();
        tcp.Stop();
        await serving;

        // The untrusted certificate ended the connection before any request was written.
        Assert.Equal(OutboundFailure.ConnectionFailed, refused.Failure);
        Assert.NotEmpty(names);
        Assert.All(names, name => Assert.Equal("pinned.example", name));
        Assert.All(dialed, endpoint => Assert.Equal(new IPEndPoint(IPAddress.Parse(AddressA), 443), endpoint));
    }

    // ---- Time limits ----

    private static async Task<(ServiceProvider Provider, Stream Body, HttpResponseMessage Response)> OpenAsync(
        ManualTimeProvider clock, Stream body, bool streaming, TimeSpan? maxStreamDuration = null)
    {
        var stub = new StubTransport().Body(() => body, "text/event-stream");
        var provider = BuildOver(stub, Fixed(AddressA), services => services.AddSingleton<TimeProvider>(clock));
        var call = Call("https://api.example", null) with { Streaming = streaming };
        if (maxStreamDuration is { } cap)
            call = call with { MaxStreamDuration = cap };

        var response = await provider.GetRequiredService<IGitHubApi>().GetRateLimit(call, default);
        return (provider, await response.Content.ReadAsStreamAsync(), response);
    }

    [Fact]
    public async Task A_reader_that_is_slow_between_reads_does_not_time_out_a_stream()
    {
        var clock = new ManualTimeProvider();
        var source = new GatedStream();
        source.Send(16);
        source.Send(16);
        var (provider, body, response) = await OpenAsync(clock, source, streaming: true);
        await using var _ = provider;
        using var __ = response;
        var buffer = new byte[64];

        Assert.Equal(16, await body.ReadAsync(buffer));
        // The reader does something else for ten times the timeout. The second chunk is already there.
        clock.Advance(TimeSpan.FromMinutes(10));
        Assert.Equal(16, await body.ReadAsync(buffer));
    }

    [Fact]
    public async Task A_stream_times_out_when_the_server_sends_nothing_for_the_timeout()
    {
        var clock = new ManualTimeProvider();
        var source = new GatedStream();
        source.Send(16);
        var (provider, body, response) = await OpenAsync(clock, source, streaming: true);
        await using var _ = provider;
        using var __ = response;
        var buffer = new byte[64];
        Assert.Equal(16, await body.ReadAsync(buffer));
        // Time the reader spent before it asked for more does not count against the server.
        clock.Advance(TimeSpan.FromSeconds(55));

        var waiting = body.ReadAsync(buffer).AsTask();
        await source.ReadIsWaitingAsync();
        clock.Advance(TimeSpan.FromSeconds(59));
        Assert.False(waiting.IsCompleted);
        clock.Advance(TimeSpan.FromSeconds(2));

        var timedOut = await Assert.ThrowsAsync<OutboundException>(() => waiting);
        Assert.Equal(OutboundFailure.Timeout, timedOut.Failure);
    }

    [Fact]
    public async Task A_stream_that_only_sends_keep_alives_ends_at_its_total_duration()
    {
        var clock = new ManualTimeProvider();
        var source = new GatedStream();
        var (provider, body, response) = await OpenAsync(clock, source, streaming: true, maxStreamDuration: TimeSpan.FromMinutes(30));
        await using var _ = provider;
        using var __ = response;
        var buffer = new byte[64];
        var started = clock.GetUtcNow();

        OutboundException? ended = null;
        var reads = 0;
        while (ended is null && reads < 100)
        {
            // A keep-alive every 50 seconds: the wait for data never reaches the timeout of 60.
            source.Send(2);
            clock.Advance(TimeSpan.FromSeconds(50));
            try
            {
                await body.ReadAsync(buffer);
                reads++;
            }
            catch (OutboundException ex)
            {
                ended = ex;
            }
        }

        Assert.Equal(OutboundFailure.Timeout, ended?.Failure);
        Assert.Equal(TimeSpan.FromMinutes(30), clock.GetUtcNow() - started);
        Assert.Equal(35, reads);
    }

    [Fact]
    public async Task A_whole_response_has_one_limit_for_headers_and_body()
    {
        var clock = new ManualTimeProvider();
        var source = new GatedStream();
        source.Send(16);
        source.Send(16);
        var (provider, body, response) = await OpenAsync(clock, source, streaming: false);
        await using var _ = provider;
        using var __ = response;
        var buffer = new byte[64];

        Assert.Equal(16, await body.ReadAsync(buffer));
        clock.Advance(TimeSpan.FromSeconds(61));

        var timedOut = await Assert.ThrowsAsync<OutboundException>(async () => await body.ReadAsync(buffer));
        Assert.Equal(OutboundFailure.Timeout, timedOut.Failure);
    }

    // ---- Retry timing ----

    [Fact]
    public async Task The_backoff_has_jitter_and_a_wait_that_the_server_asked_for_has_none()
    {
        var waits = new List<TimeSpan>();
        var draws = new Queue<double>([0.5, 1.0]);
        var stub = new StubTransport()
            .Json(HttpStatusCode.ServiceUnavailable, "{}").Json(HttpStatusCode.ServiceUnavailable, "{}").Json("{}")
            .Json(HttpStatusCode.TooManyRequests, "{}", ("Retry-After", "2")).Json("{}");
        await using var provider = BuildOver(stub, Fixed(AddressA), services => services.AddSingleton(OutboundLimits.Default with
        {
            Delay = (wait, _) =>
            {
                waits.Add(wait);
                return Task.CompletedTask;
            },
            Jitter = draws.Dequeue,
        }));
        var api = provider.GetRequiredService<IGitHubApi>();

        using var afterBackoff = await api.GetRateLimit(Call("https://api.example", null), default);
        using var afterRetryAfter = await api.GetRateLimit(Call("https://api.example", null), default);

        // 500 ms shortened by a quarter, then 1000 ms shortened by half: the draw decides the share.
        Assert.Equal([TimeSpan.FromMilliseconds(375), TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(2)], waits);
        Assert.Equal((HttpStatusCode.OK, HttpStatusCode.OK), (afterBackoff.StatusCode, afterRetryAfter.StatusCode));
    }
}
