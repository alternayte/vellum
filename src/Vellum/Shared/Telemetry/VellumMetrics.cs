using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Vellum.Shared.Telemetry;

/// <summary>
/// The counters an operator watches. Every tag is a short fixed word: an outcome, a provider
/// kind, a direction. No tag holds a workspace, a user, a URL, a model name that a user typed
/// or any text from a request. The counters cost nothing while no exporter listens.
/// </summary>
public static class VellumMetrics
{
    public const string MeterName = "Vellum";

    private static readonly Meter Meter = new(MeterName);

    private static readonly Counter<long> ImportScans = Meter.CreateCounter<long>(
        "vellum.import.scans", description: "Import scan runs that ended, by outcome.");

    private static readonly Counter<long> AiCalls = Meter.CreateCounter<long>(
        "vellum.ai.calls", description: "Calls to AI providers that ended, by provider kind and outcome.");

    private static readonly Counter<long> AiTokens = Meter.CreateCounter<long>(
        "vellum.ai.tokens", "{token}", "Tokens that AI providers reported, by provider kind and direction.");

    private static readonly Counter<long> StressRuns = Meter.CreateCounter<long>(
        "vellum.stress.runs", description: "Stress runs that were asked for, by outcome.");

    private static readonly Counter<long> AppendConflicts = Meter.CreateCounter<long>(
        "vellum.eventstore.append_conflicts", description: "Event store appends that lost to another writer of the same stream.");

    private static readonly Counter<long> OutboundBlocked = Meter.CreateCounter<long>(
        "vellum.outbound.blocked", description: "Outbound requests that the URL policy (the SSRF guard) refused, by provider label.");

    private static readonly Counter<long> LoginFailures = Meter.CreateCounter<long>(
        "vellum.auth.login_failures", description: "Password sign-ins that were refused, by reason.");

    /// <param name="outcome"><c>succeeded</c>, <c>failed</c>, <c>cancelled</c> or <c>waiting</c> (the scan runs again later).</param>
    public static void ImportScanEnded(string outcome) => ImportScans.Add(1, new KeyValuePair<string, object?>("outcome", outcome));

    /// <param name="providerKind">One of the fixed provider kinds, such as <c>openai</c>.</param>
    public static void AiCallEnded(string providerKind, string outcome, int? inputTokens, int? outputTokens)
    {
        AiCalls.Add(1, new TagList { { "provider", providerKind }, { "outcome", outcome } });
        if (inputTokens is > 0)
            AiTokens.Add(inputTokens.Value, new TagList { { "provider", providerKind }, { "direction", "input" } });
        if (outputTokens is > 0)
            AiTokens.Add(outputTokens.Value, new TagList { { "provider", providerKind }, { "direction", "output" } });
    }

    /// <param name="outcome"><c>stored</c>, <c>refused</c>, <c>too_large</c>, <c>busy</c> or <c>rate_limited</c>.</param>
    public static void StressRunEnded(string outcome) => StressRuns.Add(1, new KeyValuePair<string, object?>("outcome", outcome));

    public static void AppendConflict() => AppendConflicts.Add(1);

    /// <param name="provider">The fixed provider label of the outbound call, such as <c>github</c>.</param>
    public static void OutboundRequestBlocked(string provider) => OutboundBlocked.Add(1, new KeyValuePair<string, object?>("provider", provider));

    /// <param name="reason"><c>invalid_credentials</c> or <c>locked_out</c>.</param>
    public static void LoginFailed(string reason) => LoginFailures.Add(1, new KeyValuePair<string, object?>("reason", reason));
}
