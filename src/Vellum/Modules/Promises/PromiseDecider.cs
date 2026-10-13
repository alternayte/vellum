using Vellum.Modules.Identity.AgentTokens;
using Vellum.Shared;
using Decision = Vellum.Shared.CommandResult<System.Collections.Generic.IReadOnlyList<object>>;

namespace Vellum.Modules.Promises;

/// <summary>
/// The decisions of a promise. An approved revision is never changed: a change is a new draft
/// revision, and the approved one stays in force until a person approves the new one
/// (SDD guarantees 11 and 12). A run records a result and changes nothing else.
/// </summary>
public static class PromiseDecider
{
    private const string NotFoundMessage = "Promise not found";

    /// <summary>One answer for an ID that is taken, whatever holds it (see <c>ScenarioDecider.IdInUse</c>).</summary>
    public const string IdInUse = "This ID is already in use. Choose another ID.";

    public static Decision Create(
        PromiseState state, Guid projectId, string requirementId, int number, string origin,
        PromiseContent content, KnownThings known, string userId, string actor, DateTimeOffset now)
    {
        if (state.Revisions.Count > 0 || state.Deleted)
            return new Decision.Conflict(IdInUse);
        if (!PromiseOrigin.All.Contains(origin))
            return Invalid("origin", $"The origin is one of: {string.Join(", ", PromiseOrigin.All)}");

        content = PromiseRules.Normalize(content);
        return PromiseRules.Check(content, known) is { Count: > 0 } errors
            ? new Decision.Invalid(errors)
            : new Decision.Success([new PromiseEvent.PromiseCreated(projectId, requirementId, number, origin, content, userId, now, actor)]);
    }

    public static Decision ChangeDraft(
        PromiseState state, Guid projectId, PromiseContent content, KnownThings known, string userId, string actor, DateTimeOffset now)
    {
        if (Draft(state, projectId, out var draft) is { } refusal)
            return refusal;

        content = PromiseRules.Normalize(content);
        return PromiseRules.Check(content, known) is { Count: > 0 } errors
            ? new Decision.Invalid(errors)
            : new Decision.Success([new PromiseEvent.PromiseDraftChanged(draft.Revision, content, userId, now, actor)]);
    }

    public static Decision Revise(PromiseState state, Guid projectId, bool retires, string userId, string actor, DateTimeOffset now)
    {
        if (!state.ExistsIn(projectId))
            return new Decision.NotFound(NotFoundMessage);
        if (state.Draft is { } draft)
            return new Decision.Conflict($"Revision {draft.Revision} is a draft. Change or approve it before you start another revision.");
        if (state.Lifecycle == PromiseLifecycle.Retired)
            return new Decision.Conflict("The promise is retired.");
        if (state.Revisions.Count >= PromiseRules.MaxRevisions)
            return new Decision.Conflict($"A promise has at most {PromiseRules.MaxRevisions} revisions.");

        return new Decision.Success([new PromiseEvent.PromiseRevised(state.Latest!.Revision + 1, retires, userId, now, actor)]);
    }

    /// <summary>
    /// The draft as it is now, when it is not the one the approver read: another revision
    /// number, or the same number with other content. Null when the approval names exactly
    /// the draft, and when there is no draft at all.
    /// </summary>
    public static (int Revision, string ContentHash)? Changed(PromiseState state, Guid projectId, int revision, string? contentHash)
    {
        if (!state.ExistsIn(projectId) || state.Draft is not { } draft)
            return null;
        var current = PromiseContentHash.Of(state.RequirementId, draft);
        return draft.Revision == revision && string.Equals(current, contentHash, StringComparison.Ordinal) ? null : (draft.Revision, current);
    }

