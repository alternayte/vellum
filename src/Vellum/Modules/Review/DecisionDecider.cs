using Deedbox;
using Microsoft.EntityFrameworkCore;
using Vellum.Modules.Promises;
using Vellum.Modules.Review.Rules;
using Vellum.Shared;
using Outcome = Vellum.Shared.CommandResult<System.Collections.Generic.IReadOnlyList<object>>;

namespace Vellum.Modules.Review;

/// <summary>The bounds of a decision and of the decisions of a project.</summary>
public static class DecisionRules
{
    public const int MaxPerProject = 500;
    public const int MaxAlternatives = 20;
    public const int MaxAffected = 200;
    public const int MaxSettles = 100;
    /// <summary>The most history entries of one decision. Every entry is an event of its stream.</summary>
    public const int MaxHistory = 500;

    /// <summary>The content with its text trimmed, and no ID or pair twice.</summary>
    public static DecisionContent Normalize(DecisionContent content) => new(
        (content.Title ?? "").Trim(), (content.Context ?? "").Trim(), (content.Decision ?? "").Trim(),
        [.. (content.Alternatives ?? []).Where(a => a is not null).Select(a => new Alternative((a.Title ?? "").Trim(), string.IsNullOrWhiteSpace(a.WhyNot) ? null : a.WhyNot.Trim()))],
        string.IsNullOrWhiteSpace(content.Consequences) ? null : content.Consequences.Trim(),
        [.. (content.AffectedIds ?? []).Distinct()],
        [.. (content.Settles ?? []).Where(s => s is not null).DistinctBy(s => (s.RuleId, s.SubjectId))],
        content.Supersedes);

    public static IReadOnlyList<ValidationError> Check(DecisionContent content)
    {
        var errors = new List<ValidationError>();
        void Add(ValidationError? error)
        {
            if (error is not null)
                errors.Add(error);
        }

        if (content.Title.Length == 0) errors.Add(new("title", "A title is required"));
        if (content.Decision.Length == 0) errors.Add(new("decision", "The decision is required: say what was decided"));
        Add(TextRule.Line("title", content.Title));
        Add(TextRule.Lines("context", content.Context));
        Add(TextRule.Lines("decision", content.Decision));
        Add(TextRule.Lines("consequences", content.Consequences));
        if (content.Alternatives.Count > MaxAlternatives) errors.Add(new("alternatives", $"A decision lists at most {MaxAlternatives} alternatives"));
        foreach (var alternative in content.Alternatives.Take(MaxAlternatives))
        {
            if (alternative.Title.Length == 0) errors.Add(new("alternatives", "An alternative needs a title"));
            Add(TextRule.Line("alternatives", alternative.Title));
            Add(TextRule.Lines("alternatives", alternative.WhyNot));
        }
        if (content.AffectedIds.Count > MaxAffected) errors.Add(new("affectedIds", $"A decision names at most {MaxAffected} affected things"));
        if (content.Settles.Count > MaxSettles) errors.Add(new("settles", $"A decision settles at most {MaxSettles} findings"));
        if (content.Settles.FirstOrDefault(s => !RuleSet.IsRule(s.RuleId)) is { } unknown)
            errors.Add(new("settles", $"'{unknown.RuleId}' is not the ID of a review rule"));
        return errors;
    }
}

/// <summary>
/// The decisions of a decision (an ADR). A proposed decision can be changed. An accepted one
/// keeps its text: it is replaced by a new decision that supersedes it, or it is rejected.
/// Accepting, rejecting and settling a finding are acts of a person; the endpoints see to that.
/// </summary>
public static class DecisionDecider
{
    private const string NotFoundMessage = "Decision not found";

    public static Outcome Propose(DecisionState state, Guid projectId, int number, string origin, DecisionContent content, string userId, DateTimeOffset now)
    {
        if (state.Content is not null || state.Deleted)
            return new Outcome.Conflict(PromiseDecider.IdInUse);
        content = DecisionRules.Normalize(content);
        return DecisionRules.Check(content) is { Count: > 0 } errors
            ? new Outcome.Invalid(errors)
            : new Outcome.Success([new DecisionEvent.DecisionProposed(projectId, number, origin, content, userId, now)]);
    }

    public static Outcome Change(DecisionState state, Guid projectId, DecisionContent content, string userId, DateTimeOffset now)
    {
        if (!state.ExistsIn(projectId))
            return new Outcome.NotFound(NotFoundMessage);
        if (state.Status != DecisionStatus.Proposed)
            return new Outcome.Conflict($"The decision is {state.Status} and its text cannot be changed. Write a new decision that supersedes it.");
        if (Full(state) is { } full)
            return full;
        content = DecisionRules.Normalize(content);
        return DecisionRules.Check(content) is { Count: > 0 } errors
            ? new Outcome.Invalid(errors)
            : new Outcome.Success([new DecisionEvent.DecisionChanged(content, userId, now)]);
    }

