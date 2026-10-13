using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Vellum.Shared.Outbound;

namespace Vellum.Tests.Shared.Outbound;

/// <summary>A request as the transport saw it, with the headers of that moment.</summary>
internal sealed record SentRequest(
    HttpMethod Method, Uri Uri, IReadOnlyDictionary<string, string> Headers, string Body, CancellationToken Cancellation)
{
    public string? Header(string name) => Headers.GetValueOrDefault(name);
}

/// <summary>
/// Stands in for the network under the real handler pipeline. Each request takes the next
/// step; the last step repeats. No test opens a socket.
/// </summary>
internal sealed class StubTransport : HttpMessageHandler
{
    private readonly List<Func<SentRequest, Task<HttpResponseMessage>>> _steps = [];
    private readonly ConcurrentQueue<SentRequest> _requests = new();
    private int _next;

    public IReadOnlyList<SentRequest> Requests => [.. _requests];

    public StubTransport Then(Func<SentRequest, Task<HttpResponseMessage>> step)
    {
        _steps.Add(step);
        return this;
    }

    public StubTransport Then(Func<HttpResponseMessage> response) => Then(_ => Task.FromResult(response()));

    public StubTransport Json(HttpStatusCode status, string body, params (string Name, string Value)[] headers) => Then(() =>
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        foreach (var (name, value) in headers)
            response.Headers.TryAddWithoutValidation(name, value);
        return response;
    });

    public StubTransport Json(string body) => Json(HttpStatusCode.OK, body);

    public StubTransport Sse(string events) => Then(() => new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StringContent(events, Encoding.UTF8, "text/event-stream"),
    });

    public StubTransport Body(Func<Stream> body, string mediaType) => Then(() =>
    {
        var content = new StreamContent(body());
        content.Headers.ContentType = new(mediaType);
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    });

    /// <summary>Never answers. Ends when the pipeline or the caller cancels.</summary>
    public StubTransport Hang() => Then(async sent =>
    {
        await Task.Delay(Timeout.Infinite, sent.Cancellation);
        throw new InvalidOperationException("unreachable");
    });

    public StubTransport Throw(Func<Exception> fault) => Then(_ => throw fault());

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var headers = request.Headers.Concat(request.Content?.Headers ?? Enumerable.Empty<KeyValuePair<string, IEnumerable<string>>>())
            .ToDictionary(h => h.Key, h => string.Join(", ", h.Value), StringComparer.OrdinalIgnoreCase);
        var sent = new SentRequest(
            request.Method, request.RequestUri!, headers, request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct), ct);
        _requests.Enqueue(sent);

        if (_steps.Count == 0)
            throw new InvalidOperationException($"No response is set up for {request.Method} {request.RequestUri}.");

        var index = Math.Min(Interlocked.Increment(ref _next) - 1, _steps.Count - 1);
        var response = await _steps[index](sent);
        response.RequestMessage = request;
        return response;
    }
}

/// <summary>One transport per host name, so tests that share an app do not see each other's requests.</summary>
internal sealed class RoutedTransport : HttpMessageHandler
{
    private readonly ConcurrentDictionary<string, StubTransport> _hosts = new(StringComparer.OrdinalIgnoreCase);

    public StubTransport For(string host) => _hosts.GetOrAdd(host, _ => new StubTransport());

    public void Forget(string host) => _hosts.TryRemove(host, out _);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
        _hosts.TryGetValue(request.RequestUri!.Host, out var stub)
            ? new HttpMessageInvoker(stub, disposeHandler: false).SendAsync(request, ct)
            : throw new HttpRequestException(HttpRequestError.ConnectionError, "The test set up no host with this name.");

    // The client factory disposes its handlers. The stub outlives them.
    protected override void Dispose(bool disposing) { }
}

/// <summary>Every log line of the app, with the text of any logged exception.</summary>
internal sealed class LogCapture : ILoggerProvider
{
    private readonly ConcurrentQueue<string> _lines = new();

    public IReadOnlyList<string> Lines => [.. _lines];

    public void Clear() => _lines.Clear();

    public ILogger CreateLogger(string categoryName) => new Sink(categoryName, _lines);

    public void Dispose() { }

    private sealed class Sink(string category, ConcurrentQueue<string> lines) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            lines.Enqueue($"{logLevel} {category}: {formatter(state, exception)} {exception}");
    }
}

internal static class Fixtures
{
    public static string Read(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name.Replace('/', Path.DirectorySeparatorChar)));

    /// <summary>A fixture that holds <c>__TEXT__</c> where the model's answer goes.</summary>
    public static string WithText(string name, string text) =>
        Read(name).Replace("__TEXT__", JsonSerializer.Serialize(text)[1..^1], StringComparison.Ordinal);
}

/// <summary>The class fixture: one <see cref="OutboundHost"/> in the local profile for all tests of a class.</summary>
public sealed class SharedOutboundHost(IntegrationFixture fixture) : IDisposable
{
    public IntegrationFixture Fixture { get; } = fixture;
    public OutboundHost Host { get; } = new(fixture);

    public void Dispose() => Host.Dispose();
}

/// <summary>
/// One app for a test class, with the real outbound pipeline over <see cref="RoutedTransport"/>.
/// The retry waits are recorded and not waited.
/// </summary>
public sealed class OutboundHost : IDisposable
{
    internal RoutedTransport Transport { get; } = new();
    internal LogCapture Logs { get; } = new();
    internal ConcurrentQueue<TimeSpan> Waits { get; } = new();

    public WebApplicationFactory<Program> Factory { get; }

    public OutboundHost(IntegrationFixture fixture, IReadOnlyDictionary<string, string?>? settings = null, Action<IServiceCollection>? configure = null)
    {
        Factory = TestHost.Create(fixture.ConnectionString, settings, services =>
        {
            services.AddSingleton<OutboundPrimaryHandlerFactory>(_ => () => Transport);
            services.AddSingleton(OutboundLimits.Default with
            {
                Delay = (wait, _) =>
                {
                    Waits.Enqueue(wait);
                    return Task.CompletedTask;
                },
            });
            services.AddLogging(logging => logging.AddProvider(Logs).SetMinimumLevel(LogLevel.Debug));
            configure?.Invoke(services);
        });
    }

    public void Dispose() => Factory.Dispose();

    /// <summary>A new signed-in user who owns a new workspace.</summary>
    internal async Task<(HttpClient Owner, Guid WorkspaceId)> NewWorkspaceAsync()
    {
        var owner = await TestHost.SignInAsync(Factory);
        var (workspaceId, _) = await TestHost.CreateProjectAsync(owner);
        return (owner, workspaceId);
    }

    internal static async Task PutSettingsAsync(HttpClient owner, Guid workspaceId, JsonObject overrides)
    {
        var response = await owner.PutSettingsAsync($"/api/workspaces/{workspaceId}/settings",
            new StringContent(overrides.ToJsonString(), Encoding.UTF8, "application/json"));
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
    }

    internal static async Task PutSecretAsync(HttpClient owner, Guid workspaceId, string name, string value)
    {
        var response = await owner.PutAsJsonAsync($"/api/workspaces/{workspaceId}/secrets/{name}", new { value });
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    /// <summary>The instance admin that the fixture's first-run setup created.</summary>
    internal async Task<HttpClient> SignInAsAdminAsync(IntegrationFixture fixture)
    {
        var client = TestHost.Client(Factory);
        var login = await client.PostAsJsonAsync("/api/auth/login", new { email = fixture.AdminEmail, password = TestHost.AdminPassword });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        return client;
    }
}
