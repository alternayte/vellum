using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Deedbox;
using Vellum.Modules.Identity.AgentTokens;

namespace Vellum.Modules.Promises;

/// <summary>
/// The events of a promise stream. Plain records; the stream registers every nested type.
/// <para>
/// <c>By</c> and <c>CreatedBy</c> are the user: the person, or the user an agent token acts
/// for. <c>Actor</c> is who made the call (<c>AgentPrincipal.Actor</c>):
/// <c>user:&lt;id&gt;</c> for a person in a session, <c>agent-token:&lt;id&gt;</c> for an
/// agent token. It is null only in an event stored before the member existed; such an event
/// does not say whether a token made it.
/// </para>
/// </summary>
public static class PromiseEvent
{
    public sealed record PromiseCreated(
        Guid ProjectId, string RequirementId, int Number, string Origin, PromiseContent Content, string CreatedBy, DateTimeOffset At,
        string? Actor = null);

    /// <summary>The draft revision has new content. An approved revision never gets this event.</summary>
    public sealed record PromiseDraftChanged(int Revision, PromiseContent Content, string By, DateTimeOffset At, string? Actor = null);

    /// <summary>
    /// A new draft revision. It starts as a copy of the revision in force, obligations
    /// included. With <paramref name="Retires"/> it copies nothing: it only names the revision
    /// in force, and approving it retires the promise.
    /// </summary>
    public sealed record PromiseRevised(int Revision, bool Retires, string By, DateTimeOffset At, string? Actor = null);

    /// <param name="ModelVersion">The version of the project's main model stream at approval.</param>
    /// <param name="Links">A hash of every linked thing as it was at approval, by <c>kind:id</c>.</param>
    /// <param name="ContentHash">
    /// <see cref="PromiseContentHash"/> of the revision the approver read. Null only in an
    /// approval stored before the hash existed.
    /// </param>
    public sealed record PromiseApproved(
        int Revision, string ApprovedBy, DateTimeOffset At, long ModelVersion, IReadOnlyDictionary<string, string> Links,
        string? ContentHash = null);

    public sealed record PromiseDeleted(string By, DateTimeOffset At, string? Actor = null);

    public sealed record ObligationAdded(int Revision, Obligation Obligation, string By, DateTimeOffset At, string? Actor = null);

    public sealed record ObligationChanged(int Revision, Obligation Obligation, string By, DateTimeOffset At, string? Actor = null);

    public sealed record ObligationRemoved(int Revision, string ObligationId, string By, DateTimeOffset At, string? Actor = null);

    public sealed record VerificationRunRecorded(VerificationRun Run);
}

/// <summary>Who added an obligation to a draft and who changed it last, as the events name their actor.</summary>
/// <param name="LastChangedBy">Null while nobody changed the obligation after it was added.</param>
public sealed record ObligationActors(string? AddedBy, string? LastChangedBy)
{
    /// <summary>True when an agent token added the obligation and no person changed it since.</summary>
    [JsonIgnore]
    public bool AgentAlone => AgentPrincipal.IsAgentActor(AddedBy) && (LastChangedBy is null || AgentPrincipal.IsAgentActor(LastChangedBy));
}