    public const string ChangedMessage = "The decision changed after it was read. Read it again before you accept or reject it.";

    /// <summary>
    /// The content hash of the decision as it is now, when it is not the one the person read.
    /// Null when the hash names exactly this decision, and when the decision does not exist.
    /// </summary>
    public static string? Changed(DecisionState state, Guid projectId, string? contentHash)
    {
        if (!state.ExistsIn(projectId))
            return null;
        var current = DecisionContentHash.Of(state);
        return string.Equals(current, contentHash, StringComparison.Ordinal) ? null : current;
    }

    /// <param name="contentHash">The hash of what the person read. The acceptance binds that decision and no other.</param>
    /// <param name="stamp">Gives, for a finding the decision settles, what that finding is now.</param>
    public static Outcome Accept(DecisionState state, Guid projectId, string? contentHash, Func<Settles, SettlementStamp> stamp, string userId, DateTimeOffset now)
    {
        if (!state.ExistsIn(projectId))
            return new Outcome.NotFound(NotFoundMessage);
        if (Changed(state, projectId, contentHash) is not null)
            return new Outcome.Conflict(ChangedMessage);
        if (state.Status != DecisionStatus.Proposed)
            return new Outcome.Conflict($"The decision is {state.Status}. Only a proposed decision is accepted.");
        return new Outcome.Success([new DecisionEvent.DecisionAccepted(userId, now, [.. state.Content!.Settles.Select(stamp)], contentHash)]);
    }

    /// <param name="contentHash">The hash of what the person read: a proposal to reject, or an accepted decision to withdraw.</param>
    public static Outcome Reject(DecisionState state, Guid projectId, string? contentHash, string? reason, string userId, DateTimeOffset now)
    {
        if (!state.ExistsIn(projectId))
            return new Outcome.NotFound(NotFoundMessage);
        if (Changed(state, projectId, contentHash) is not null)
            return new Outcome.Conflict(ChangedMessage);
        if (state.Status == DecisionStatus.Rejected)
            return new Outcome.Conflict("The decision is rejected already.");
        if (TextRule.Lines("reason", reason) is { } error)
            return new Outcome.Invalid([error]);
        return new Outcome.Success([new DecisionEvent.DecisionRejected(userId, now, string.IsNullOrWhiteSpace(reason) ? null : reason.Trim())]);
    }

    public static Outcome RecordSettlement(DecisionState state, Guid projectId, SettlementStamp stamp, string userId, DateTimeOffset now)
    {
        if (!state.ExistsIn(projectId))
            return new Outcome.NotFound(NotFoundMessage);
        if (state.Status != DecisionStatus.Accepted)
            return new Outcome.Conflict($"The decision is {state.Status}. Only an accepted decision settles a finding.");
        if (Full(state) is { } full)
            return full;
        if (state.Content!.Settles.Count >= DecisionRules.MaxSettles && !state.Content.Settles.Any(s => s.RuleId == stamp.RuleId && s.SubjectId == stamp.SubjectId))
            return new Outcome.Conflict($"A decision settles at most {DecisionRules.MaxSettles} findings.");
        return new Outcome.Success([new DecisionEvent.SettlementRecorded(stamp, userId, now)]);
    }

    public static Outcome RemoveSettlement(DecisionState state, Guid projectId, string ruleId, Guid subjectId, string userId, DateTimeOffset now)
    {
        if (!state.ExistsIn(projectId))
            return new Outcome.NotFound(NotFoundMessage);
        if (!state.Content!.Settles.Any(s => s.RuleId == ruleId && s.SubjectId == subjectId))
            return new Outcome.Success([]);
        return Full(state) ?? new Outcome.Success([new DecisionEvent.SettlementRemoved(ruleId, subjectId, userId, now)]);
    }

    public static Outcome Delete(DecisionState state, Guid projectId, string userId, DateTimeOffset now)
    {
        if (!state.ExistsIn(projectId))
            return new Outcome.NotFound(NotFoundMessage);
        if (state.AcceptedAt is not null)
            return new Outcome.Conflict("A decision that was accepted is a record and is not deleted. Reject it, or supersede it with a new decision.");
        return new Outcome.Success([new DecisionEvent.DecisionDeleted(userId, now)]);
    }

    private static Outcome? Full(DecisionState state) => state.History.Count >= DecisionRules.MaxHistory
        ? new Outcome.Conflict($"The decision has {DecisionRules.MaxHistory} history entries, the most one decision holds. Write a new decision that supersedes it.")
        : null;
}
