using System.Text.Json;

namespace Vellum.Modules.Trace.Engine;

public enum TraceEventKind
{
    RequestSent,
    ResponseReceived,
    RequestFailed,
    RequestTimedOut,
    RetryScheduled,
    Published,
    PublishFailed,
    Delivered,
    DeliveryFailed,
    DeadLettered,
    Dropped,
    DataWritten,
    DataWriteFailed,
    BranchTaken,
    StepSkipped,
}

/// <summary>
/// One observable moment of a playback. <paramref name="Target"/> is the operation, channel
/// or subscription involved; <paramref name="From"/> and <paramref name="To"/> are elements.
/// </summary>
public sealed record TraceEvent(
    int Sequence, long AtMs, TraceEventKind Kind, string StepId, int Attempt,
    Guid? From, Guid? To, Guid? Target, JsonElement? Payload, string? Detail,
    Guid? Message = null, IReadOnlyDictionary<string, string>? Headers = null);

/// <summary>
/// Bounds on what one playback may cost. Scenarios arrive from imports and AI proposals, so
/// they are untrusted input: nested retries multiply, and the limits keep that finite.
/// </summary>
public static class TraceLimits
{
    public const int MaxDepth = 24;
    public const int MaxSteps = 500;
    public const int MaxAttempts = 10;
    public const int MaxDurationMs = 3_600_000;
    public const int MaxEvents = 10_000;
}

public sealed record ScenarioIssue(string StepId, string Code, string Message);

public sealed record AssertionResult(string Id, bool Passed, string Detail);

/// <summary>
/// A playback is declared behaviour replayed over declared contracts. It is not evidence
/// that a running system behaves this way (SDD guarantee 8).
/// </summary>
public sealed record TraceResult(
    string EngineVersion,
    IReadOnlyList<ScenarioIssue> Issues,
    IReadOnlyList<TraceEvent> Events,
    IReadOnlyDictionary<string, StepOutcome> Outcomes,
    IReadOnlyList<AssertionResult> Assertions,
    string Hash)
{
    public bool IsValid => Issues.Count == 0;
}
