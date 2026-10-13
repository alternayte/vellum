using System.Diagnostics;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Vellum.Modules.Settings;
using Vellum.Shared.Telemetry;

namespace Vellum.Tests.Shared;

/// <summary>
/// The telemetry of the app with in-memory exporters behind the same processors that stand
/// before the OTLP exporter. The host is a real server on a loopback port, so the server and
/// the HTTP client instrumentation both run.
/// </summary>
public class TelemetryRedactionTests
{
    private const string AgentToken = "vat_9fQ2mZp7Lx3Rk8Tn5Vb1Yc6Wd4Hs0JgAeUoIiPq";
    private const string GitHubToken = "ghp_A1b2C3d4E5f6G7h8I9j0K1l2M3n4O5p6Q7r8";
    private const string ApiKey = "sk-proj-Zx9Yw8Vu7Ts6Rq5Po4Nm3Lk2Ji1Hg0";
    private const string OAuthCode = "plain-oauth-code-7731";
    private const string SessionCookie = "CfDJ8-session-cookie-value-4410";
    private const string DatabasePassword = "hunter2-database-password";
    private const string UpstreamSignature = "upstream-signature-9912";

    private static readonly string[] Secrets =
        [AgentToken, GitHubToken, ApiKey, OAuthCode, SessionCookie, DatabasePassword, UpstreamSignature];