    /// <param name="revision">The draft the approver read.</param>
    /// <param name="contentHash">The hash of what the approver read. The approval binds that content and no other.</param>
    public static Decision Approve(
        PromiseState state, Guid projectId, int revision, string? contentHash, KnownThings known, long modelVersion,
        IReadOnlyDictionary<string, string> linkHashes, string userId, DateTimeOffset now)
    {
        if (!state.ExistsIn(projectId))
            return new Decision.NotFound(NotFoundMessage);
        if (state.Draft is not { } draft)
            return new Decision.Conflict("The promise has no draft revision to approve.");
        if (Changed(state, projectId, revision, contentHash) is { } changed)
        {
            return new Decision.Conflict(changed.Revision != revision
                ? $"The draft is revision {changed.Revision}, not {revision}. Read it again before you approve."
                : $"Revision {revision} changed after it was read. Read it again before you approve.");
        }

        // A revision that retires the promise needs no more than the approval itself.
        if (!draft.Retires)
        {
            var errors = PromiseRules.Check(draft.Content, known).Concat(PromiseRules.CheckComplete(draft.Content)).ToList();
            if (errors.Count > 0)
                return new Decision.Invalid(errors);
        }

        return new Decision.Success([new PromiseEvent.PromiseApproved(draft.Revision, userId, now, modelVersion, linkHashes, contentHash)]);
    }

    /// <summary>
    /// True when an agent token may delete the promise: it exists, agent tokens alone wrote
    /// it, and no person changed or approved it. Everything else is for a person.
    /// </summary>
    public static bool AgentAlone(PromiseState state) => state.Origin == PromiseOrigin.AgentSuggested && state.AgentOnly;

    /// <summary>
    /// True when an agent token may change or remove the obligation <paramref name="key"/>
    /// of the draft: an agent token added it to this draft and no person changed it since.
    /// </summary>
    public static bool AgentAlone(PromiseState state, string key) =>
        state.Draft is { } draft && draft.Find(key) is { } obligation && draft.ActorsOf(obligation.Id) is { AgentAlone: true };

    public static Decision Delete(PromiseState state, Guid projectId, string userId, string actor, DateTimeOffset now)
    {
        if (!state.ExistsIn(projectId))
            return new Decision.NotFound(NotFoundMessage);
        if (state.InForce is not null)
            return new Decision.Conflict("An approved promise is not deleted. Approve a revision that retires it.");

        return new Decision.Success([new PromiseEvent.PromiseDeleted(userId, now, actor)]);
    }

    public static Decision AddObligation(
        PromiseState state, Guid projectId, PromiseRules.ObligationInput input, KnownThings known, string userId, string actor, DateTimeOffset now)
    {
        if (Draft(state, projectId, out var draft) is { } refusal)
            return refusal;
        if (draft.Obligations.Count >= PromiseRules.MaxObligations)
            return new Decision.Conflict($"A revision has at most {PromiseRules.MaxObligations} obligations.");
        if (Checked(input, known, out var checkedInput) is { } invalid)
            return invalid;

        return new Decision.Success(
            [new PromiseEvent.ObligationAdded(
                draft.Revision,
                New(state, state.NextObligation, checkedInput, AgentPrincipal.IsAgentActor(actor) ? Obligation.Agent : Obligation.Manual, null),
                userId, now, actor)]);
    }

    public static Decision ChangeObligation(
        PromiseState state, Guid projectId, string key, PromiseRules.ObligationInput input, KnownThings known, string userId, string actor, DateTimeOffset now)
    {
        if (Draft(state, projectId, out var draft) is { } refusal)
            return refusal;
        if (draft.Find(key) is not { } existing)
            return new Decision.NotFound("Obligation not found");
        if (Checked(input, known, out var checkedInput) is { } invalid)
            return invalid;

        return new Decision.Success([new PromiseEvent.ObligationChanged(
            draft.Revision, existing with { Kind = checkedInput.Kind, Title = checkedInput.Title, Recipe = checkedInput.Recipe }, userId, now, actor)]);
    }

    public static Decision RemoveObligation(PromiseState state, Guid projectId, string key, string userId, string actor, DateTimeOffset now)
    {
        if (Draft(state, projectId, out var draft) is { } refusal)
            return refusal;

        return draft.Find(key) is { } existing
            ? new Decision.Success([new PromiseEvent.ObligationRemoved(draft.Revision, existing.Id, userId, now, actor)])
            : new Decision.NotFound("Obligation not found");
    }

