using System.Collections.Immutable;
using Deedbox;
using Vellum.Modules.Promises;

namespace Vellum.Modules.Review.Decisions;

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
    public sealed record DecisionAccepted(string By, DateTimeOffset At, IReadOnlyList<SettlementStamp> Stamps);

    public sealed record DecisionRejected(string By, DateTimeOffset At, string? Reason);

    /// <summary>A person settled a finding with the accepted decision, or reviewed it again after it changed.</summary>
    public sealed record SettlementRecorded(SettlementStamp Stamp, string By, DateTimeOffset At);

    public sealed record SettlementRemoved(string RuleId, Guid SubjectId, string By, DateTimeOffset At);

    public sealed record DecisionDeleted(string By, DateTimeOffset At);
}

/// <summary>One decision: its number, its content, who accepted it and what happened to it.</summary>
public sealed record DecisionState(
    Guid? ProjectId, int Number, string? Origin, string Status, DecisionContent? Content,
    ImmutableList<SettlementStamp> Stamps,
    string? CreatedBy, DateTimeOffset? CreatedAt, DateTimeOffset? UpdatedAt,
    string? AcceptedBy, DateTimeOffset? AcceptedAt,
    ImmutableList<DecisionHistoryEntry> History, bool Deleted)
    : IState<DecisionState>
{
    public static DecisionState Initial => new(null, 0, null, DecisionStatus.Proposed, null, [], null, null, null, null, null, [], false);

    public static DecisionState Evolve(DecisionState state, object @event) => @event switch
    {
        DecisionEvent.DecisionProposed e => state with
        {
            ProjectId = e.ProjectId, Number = e.Number, Origin = e.Origin, Status = DecisionStatus.Proposed, Content = e.Content,
            CreatedBy = e.By, CreatedAt = e.At, UpdatedAt = e.At,
            History = [new DecisionHistoryEntry(e.At, e.By, "proposed", e.Origin)],
        },
        DecisionEvent.DecisionChanged e => state.Noted(e.At, e.By, "changed", null) with { Content = e.Content },
        DecisionEvent.DecisionAccepted e => state.Noted(e.At, e.By, "accepted", null) with
        {
            Status = DecisionStatus.Accepted, AcceptedBy = e.By, AcceptedAt = e.At, Stamps = [.. e.Stamps],
        },
        DecisionEvent.DecisionRejected e => state.Noted(e.At, e.By, "rejected", e.Reason) with { Status = DecisionStatus.Rejected },
        DecisionEvent.SettlementRecorded e when state.Content is { } content =>
            state.Noted(e.At, e.By, "settlement-recorded", $"{e.Stamp.RuleId} {e.Stamp.SubjectId}") with
            {
                Content = content.Settles.Any(s => s.RuleId == e.Stamp.RuleId && s.SubjectId == e.Stamp.SubjectId)
                    ? content
                    : content with { Settles = [.. content.Settles, new Settles(e.Stamp.RuleId, e.Stamp.SubjectId)] },
                Stamps = state.Stamps.RemoveAll(s => s.RuleId == e.Stamp.RuleId && s.SubjectId == e.Stamp.SubjectId).Add(e.Stamp),
            },
        DecisionEvent.SettlementRemoved e when state.Content is { } content =>
            state.Noted(e.At, e.By, "settlement-removed", $"{e.RuleId} {e.SubjectId}") with
            {
                Content = content with { Settles = [.. content.Settles.Where(s => !(s.RuleId == e.RuleId && s.SubjectId == e.SubjectId))] },
                Stamps = state.Stamps.RemoveAll(s => s.RuleId == e.RuleId && s.SubjectId == e.SubjectId),
            },
        DecisionEvent.DecisionDeleted e => state with { Deleted = true, UpdatedAt = e.At },
        _ => state,
    };

    /// <summary><c>ADR-0001</c>: the number in four digits. A number is issued once in a project and never again.</summary>
    public static string Label(int number) => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"ADR-{number:D4}");

    /// <summary>True when the decision exists in <paramref name="projectId"/>. A decision of another project reads as not found.</summary>
    public bool ExistsIn(Guid projectId) => !Deleted && ProjectId == projectId && Content is not null;

    private DecisionState Noted(DateTimeOffset at, string by, string action, string? note) =>
        this with { UpdatedAt = at, History = History.Add(new DecisionHistoryEntry(at, by, action, note)) };
}

/// <summary>A decision at one version of its stream: what an export pins and what a review reads.</summary>
public sealed record PinnedDecision(Guid Id, DecisionState State, long Version);

/// <summary>
/// The decisions of a project as one set. Which decision is in force depends on the others:
/// an accepted decision is superseded while another accepted decision names it.
/// </summary>
public sealed class DecisionBook
{
    private readonly Dictionary<Guid, PinnedDecision> _supersededBy = [];

    public DecisionBook(IEnumerable<PinnedDecision> decisions)
    {
        All = [.. decisions.OrderBy(d => d.State.Number).ThenBy(d => d.Id)];
        foreach (var decision in All.Where(d => d.State.Status == DecisionStatus.Accepted && d.State.Content!.Supersedes is not null))
            _supersededBy.TryAdd(decision.State.Content!.Supersedes!.Value, decision);
    }

    public static DecisionBook Empty { get; } = new([]);

    /// <summary>Every decision, by number.</summary>
    public IReadOnlyList<PinnedDecision> All { get; }

    public string StatusOf(PinnedDecision decision) =>
        decision.State.Status == DecisionStatus.Accepted && _supersededBy.ContainsKey(decision.Id) ? DecisionStatus.Superseded : decision.State.Status;

    public PinnedDecision? SupersededBy(Guid id) => _supersededBy.GetValueOrDefault(id);

    /// <summary>The accepted decisions that no accepted decision supersedes.</summary>
    public IEnumerable<PinnedDecision> InForce => All.Where(d => StatusOf(d) == DecisionStatus.Accepted);

    /// <summary>The decision in force that names the finding, with what the finding was when a person settled it. The lowest number wins.</summary>
    public (PinnedDecision Decision, SettlementStamp? Stamp)? Settling(string ruleId, Guid subjectId)
    {
        foreach (var decision in InForce)
        {
            if (decision.State.Content!.Settles.Any(s => s.RuleId == ruleId && s.SubjectId == subjectId))
                return (decision, decision.State.Stamps.FirstOrDefault(s => s.RuleId == ruleId && s.SubjectId == subjectId));
        }
        return null;
    }
}

/// <summary>Who first wrote a decision. Only a person in an interactive session accepts one, whatever its origin.</summary>
public static class DecisionOrigin
{
    public const string Human = PromiseOrigin.Human;
    public const string AiSuggested = PromiseOrigin.AiSuggested;
    public const string AgentSuggested = PromiseOrigin.AgentSuggested;
}
