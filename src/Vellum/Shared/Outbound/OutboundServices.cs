using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Refit;

namespace Vellum.Shared.Outbound;

public static class OutboundServices
{
    private static readonly Uri Placeholder = new("http://outbound.invalid/");

    private static readonly RefitSettings Settings = new(new SystemTextJsonContentSerializer(new JsonSerializerOptions
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    }));

    public static IServiceCollection AddOutbound(this IServiceCollection services)
    {
        services.TryAddSingleton(OutboundLimits.Default);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<SocketDialer>(PinnedConnector.DialSocketAsync);
        services.TryAddSingleton<PinnedConnector>();
        services.TryAddSingleton<OutboundPrimaryHandlerFactory>(provider =>
            () => new OutboundPrimaryHandler(provider.GetRequiredService<PinnedConnector>()));
        services.TryAddTransient<OutboundTelemetryHandler>();
        services.TryAddTransient<OutboundRetryHandler>();
        services.TryAddTransient<OutboundAttemptHandler>();
        return services;
    }

    /// <summary>
    /// Registers a Refit interface on the one outbound pipeline. Every third-party client of
    /// the server is registered here and nowhere else.
    /// </summary>
    public static IHttpClientBuilder AddOutboundClient<TApi>(this IServiceCollection services) where TApi : class =>
        services.AddRefitClient<TApi>(Settings)
            .ConfigureHttpClient(client =>
            {
                client.BaseAddress = Placeholder;
                // The pipeline applies the timeout from settings.
                client.Timeout = Timeout.InfiniteTimeSpan;
            })
            // The default client loggers write the request address. The pipeline writes its own line.
            .RemoveAllLoggers()
            .AddHttpMessageHandler<OutboundTelemetryHandler>()
            .AddHttpMessageHandler<OutboundRetryHandler>()
            .AddHttpMessageHandler<OutboundAttemptHandler>()
            .ConfigurePrimaryHttpMessageHandler(provider => provider.GetRequiredService<OutboundPrimaryHandlerFactory>()());
}

public static class OutboundSend
{
    /// <summary>
    /// Runs one Refit call. Refit wraps an exception of the handler pipeline in its own
    /// type; callers of the pipeline handle <see cref="OutboundException"/>, so that one is
    /// taken out again.
    /// </summary>
    public static async Task<HttpResponseMessage> RunAsync(Func<Task<HttpResponseMessage>> send)
    {
        try
        {
            return await send();
        }
        catch (ApiRequestException ex) when (ex.InnerException is OutboundException inner)
        {
            ExceptionDispatchInfo.Capture(inner).Throw();
            throw;
        }
    }
}

/// <summary>Why a "Test connection" call failed: a stable code and a sentence that says what to change.</summary>
public sealed record ConnectionTestError(string Code, string Message);

/// <summary>Reads response bodies. Nothing here puts body text into an exception.</summary>
public static class OutboundBody
{
    private static readonly JsonSerializerOptions Options = new() { MaxDepth = 64 };

    /// <returns>The body as <typeparamref name="T"/>, or null when it is not that JSON.</returns>
    public static async Task<T?> ReadJsonAsync<T>(HttpResponseMessage response, CancellationToken ct) where T : class
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        try
        {
            return await JsonSerializer.DeserializeAsync<T>(stream, Options, ct);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <returns>The body as untyped JSON, or null when it is not JSON.</returns>
    public static async Task<JsonElement?> ReadElementAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        try
        {
            return await JsonSerializer.DeserializeAsync<JsonElement>(stream, Options, ct);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static JsonElement? ParseElement(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<JsonElement>(json, Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static async Task<string> ReadTextAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync(ct);
    }

    /// <summary>
    /// The server-sent events of a streamed response. Comment lines (keep-alives) and frames
    /// split across reads are handled by the parser. An event that the stream cut before its
    /// blank line is not returned.
    /// </summary>
    public static async IAsyncEnumerable<SseItem<string>> ReadEventsAsync(
        HttpResponseMessage response, [EnumeratorCancellation] CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        await foreach (var item in SseParser.Create(stream).EnumerateAsync(ct))
            yield return item;
    }
}
