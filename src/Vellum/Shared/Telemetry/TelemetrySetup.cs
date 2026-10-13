using Npgsql;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Vellum.Modules.Settings;
using Vellum.Shared.Outbound;

namespace Vellum.Shared.Telemetry;

/// <summary>
/// Whether this instance exports telemetry, and to which host. The path, the port and the
/// headers of the endpoint are not kept: a collector address can hold a credential.
/// </summary>
public sealed record TelemetryState(bool Enabled, string? EndpointHost);

/// <summary>
/// Traces, metrics and logs through OTLP. The export is off unless the standard bootstrap
/// variable <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> is set: an instance sends nothing to another
/// machine until its operator names the collector. The other standard variables
/// (<c>OTEL_EXPORTER_OTLP_PROTOCOL</c>, <c>OTEL_EXPORTER_OTLP_HEADERS</c>,
/// <c>OTEL_SERVICE_NAME</c>, <c>OTEL_TRACES_SAMPLER</c>) apply as the OpenTelemetry SDK
/// reads them.
/// </summary>
public static class TelemetrySetup
{
    public const string EndpointVariable = "OTEL_EXPORTER_OTLP_ENDPOINT";
    public const string ServiceName = "vellum";

    /// <exception cref="BootstrapException">The endpoint is not an absolute http or https URL.</exception>
    public static TelemetryState Read(IConfiguration configuration)
    {
        var endpoint = configuration[EndpointVariable]?.Trim();
        if (string.IsNullOrEmpty(endpoint))
            return new TelemetryState(false, null);

        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new BootstrapException(
                $"{EndpointVariable} is not valid. Set it to the http or https address of an OTLP collector, for example http://otel-collector:4317, or remove it to turn telemetry off.");

        return new TelemetryState(true, uri.Host);
    }

    public static IServiceCollection AddTelemetry(this IServiceCollection services, IConfiguration configuration, string version)
    {
        var state = Read(configuration);
        services.AddSingleton(state);
        if (!state.Enabled)
            return services;

        var telemetry = services.AddOpenTelemetry()
            .WithTracing(tracing => tracing
                // A probe every few seconds is no trace an operator reads.
                .AddAspNetCoreInstrumentation(options => options.Filter = http => !http.Request.Path.StartsWithSegments("/health"))
                .AddHttpClientInstrumentation()
                .AddNpgsql()
                .AddSource(OutboundTelemetryHandler.Name)
                .AddProcessor(new RedactingActivityProcessor()))
            .WithMetrics(metrics => metrics
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddMeter("Npgsql", VellumMetrics.MeterName, OutboundTelemetryHandler.Name))
            .WithLogging(
                logging => logging.AddProcessor(new RedactingLogProcessor()),
                options =>
                {
                    options.IncludeFormattedMessage = true;
                    // A scope holds what the framework knows of the request, the raw path among it.
                    options.IncludeScopes = false;
                })
            .UseOtlpExporter();

        // OTEL_SERVICE_NAME wins when the operator sets it.
        if (string.IsNullOrWhiteSpace(configuration["OTEL_SERVICE_NAME"]))
            telemetry.ConfigureResource(resource => resource.AddService(ServiceName, serviceVersion: version));

        return services;
    }
}
