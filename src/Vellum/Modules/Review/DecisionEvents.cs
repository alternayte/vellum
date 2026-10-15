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

/// <summary>
/// Where a decision that an import proposed was read: an ADR file of a Source. The decision
/// is a statement people wrote down, so its truth label is <c>declared</c>; it is still only
/// proposed until a person accepts it.
/// </summary>
/// <param name="FactStatus">Always <c>declared</c>.</param>
/// <param name="Citation"><c>&lt;source&gt;@&lt;revision&gt;:path:start-end</c>: the ADR from its title to its last line.</param>
/// <param name="RecordedStatus">The status the file states, as written (<c>Accepted</c>, <c>superseded by …</c>). It is not the status of the decision in Vellum.</param>
/// <param name="RecordedDate">The date the file states, as written.</param>
/// <param name="Sections">Each section the decision was read from, with its citation.</param>
public sealed record DecisionSource(
    string FactStatus, string Citation, string? RecordedStatus, string? RecordedDate, IReadOnlyList<DecisionSection> Sections);

/// <param name="Name"><c>title</c>, <c>status</c>, <c>context</c>, <c>decision</c> or <c>consequences</c>.</param>
public sealed record DecisionSection(string Name, string Citation);

/// <param name="Action"><c>proposed</c>, <c>changed</c>, <c>accepted</c>, <c>rejected</c>, <c>settlement-recorded</c> or <c>settlement-removed</c>.</param>
public sealed record DecisionHistoryEntry(DateTimeOffset At, string By, string Action, string? Note);

/// <summary>The events of a decision stream. Plain records; the stream registers every nested type.</summary>
public static class DecisionEvent
{
    /// <param name="Source">
    /// For a decision that an import read from an ADR file: where, and what the file said of its
    /// own status and date. Added later as a nullable member, which keeps the event at shape
    /// version 1: an event stored without it reads as null, a decision written in Vellum.
    /// </param>
    public sealed record DecisionProposed(
        Guid ProjectId, int Number, string Origin, DecisionContent Content, string By, DateTimeOffset At, DecisionSource? Source = null);

    /// <summary>New content of a proposed decision. An accepted or rejected decision never gets this event.</summary>
    /// <param name="Source">For an import that read a changed ADR: the new citation. Null keeps the one the decision has.</param>
    public sealed record DecisionChanged(DecisionContent Content, string By, DateTimeOffset At, DecisionSource? Source = null);

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
