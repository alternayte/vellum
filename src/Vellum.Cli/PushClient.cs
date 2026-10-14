using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Refit;
using Vellum.Modules.Import.Sources;

namespace Vellum.Cli;

/// <summary>The two calls of a push. The token goes in the Authorization header of each.</summary>
public interface IVellumPushApi
{
    [Get("/api/projects/{projectId}/sources/push")]
    Task<AgentPushSettings> SettingsAsync(Guid projectId, CancellationToken ct);

    [Post("/api/projects/{projectId}/sources/push")]
    Task<AgentPushResponse> PushAsync(Guid projectId, [Body] AgentPushRequest request, CancellationToken ct);
}

/// <summary>The answer a server gave instead of a result, as the CLI reports it.</summary>
internal sealed class PushRefused(int exitCode, string message) : Exception(message)
{
    public int ExitCode { get; } = exitCode;
}

/// <summary>The Refit client of a push, through <see cref="IHttpClientFactory"/> like every outbound call of Vellum.</summary>
internal sealed class PushClient : IDisposable
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly ServiceProvider _services;
    private readonly IVellumPushApi _api;

    /// <param name="primaryHandler">The handler that sends the requests. Null for the network.</param>
    public PushClient(Uri baseUrl, string token, TimeSpan timeout, Func<HttpMessageHandler>? primaryHandler = null)
    {
        var services = new ServiceCollection();
        var client = services
            .AddRefitClient<IVellumPushApi>(new RefitSettings(new SystemTextJsonContentSerializer(Json)))
            .ConfigureHttpClient(http =>
            {
                http.BaseAddress = baseUrl;
                http.Timeout = timeout;
                http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
                http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("vellum-cli", Program.Version));
            });
        if (primaryHandler is not null)
            client.ConfigurePrimaryHttpMessageHandler(primaryHandler);
        else
            client.ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
        _services = services.BuildServiceProvider();
        _api = _services.GetRequiredService<IVellumPushApi>();
    }

    public Task<AgentPushSettings> SettingsAsync(Guid projectId, CancellationToken ct) => CallAsync(() => _api.SettingsAsync(projectId, ct));

    public Task<AgentPushResponse> PushAsync(Guid projectId, AgentPushRequest request, CancellationToken ct) =>
        CallAsync(() => _api.PushAsync(projectId, request, ct));

    public void Dispose() => _services.Dispose();

    private static async Task<T> CallAsync<T>(Func<Task<T>> call)
    {
        try
        {
            return await call();
        }
        catch (ApiException ex)
        {
            var detail = Describe(ex.Content);
            throw ex.StatusCode switch
            {
                HttpStatusCode.Unauthorized => new PushRefused(ExitCodes.Auth, "The server did not accept the token (401). Check VELLUM_TOKEN: it may be revoked or expired."),
                HttpStatusCode.Forbidden => new PushRefused(ExitCodes.Auth, $"The token may not do this (403). {detail ?? "It needs the push scope, the project, and a user with the Editor role."}"),
                >= HttpStatusCode.InternalServerError => new PushRefused(ExitCodes.Server, $"The server failed ({(int)ex.StatusCode}). {detail}".TrimEnd()),
                HttpStatusCode.NotFound when detail is null => new PushRefused(ExitCodes.Refused, "The server has no push endpoint here (404). Check --url: it is the address of the Vellum app."),
                _ => new PushRefused(ExitCodes.Refused, $"The server refused the push ({(int)ex.StatusCode}). {detail}".TrimEnd()),
            };
        }
        // Refit wraps a transport failure in ApiRequestException; a time-out is a cancellation inside it.
        catch (Exception ex) when (ex is ApiRequestException or HttpRequestException or TaskCanceledException)
        {
            var cause = ex is ApiRequestException { InnerException: { } inner } ? inner : ex;
            throw cause is TaskCanceledException or TimeoutException
                ? new PushRefused(ExitCodes.Server, "The server did not answer in time. Raise --timeout, or try again.")
                : new PushRefused(ExitCodes.Server, $"The server could not be reached: {cause.Message}");
        }
    }

    /// <summary>The title and detail of the server's error body. The text is the server's, shown as text.</summary>
    private static string? Describe(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return null;
        try
        {
            using var document = JsonDocument.Parse(content);
            var root = document.RootElement;
            string? Text(string name) => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            var parts = new List<string?> { Text("title"), Text("detail") };
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array)
            {
                parts.AddRange(errors.EnumerateArray().Take(5).Select(e =>
                    e.TryGetProperty("field", out var f) && e.TryGetProperty("message", out var m) ? $"{f.GetString()}: {m.GetString()}" : null));
            }
            var text = string.Join(" ", parts.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p!.TrimEnd('.') + "."));
            return text.Length == 0 ? null : new string([.. text.Where(c => !char.IsControl(c)).Take(2000)]);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