/// <param name="RetiresRevision">
/// For a revision that retires the promise: the revision it retires. Such a revision holds no
/// content and no obligations of its own; a reader takes the text from the one it names.
/// </param>
/// <param name="ApprovedContentHash">The hash the approver sent and the decision checked.</param>
/// <param name="ApprovalIntact">
/// False when the content folded from the stream at the approval is not the content of
/// <paramref name="ApprovedContentHash"/>. Every fold checks it: an append, a load and a rebuild.
/// </param>
/// <param name="LastChangedBy">
/// The actor of the last event that changed the content or the obligations of the revision
/// (<c>user:&lt;id&gt;</c> or <c>agent-token:&lt;id&gt;</c>). An approval is not a change. Null
/// when that event was stored before events named their actor.
/// </param>
/// <param name="ObligationActors">
/// By obligation ID, for the obligations that were added to this revision while it was a
/// draft. An obligation that a new revision copied from an approved one has no entry: a
/// person approved it.
/// </param>
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
    ImmutableDictionary<string, string>? ApprovedLinks = null,
    int? RetiresRevision = null,
    string? ApprovedContentHash = null,
    bool? ApprovalIntact = null,
    string? LastChangedBy = null,
    DateTimeOffset? LastChangedAt = null,
    ImmutableDictionary<string, ObligationActors>? ObligationActors = null)
{
    [JsonIgnore]
    public bool IsDraft => Status == RevisionStatus.Draft;

    public Obligation? Find(string key) =>
        Obligations.FirstOrDefault(o => string.Equals(o.Key, key, StringComparison.OrdinalIgnoreCase) || o.Id == key);

    public ObligationActors? ActorsOf(string obligationId) =>
        ObligationActors is { } actors && actors.TryGetValue(obligationId, out var found) ? found : null;

    /// <summary>The revision after a change by <paramref name="actor"/> at <paramref name="at"/>.</summary>
    internal PromiseRevisionState ChangedBy(string? actor, DateTimeOffset at) =>
        this with { LastChangedBy = actor, LastChangedAt = actor is null ? null : at };

    internal PromiseRevisionState WithActors(string obligationId, ObligationActors? actors)
    {
        var all = ObligationActors ?? ImmutableDictionary<string, ObligationActors>.Empty;
        all = actors is null ? all.Remove(obligationId) : all.SetItem(obligationId, actors);
        return this with { ObligationActors = all.IsEmpty ? null : all };
    }
}

