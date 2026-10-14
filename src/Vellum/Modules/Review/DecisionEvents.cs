using System.Collections.Immutable;
using Deedbox;
using Vellum.Modules.Promises;

namespace Vellum.Modules.Review;

/// <summary>
/// The stored states of a decision. <c>superseded</c> is not stored: a decision is superseded
/// while an accepted decision names it in <c>supersedes</c>, so it is derived on every read.
/// </summary>
public static class DecisionStatus
{
    public const string Proposed = "proposed";
    public const string Accepted = "accepted";
    public const string Rejected = "rejected";
    public const string Superseded = "superseded";
}

public sealed record Alternative(string Title, string? WhyNot);

/// <summary>A finding that the decision answers: one rule for one subject.</summary>
public sealed record Settles(string RuleId, Guid SubjectId);

/// <summary>
/// What a finding was when a person settled it with the decision: the version of the rule
/// and the fingerprint of the finding. A finding with another version or fingerprint is not
/// the finding the person read, so the decision does not settle it until it is reviewed again.
/// </summary>
/// <param name="Fingerprint">Null when the rule gave no finding for the subject at that time.</param>
public sealed record SettlementStamp(string RuleId, Guid SubjectId, int RuleVersion, string? Fingerprint);

/// <summary>The text and the links of a decision (an ADR).</summary>
/// <param name="AffectedIds">The things of the model the decision is about.</param>
/// <param name="Supersedes">The decision that this one replaces when it is accepted.</param>
public sealed record DecisionContent(
    string Title, string Context, string Decision,
    IReadOnlyList<Alternative> Alternatives, string? Consequences,
    IReadOnlyList<Guid> AffectedIds, IReadOnlyList<Settles> Settles, Guid? Supersedes);

/// <param name="Action"><c>proposed</c>, <c>changed</c>, <c>accepted</c>, <c>rejected</c>, <c>settlement-recorded</c> or <c>settlement-removed</c>.</param>
public sealed record DecisionHistoryEntry(DateTimeOffset At, string By, string Action, string? Note);

/// <summary>The events of a decision stream. Plain records; the stream registers every nested type.</summary>
public static class DecisionEvent
{
    public sealed record DecisionProposed(Guid ProjectId, int Number, string Origin, DecisionContent Content, string By, DateTimeOffset At);

    /// <summary>New content of a proposed decision. An accepted or rejected decision never gets this event.</summary>
    public sealed record DecisionChanged(DecisionContent Content, string By, DateTimeOffset At);

    /// <param name="Stamps">Each finding the decision settles, as it was when the person accepted.</param>
    /// <param name="ContentHash">
    /// <see cref="DecisionContentHash"/> of the decision the person read. Null only in an event
    /// that was stored before the acceptance named it.
    /// </param>
    public sealed record DecisionAccepted(string By, DateTimeOffset At, IReadOnlyList<SettlementStamp> Stamps, string? ContentHash = null);

    public sealed record DecisionRejected(string By, DateTimeOffset At, string? Reason);

    /// <summary>A person settled a finding with the accepted decision, or reviewed it again after it changed.</summary>
    public sealed record SettlementRecorded(SettlementStamp Stamp, string By, DateTimeOffset At);

    public sealed record SettlementRemoved(string RuleId, Guid SubjectId, string By, DateTimeOffset At);

    public sealed record DecisionDeleted(string By, DateTimeOffset At);
}