    /// <summary>
    /// Adds each generated obligation that the draft does not hold yet, by generator key, in
    /// the order given. Generating twice adds nothing the second time, and an obligation a
    /// person edited is kept as edited. One a person deleted is added again, under a new ID,
    /// when they generate again.
    /// </summary>
    public static Decision AddGenerated(
        PromiseState state, Guid projectId, Func<PromiseRevisionState, IReadOnlyList<ObligationGenerator.Candidate>> generate,
        string userId, string actor, DateTimeOffset now)
    {
        if (Draft(state, projectId, out var draft) is { } refusal)
            return refusal;

        var present = draft.Obligations.Select(o => o.GeneratorKey).OfType<string>().ToHashSet(StringComparer.Ordinal);
        var events = new List<object>();
        var number = state.NextObligation;
        foreach (var candidate in generate(draft).Where(c => !present.Contains(c.Key)))
        {
            if (draft.Obligations.Count + events.Count >= PromiseRules.MaxObligations)
                return new Decision.Conflict($"A revision has at most {PromiseRules.MaxObligations} obligations.");
            events.Add(new PromiseEvent.ObligationAdded(
                draft.Revision, New(state, number++, candidate.Input, Obligation.Generated, candidate.Key), userId, now, actor));
        }

        return new Decision.Success(events);
    }

    /// <summary>
    /// A run is recorded for an obligation of any revision; it changes neither. The run says
    /// whether the revision was a draft when it was made, and the stream decides that, not the
    /// caller: a revision that was approved between the check and this append is not a draft.
    /// </summary>
    public static Decision RecordRun(PromiseState state, Guid projectId, VerificationRun run, int maxRuns)
    {
        if (!state.ExistsIn(projectId))
            return new Decision.NotFound(NotFoundMessage);
        if (state.Revision(run.PromiseRevision) is not { } revision || revision.Obligations.All(o => o.Id != run.ObligationId))
            return new Decision.NotFound("Obligation not found");
        if (state.RunsOf(run.PromiseRevision, run.ObligationId) >= maxRuns)
        {
            return new Decision.Conflict(
                $"{run.ObligationId} of revision {run.PromiseRevision} already has {maxRuns} runs, the most one obligation of one revision holds. Start a new revision to record more.");
        }

        return new Decision.Success([new PromiseEvent.VerificationRunRecorded(run with { Draft = revision.IsDraft ? true : null })]);
    }

    private static Decision? Draft(PromiseState state, Guid projectId, out PromiseRevisionState draft)
    {
        draft = null!;
        if (!state.ExistsIn(projectId))
            return new Decision.NotFound(NotFoundMessage);
        if (state.Draft is not { } found)
        {
            return new Decision.Conflict(
                $"Revision {state.Latest!.Revision} is approved and cannot be changed. Start a new revision; it needs approval again.");
        }
        // A revision that retires names the revision it retires and holds nothing else.
        if (found.Retires)
        {
            return new Decision.Conflict(
                $"Revision {found.Revision} retires the promise. It has no content and no obligations to change; approve it to retire the promise.");
        }

        draft = found;
        return null;
    }

    private static Decision? Checked(PromiseRules.ObligationInput input, KnownThings known, out PromiseRules.ObligationInput checkedInput)
    {
        checkedInput = input.Recipe is null ? input : input with { Recipe = PromiseRules.Normalize(input.Recipe) };
        return PromiseRules.Check(checkedInput, known) is { Count: > 0 } errors ? new Decision.Invalid(errors) : null;
    }

    private static Obligation New(PromiseState state, int number, PromiseRules.ObligationInput input, string source, string? generatorKey) =>
        new($"{state.RequirementId}/O{number}", number, input.Kind, input.Title, input.Recipe, source, generatorKey);

    private static Decision Invalid(string field, string message) => new Decision.Invalid([new ValidationError(field, message)]);
}