/// <summary>
/// One promise: its requirement ID and every revision it ever had. Runs are not part of the
/// state, because no decision depends on them; the read model keeps them.
/// </summary>
/// <param name="AgentOnly">
/// True while every event of the promise names an agent token as its actor: agent tokens
/// alone wrote it, and no person changed or approved it. False from the first event that a
/// person made, and for every promise that holds an event with no actor.
/// </param>
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
    bool Deleted,
    ImmutableDictionary<string, int>? RunCounts = null,
    bool AgentOnly = false)
    : IState<PromiseState>
{
    public static PromiseState Initial => new(null, null, 0, null, null, null, null, [], 1, false);

    /// <summary>The content of a revision that retires the promise: the title of what it retires and nothing else.</summary>
    private static PromiseContent Retiring(PromiseContent retired) =>
        new(retired.Title, "This revision retires the promise.", null, null, null, null, null, [], null, PromiseLinks.None);

    public static PromiseState Evolve(PromiseState state, object @event) => @event switch
    {
        PromiseEvent.PromiseCreated e => state with
        {
            ProjectId = e.ProjectId,
            RequirementId = e.RequirementId,
            Number = e.Number,
            Origin = e.Origin,
            CreatedBy = e.CreatedBy,
            CreatedAt = e.At,
            UpdatedAt = e.At,
            Revisions = [new PromiseRevisionState(1, RevisionStatus.Draft, false, e.Content, [], e.CreatedBy, e.At).ChangedBy(e.Actor, e.At)],
            AgentOnly = AgentPrincipal.IsAgentActor(e.Actor),
        },

        PromiseEvent.PromiseDraftChanged e => state
            .WithRevision(e.Revision, e.At, r => (r with { Content = e.Content }).ChangedBy(e.Actor, e.At))
            .TouchedBy(e.Actor),

        // A revision that retires copies nothing, so a promise of any size can be retired.
        PromiseEvent.PromiseRevised e when state.InForce is { } inForce => state with
        {
            UpdatedAt = e.At,
            Revisions = state.Revisions.Add((e.Retires
                ? new PromiseRevisionState(e.Revision, RevisionStatus.Draft, true, Retiring(inForce.Content), [], e.By, e.At)
                { RetiresRevision = inForce.Revision }
                : new PromiseRevisionState(e.Revision, RevisionStatus.Draft, false, inForce.Content, inForce.Obligations, e.By, e.At))
                .ChangedBy(e.Actor, e.At)),
        },

        PromiseEvent.PromiseApproved e => state.WithRevision(e.Revision, e.At, r => r with
        {
            Status = RevisionStatus.Approved,
            ApprovedBy = e.ApprovedBy,
            ApprovedAt = e.At,
            ApprovedModelVersion = e.ModelVersion,
            ApprovedLinks = e.Links.ToImmutableDictionary(),
            ApprovedContentHash = e.ContentHash,
            ApprovalIntact = e.ContentHash is null ? null : e.ContentHash == PromiseContentHash.Of(state.RequirementId, r),
        }) with
        {
            // Only a person approves.
            AgentOnly = false,
        },

        PromiseEvent.PromiseDeleted e => state with { Deleted = true, UpdatedAt = e.At },

        PromiseEvent.ObligationAdded e => state
            .WithRevision(e.Revision, e.At, r => (r with { Obligations = r.Obligations.Add(e.Obligation) })
                .WithActors(e.Obligation.Id, e.Actor is null ? null : new ObligationActors(e.Actor, null))
                .ChangedBy(e.Actor, e.At))
            .TouchedBy(e.Actor) with
        {
            NextObligation = Math.Max(state.NextObligation, e.Obligation.Number + 1),
        },

        // A change by an actor that is not named leaves no entry: nothing then says that an agent alone wrote the obligation.
        PromiseEvent.ObligationChanged e => state
            .WithRevision(e.Revision, e.At, r => (r with
            {
                Obligations = r.Obligations.Select(o => o.Id == e.Obligation.Id ? e.Obligation : o).ToImmutableList(),
            })
                .WithActors(e.Obligation.Id, e.Actor is null ? null : new ObligationActors(r.ActorsOf(e.Obligation.Id)?.AddedBy, e.Actor))
                .ChangedBy(e.Actor, e.At))
            .TouchedBy(e.Actor),

        PromiseEvent.ObligationRemoved e => state
            .WithRevision(e.Revision, e.At, r => (r with { Obligations = r.Obligations.RemoveAll(o => o.Id == e.ObligationId) })
                .WithActors(e.ObligationId, null)
                .ChangedBy(e.Actor, e.At))
            .TouchedBy(e.Actor),

        PromiseEvent.VerificationRunRecorded e => state with
        {
            UpdatedAt = e.Run.At,
            RunCounts = (state.RunCounts ?? ImmutableDictionary<string, int>.Empty)
                .SetItem(RunKey(e.Run.PromiseRevision, e.Run.ObligationId), state.RunsOf(e.Run.PromiseRevision, e.Run.ObligationId) + 1),
        },

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

    /// <summary>
    /// The revision whose text and obligations a reader shows for <paramref name="revision"/>:
    /// the revision itself, or the one it retires.
    /// </summary>
    public PromiseRevisionState Shown(PromiseRevisionState revision) =>
        revision.RetiresRevision is { } retired && Revision(retired) is { } source ? source : revision;

    public static string RunKey(int revision, string obligationId) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{revision}:{obligationId}");

    /// <summary>How many runs were recorded for an obligation of a revision.</summary>
    public int RunsOf(int revision, string obligationId) =>
        RunCounts is { } counts && counts.TryGetValue(RunKey(revision, obligationId), out var count) ? count : 0;

    /// <summary>True when the promise exists in <paramref name="projectId"/>. A promise of another project reads as not found.</summary>
    public bool ExistsIn(Guid projectId) => !Deleted && ProjectId == projectId && Revisions.Count > 0;

    /// <summary>The state after an event by <paramref name="actor"/>: no longer of agent tokens alone when the actor is not one.</summary>
    private PromiseState TouchedBy(string? actor) =>
        AgentOnly && !AgentPrincipal.IsAgentActor(actor) ? this with { AgentOnly = false } : this;

    private PromiseState WithRevision(int number, DateTimeOffset at, Func<PromiseRevisionState, PromiseRevisionState> update)
    {
        var index = Revisions.FindIndex(r => r.Revision == number);
        return index < 0 ? this : this with { UpdatedAt = at, Revisions = Revisions.SetItem(index, update(Revisions[index])) };
    }
}