    [Fact]
    public void Telemetry_is_off_and_registers_nothing_unless_the_endpoint_variable_is_set()
    {
        var services = new ServiceCollection();
        services.AddTelemetry(new ConfigurationBuilder().Build(), "test");

        Assert.Equal(new TelemetryState(false, null), services.BuildServiceProvider().GetRequiredService<TelemetryState>());
        Assert.DoesNotContain(services, d => d.ServiceType.Namespace?.StartsWith("OpenTelemetry", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void The_state_holds_the_host_of_the_endpoint_and_nothing_else_of_it()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [TelemetrySetup.EndpointVariable] = "https://user:collector-password@otel.example.com:4318/v1/tenant-key",
        }).Build();

        Assert.Equal(new TelemetryState(true, "otel.example.com"), TelemetrySetup.Read(configuration));

        var broken = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [TelemetrySetup.EndpointVariable] = "otel.example.com:4317",
        }).Build();
        var refusal = Assert.Throws<BootstrapException>(() => TelemetrySetup.Read(broken));
        Assert.Contains(TelemetrySetup.EndpointVariable, refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_request_with_a_credential_header_and_a_secret_in_its_address_leaves_no_secret_in_exported_telemetry()
    {
        var spans = new List<Activity>();
        var logs = new List<LogRecord>();
        var metrics = new List<Metric>();

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        // Nothing listens there. The OTLP exporter is registered as in the app; the in-memory exporters stand beside it.
        builder.Configuration[TelemetrySetup.EndpointVariable] = "http://127.0.0.1:9";
        // The request log of the framework is on, as an operator can turn it on.
        builder.Configuration["Logging:LogLevel:Default"] = "Information";
        builder.Configuration["Logging:LogLevel:Microsoft.AspNetCore"] = "Information";
        builder.Services.AddTelemetry(builder.Configuration, "test");
        builder.Services.ConfigureOpenTelemetryTracerProvider(tracing => tracing.AddInMemoryExporter(spans));
        builder.Services.ConfigureOpenTelemetryLoggerProvider(logging => logging.AddInMemoryExporter(logs));
        builder.Services.ConfigureOpenTelemetryMeterProvider(metering => metering.AddInMemoryExporter(metrics));

        await using var app = builder.Build();
        using var outbound = new HttpClient();
        app.MapGet("/inner/{id}", () => Results.Ok());
        app.MapGet("/outer/{id}", async (string id, HttpContext http, ILogger<TelemetryRedactionTests> logger) =>
        {
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{address}/inner/{id}?sig={UpstreamSignature}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", GitHubToken);
            using var response = await outbound.SendAsync(request);

            logger.LogWarning("Called {Path} with {Authorization} and key {Key}",
                http.Request.Path + http.Request.QueryString, http.Request.Headers.Authorization.ToString(), ApiKey);
            logger.LogError(new InvalidOperationException($"Could not connect: Host=db;Username=vellum;Password={DatabasePassword}"),
                "The database refused {Target}", new Uri($"postgres://vellum:{DatabasePassword}@db:5432/vellum"));
            VellumMetrics.LoginFailed("invalid_credentials");
            return Results.Ok();
        });
        await app.StartAsync();

        var root = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        using (var client = new HttpClient())
        using (var call = new HttpRequestMessage(HttpMethod.Get, $"{root}/outer/{GitHubToken}?api_key={ApiKey}&code={OAuthCode}"))
        {
            call.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AgentToken);
            call.Headers.Add("Cookie", $".AspNetCore.Identity.Application={SessionCookie}");
            call.Headers.Add("X-Api-Key", ApiKey);
            using var answer = await client.SendAsync(call);
            Assert.True(answer.IsSuccessStatusCode, await answer.Content.ReadAsStringAsync());
        }

        app.Services.GetRequiredService<TracerProvider>().ForceFlush();
        app.Services.GetRequiredService<LoggerProvider>().ForceFlush();
        app.Services.GetRequiredService<MeterProvider>().ForceFlush();

        // Every piece of text that an exporter writes.
        var exported = new List<string>();
        foreach (var span in spans)
        {
            exported.Add(span.DisplayName);
            exported.Add(span.StatusDescription ?? "");
            exported.AddRange(span.TagObjects.Select(tag => $"{tag.Key}={Text(tag.Value)}"));
            exported.AddRange(span.Events.SelectMany(e => e.Tags.Select(tag => $"{e.Name}:{tag.Key}={Text(tag.Value)}")));
        }

        foreach (var log in logs)
        {
            exported.Add(log.FormattedMessage ?? "");
            exported.Add(log.Body ?? "");
            exported.Add(log.Exception?.ToString() ?? "");
            exported.AddRange((log.Attributes ?? []).Select(a => $"{a.Key}={Text(a.Value)}"));
        }

        foreach (var metric in metrics)
        {
            exported.Add(metric.Name);
            foreach (ref readonly var point in metric.GetMetricPoints())
            {
                foreach (var tag in point.Tags)
                    exported.Add($"{tag.Key}={Text(tag.Value)}");
            }
        }

        // The capture is real: both spans, both log lines and the counter came through.
        var server = Assert.Single(spans, s => s.Kind == ActivityKind.Server && s.DisplayName.Contains("/outer/", StringComparison.Ordinal));
        Assert.Equal("GET /outer/{id}", server.DisplayName);
        Assert.Null(server.GetTagItem("url.query"));
        var client2 = Assert.Single(spans, s => s.Kind == ActivityKind.Client && s.ParentSpanId == server.SpanId);
        Assert.Equal(root, client2.GetTagItem("url.full"));
        Assert.Contains(logs, l => l.FormattedMessage?.StartsWith("Called ", StringComparison.Ordinal) == true);
        var failure = Assert.Single(logs, l => l.FormattedMessage?.StartsWith("The database refused", StringComparison.Ordinal) == true);
        Assert.Null(failure.Exception);
        Assert.Contains(failure.Attributes!, a => a.Key == "exception.type" && (string?)a.Value == typeof(InvalidOperationException).FullName);
        Assert.Contains(logs, l => l.CategoryName == "Microsoft.AspNetCore.Hosting.Diagnostics");
        Assert.Contains(metrics, m => m.Name == "vellum.auth.login_failures");
        Assert.Contains(metrics, m => m.Name == "http.server.request.duration");

        foreach (var secret in Secrets)
            Assert.DoesNotContain(exported, text => text.Contains(secret, StringComparison.Ordinal));

        await app.StopAsync();
    }

    private static string Text(object? value) => value switch
    {
        null => "",
        string text => text,
        System.Collections.IEnumerable items => string.Join(",", items.Cast<object?>()),
        _ => value.ToString() ?? "",
    };
}
