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
    Guid? From, Guid? To, Guid? Target, JsonElement? Payload, string? Detail);

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
