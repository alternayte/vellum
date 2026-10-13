using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Deedbox;

namespace Vellum.Modules.Promises;

/// <summary>The events of a promise stream. Plain records; the stream registers every nested type.</summary>
public static class PromiseEvent
{
    public sealed record PromiseCreated(
        Guid ProjectId, string RequirementId, int Number, string Origin, PromiseContent Content, string CreatedBy, DateTimeOffset At);

    /// <summary>The draft revision has new content. An approved revision never gets this event.</summary>
    public sealed record PromiseDraftChanged(int Revision, PromiseContent Content, string By, DateTimeOffset At);

    /// <summary>
    /// A new draft revision that starts as a copy of the revision in force, obligations
    /// included. <paramref name="Retires"/>: approving it retires the promise.
    /// </summary>
    public sealed record PromiseRevised(int Revision, bool Retires, string By, DateTimeOffset At);

    /// <param name="ModelVersion">The version of the project's main model stream at approval.</param>
    /// <param name="Links">A hash of every linked thing as it was at approval, by <c>kind:id</c>.</param>
    public sealed record PromiseApproved(
        int Revision, string ApprovedBy, DateTimeOffset At, long ModelVersion, IReadOnlyDictionary<string, string> Links);

    public sealed record PromiseDeleted(string By, DateTimeOffset At);

    public sealed record ObligationAdded(int Revision, Obligation Obligation, string By, DateTimeOffset At);

    public sealed record ObligationChanged(int Revision, Obligation Obligation, string By, DateTimeOffset At);

    public sealed record ObligationRemoved(int Revision, string ObligationId, string By, DateTimeOffset At);

    public sealed record VerificationRunRecorded(VerificationRun Run);
}

public sealed record PromiseRevisionState(
    int Revision,
    string Status,
    bool Retires,
    PromiseContent Content,
    ImmutableList<Obligation> Obligations,
    string CreatedBy,
    DateTimeOffset CreatedAt,
    string? ApprovedBy = null,
    DateTimeOffset? ApprovedAt = null,
    long? ApprovedModelVersion = null,
    ImmutableDictionary<string, string>? ApprovedLinks = null)
{
    [JsonIgnore]
    public bool IsDraft => Status == RevisionStatus.Draft;

    public Obligation? Find(string key) =>
        Obligations.FirstOrDefault(o => string.Equals(o.Key, key, StringComparison.OrdinalIgnoreCase) || o.Id == key);
}

/// <summary>
/// One promise: its requirement ID and every revision it ever had. Runs are not part of the
/// state, because no decision depends on them; the read model keeps them.
/// </summary>
public sealed record PromiseState(
    Guid? ProjectId,
    string? RequirementId,
    int Number,
    string? Origin,
    string? CreatedBy,
    DateTimeOffset? CreatedAt,
    DateTimeOffset? UpdatedAt,
    ImmutableList<PromiseRevisionState> Revisions,
    int NextObligation,
    bool Deleted)
    : IState<PromiseState>
{
    public static PromiseState Initial => new(null, null, 0, null, null, null, null, [], 1, false);

    public static PromiseState Evolve(PromiseState state, object @event) => @event switch
    {
        PromiseEvent.PromiseCreated e => state with
        {
            ProjectId = e.ProjectId, RequirementId = e.RequirementId, Number = e.Number, Origin = e.Origin,
            CreatedBy = e.CreatedBy, CreatedAt = e.At, UpdatedAt = e.At,
            Revisions = [new PromiseRevisionState(1, RevisionStatus.Draft, false, e.Content, [], e.CreatedBy, e.At)],
        },

        PromiseEvent.PromiseDraftChanged e => state.WithRevision(e.Revision, e.At, r => r with { Content = e.Content }),

        PromiseEvent.PromiseRevised e when state.InForce is { } inForce => state with
        {
            UpdatedAt = e.At,
            Revisions = state.Revisions.Add(new PromiseRevisionState(
                e.Revision, RevisionStatus.Draft, e.Retires, inForce.Content, inForce.Obligations, e.By, e.At)),
        },

        PromiseEvent.PromiseApproved e => state.WithRevision(e.Revision, e.At, r => r with
        {
            Status = RevisionStatus.Approved, ApprovedBy = e.ApprovedBy, ApprovedAt = e.At,
            ApprovedModelVersion = e.ModelVersion, ApprovedLinks = e.Links.ToImmutableDictionary(),
        }),

        PromiseEvent.PromiseDeleted e => state with { Deleted = true, UpdatedAt = e.At },

        PromiseEvent.ObligationAdded e => state.WithRevision(e.Revision, e.At, r => r with { Obligations = r.Obligations.Add(e.Obligation) }) with
        {
            NextObligation = Math.Max(state.NextObligation, e.Obligation.Number + 1),
        },

        PromiseEvent.ObligationChanged e => state.WithRevision(e.Revision, e.At, r => r with
        {
            Obligations = r.Obligations.Select(o => o.Id == e.Obligation.Id ? e.Obligation : o).ToImmutableList(),
        }),

        PromiseEvent.ObligationRemoved e => state.WithRevision(e.Revision, e.At, r => r with
        {
            Obligations = r.Obligations.RemoveAll(o => o.Id == e.ObligationId),
        }),

        PromiseEvent.VerificationRunRecorded e => state with { UpdatedAt = e.Run.At },

        _ => state,
    };

    // The members below are derived from the revisions. They are not stored with the state.

    /// <summary>The newest revision, or null before the promise exists.</summary>
    [JsonIgnore]
    public PromiseRevisionState? Latest => Revisions.Count == 0 ? null : Revisions[^1];

    /// <summary>The unapproved revision, when the newest one is a draft.</summary>
    [JsonIgnore]
    public PromiseRevisionState? Draft => Latest is { IsDraft: true } draft ? draft : null;

    /// <summary>
    /// The newest approved revision. It stays in force while a later revision is a draft
    /// (SDD guarantee 12: a change of an approved promise does nothing until a person approves it).
    /// </summary>
    [JsonIgnore]
    public PromiseRevisionState? InForce => Revisions.LastOrDefault(r => !r.IsDraft);

    [JsonIgnore]
    public string Lifecycle => InForce switch
    {
        null => PromiseLifecycle.Draft,
        { Retires: true } => PromiseLifecycle.Retired,
        _ => PromiseLifecycle.Approved,
    };

    public PromiseRevisionState? Revision(int number) => Revisions.FirstOrDefault(r => r.Revision == number);

    /// <summary>True when the promise exists in <paramref name="projectId"/>. A promise of another project reads as not found.</summary>
    public bool ExistsIn(Guid projectId) => !Deleted && ProjectId == projectId && Revisions.Count > 0;

    private PromiseState WithRevision(int number, DateTimeOffset at, Func<PromiseRevisionState, PromiseRevisionState> update)
    {
        var index = Revisions.FindIndex(r => r.Revision == number);
        return index < 0 ? this : this with { UpdatedAt = at, Revisions = Revisions.SetItem(index, update(Revisions[index])) };
    }
}
