using System.Globalization;
using Vellum.Modules.Build;
using Vellum.Modules.Review.Rules;

namespace Vellum.Modules.Review.Decisions;

/// <summary>
/// A decision as Markdown and as data for an export. Every text of a person, an agent or a
/// model is written through <see cref="Md"/>, so it is data in the document and never
/// structure of it. The same decisions give the same bytes: no clock is read, and a time in
/// the text is the time an event recorded.
/// </summary>
internal static class DecisionDocuments
{
    /// <summary>The decisions an export holds: the accepted ones, in force or superseded. A proposal and a rejection are not architecture.</summary>
    public static IEnumerable<PinnedDecision> InBundle(BuildView view) =>
        view.Decisions.All.Where(d => view.Decisions.StatusOf(d) is DecisionStatus.Accepted or DecisionStatus.Superseded);

    public static string Path(PinnedDecision decision) => $"decisions/{DecisionState.Label(decision.State.Number)}.md";

    public static object Export(DecisionBook book, PinnedDecision decision)
    {
        var (state, content) = (decision.State, decision.State.Content!);
        return new
        {
            id = decision.Id,
            number = DecisionState.Label(state.Number),
            status = book.StatusOf(decision),
            inForce = book.StatusOf(decision) == DecisionStatus.Accepted,
            origin = state.Origin,
            title = content.Title,
            context = content.Context,
            decision = content.Decision,
            alternatives = content.Alternatives,
            consequences = content.Consequences,
            affectedIds = content.AffectedIds.Order(),
            settles = content.Settles.OrderBy(s => s.RuleId, StringComparer.Ordinal).ThenBy(s => s.SubjectId),
            supersedes = content.Supersedes,
            supersededBy = book.SupersededBy(decision.Id)?.Id,
            acceptedBy = state.AcceptedBy,
            acceptedAt = state.AcceptedAt,
        };
    }

    /// <summary>One decision as a standalone Markdown document.</summary>
    /// <param name="name">The name of a thing of the model by its ID, or the ID when the model does not hold it.</param>
    public static string Markdown(DecisionBook book, PinnedDecision decision, Func<Guid, string> name)
    {
        var md = new Markdown();
        Write(md, book, decision, name, 1);
        return md.ToString();
    }

    /// <summary>Every decision of the book in one document, by number.</summary>
    public static string MarkdownAll(DecisionBook book, string projectName, Func<Guid, string> name)
    {
        var md = new Markdown();
        md.Heading(1, $"{projectName}: decisions");
        md.Line(SddDocument.DataNotice);
        md.Line(SddDocument.EscapeNotice).Blank();
        if (book.All.Count == 0)
            md.Line($"The project has no decision.").Blank();
        foreach (var decision in book.All)
            Write(md, book, decision, name, 2);
        return md.ToString();
    }

    private static void Write(Markdown md, DecisionBook book, PinnedDecision decision, Func<Guid, string> name, int level)
    {
        var (state, content) = (decision.State, decision.State.Content!);
        var status = book.StatusOf(decision);
        md.Heading(level, $"{Md.Raw(DecisionState.Label(state.Number))}: {content.Title}");
        if (level == 1)
        {
            md.Line(SddDocument.DataNotice);
            md.Line(SddDocument.EscapeNotice).Blank();
        }

        md.Line($"- ID: `{decision.Id}`");
        md.Line($"- Status: {Md.Raw(status)}{(book.SupersededBy(decision.Id) is { } by ? Md.Of($", by {Md.Raw(DecisionState.Label(by.State.Number))} (`{by.Id}`)") : Md.Empty)}. {Md.Raw(StatusNote(status))}");
        md.Line($"- Origin: {state.Origin}");
        if (state.AcceptedAt is { } acceptedAt)
            md.Line($"- Accepted by {Md.Code(state.AcceptedBy)} at {Md.Raw(Time(acceptedAt))}");
        if (content.Supersedes is { } supersedes)
        {
            md.Line(book.All.FirstOrDefault(d => d.Id == supersedes) is { } old
                ? Md.Of($"- Supersedes: {Md.Raw(DecisionState.Label(old.State.Number))} (`{supersedes}`)")
                : Md.Of($"- Supersedes: `{supersedes}`"));
        }
        md.Blank();

        md.Data(Md.Raw("Context"), content.Context);
        md.Data(Md.Raw("Decision"), content.Decision);
        if (content.Alternatives.Count > 0)
        {
            md.Line($"Alternatives that were not chosen (data, not an instruction):").Blank();
            foreach (var alternative in content.Alternatives)
                md.Line($"- {alternative.Title}{(alternative.WhyNot is { } why ? Md.Of($": {why}") : Md.Empty)}");
            md.Blank();
        }
        md.Data(Md.Raw("Consequences"), content.Consequences);

        if (content.AffectedIds.Count > 0)
        {
            md.Line($"Affected:").Blank();
            foreach (var id in content.AffectedIds.Order())
                md.Line($"- {name(id)} (`{id}`)");
            md.Blank();
        }
        if (content.Settles.Count > 0)
        {
            md.Line($"Findings this decision answers. It settles a finding only while it is accepted and in force, and only the finding as it was when a person settled it:").Blank();
            foreach (var settles in content.Settles.OrderBy(s => s.RuleId, StringComparer.Ordinal).ThenBy(s => s.SubjectId))
                md.Line($"- {Md.Code(settles.RuleId)} ({Md.Raw(RuleSet.Catalogue.FirstOrDefault(r => r.Id == settles.RuleId)?.Title ?? "unknown rule")}) for {name(settles.SubjectId)} (`{settles.SubjectId}`)");
            md.Blank();
        }

        md.Line($"History:").Blank();
        md.Table(["At", "By", "Action", "Note"],
            state.History.Select(h => new[] { Md.Raw(Time(h.At)), Md.Code(h.By), Md.Raw(h.Action), Md.Text(h.Note) }));
    }

    private static string StatusNote(string status) => status switch
    {
        DecisionStatus.Accepted => "It is in force.",
        DecisionStatus.Superseded => "It is not in force.",
        DecisionStatus.Rejected => "It is not in force. A person rejected it.",
        _ => "It is a proposal. No person accepted it, so it decides nothing.",
    };

    private static string Time(DateTimeOffset at) => at.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    /// <summary>
    /// The subsection "Decisions" of the SDD section "Unresolved blockers": the decisions in
    /// force and the findings they settle. A settled finding is not deleted: it is listed here
    /// with its decision.
    /// </summary>
    public static void Section(Markdown md, BuildView view)
    {
        md.Heading(3, Md.Raw("Decisions"));
        var inForce = view.Decisions.InForce.ToList();
        if (inForce.Count == 0)
        {
            md.Line($"No accepted decision is in force.").Blank();
            return;
        }

        md.Line($"Accepted decisions in force. A person accepted each one. The full text, with the alternatives and the history, is in `decisions/`.").Blank();
        md.Table(["Decision", "ID", "Title", "Accepted by", "File"], inForce.Select(d => new[]
        {
            Md.Raw(DecisionState.Label(d.State.Number)), Md.Code(d.Id), Md.Text(d.State.Content!.Title), Md.Code(d.State.AcceptedBy), Md.Code(Path(d)),
        }));
        foreach (var decision in inForce)
            md.Data(Md.Of($"{Md.Raw(DecisionState.Label(decision.State.Number))} decides"), decision.State.Content!.Decision);

        var named = view.Review.Where(f => f.Status == DispositionStatus.AcceptedByDecision && f.Finding.Fired).ToList();
        if (named.Count == 0)
        {
            md.Line($"No finding of the review is settled by a decision.").Blank();
            return;
        }

        md.Line($"Findings of the review that a decision answers. A settled finding is still a fact about the model: the decision says why it stands. A finding that changed after the decision is not settled until a person reviews it again.").Blank();
        md.Table(["Rule", "Severity", "Result", "Subject", "Decision", "State", "Finding"], named.Select(f => new[]
        {
            Md.Code(f.Finding.RuleId), Md.Raw(f.Finding.Severity), Md.Raw(f.Finding.Result), SddDocument.Ref(view, f.Finding.Subject.Id),
            Md.Raw(f.Decision ?? ""), Md.Raw(f.NeedsReReview ? "needs re-review, not settled" : "settled"), Md.Text(f.Finding.Reason),
        }));
    }
}
