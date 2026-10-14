using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Vellum.Modules.Ai.Design;
using Vellum.Modules.Review;
using Vellum.Modules.Review.Rules;
using Vellum.Shared;

namespace Vellum.Modules.Review.Critique;

/// <summary>The flat answer a model gives for a critique. Every reference is an alias. No member is null on the wire.</summary>
public sealed record CritiqueOutput(
    IReadOnlyList<CritiqueQuestionOutput>? Questions, IReadOnlyList<CritiqueRiskOutput>? Risks, IReadOnlyList<CritiqueDecisionOutput>? ProposedDecisions);

public sealed record CritiqueQuestionOutput(IReadOnlyList<string>? About, string? Text, string? Why);

/// <param name="Basis"><c>rule:&lt;rule ID&gt;</c>, <c>model:&lt;alias&gt;</c> or <c>none</c>.</param>
public sealed record CritiqueRiskOutput(IReadOnlyList<string>? About, string? Claim, string? Basis);

/// <param name="Alternatives">Each as <c>&lt;option&gt;: &lt;why not&gt;</c>.</param>
/// <param name="SettlesRule">A rule ID, or the empty string.</param>
public sealed record CritiqueDecisionOutput(
    string? Title, string? Context, string? Decision, IReadOnlyList<string>? Alternatives, string? SettlesRule, IReadOnlyList<string>? About);

/// <summary>
/// The JSON Schema of the critique answer. It is inside the subset that every vendor
/// documents for structured output, as <see cref="DesignSchemas"/> describes: six keywords,
/// no union type, no optional property, no recursion. A text that an item does not use is
/// the empty string. <c>DesignSchemaTests</c> walks it with the same limits.
/// </summary>
internal static class CritiqueSchema
{
    public static readonly JsonElement Schema = JsonSerializer.SerializeToElement(Object(
        ("questions", Array(Object(("about", Array(Text())), ("text", Text()), ("why", Text())))),
        ("risks", Array(Object(("about", Array(Text())), ("claim", Text()), ("basis", Text())))),
        ("proposedDecisions", Array(Object(
            ("title", Text()), ("context", Text()), ("decision", Text()),
            ("alternatives", Array(Text())), ("settlesRule", Text()), ("about", Array(Text())))))));

    private static JsonObject Object(params (string Name, JsonObject Schema)[] properties)
    {
        var listed = new JsonObject();
        var required = new JsonArray();
        foreach (var (name, schema) in properties)
        {
            listed[name] = schema;
            required.Add(name);
        }
        return new JsonObject { ["type"] = "object", ["properties"] = listed, ["required"] = required, ["additionalProperties"] = false };
    }

    private static JsonObject Array(JsonObject items) => new() { ["type"] = "array", ["items"] = items };

    private static JsonObject Text() => new() { ["type"] = "string" };
}

/// <param name="Source"><c>assistant</c> for a question the assistant asked, <c>unsupported-risk</c> for a risk it claimed with no finding behind it.</param>
/// <param name="Note">For <c>unsupported-risk</c>: why the model does not show it.</param>
/// <param name="KeptAs">The open question of the project that a person made of it.</param>
public sealed record CritiqueQuestion(IReadOnlyList<Cited> About, string Text, string? Why, string Source, string? Note, Guid? KeptAs = null);

/// <summary>
/// A risk the assistant named that a finding of a rule supports. The finding is the fact. The
/// claim is the assistant's sentence: the server checks that the rule fired for a part the
/// risk names, and it does not check what the sentence says. A reader gets the sentence of
/// the finding beside it, and the claim is never the result of the rule.
/// </summary>
/// <param name="Claim">The sentence of the assistant.</param>
/// <param name="RuleId">The rule whose finding supports the risk.</param>
/// <param name="SubjectId">The subject of that finding.</param>
/// <param name="FindingResult">The result of that finding: <c>concern</c> or <c>unknown</c>. Null in a critique that was stored before it was kept.</param>
/// <param name="FindingReason">The sentence of that finding, as the rule gave it when the critique was made. Null in a critique that was stored before it was kept.</param>
/// <param name="ClaimSource">Always <c>assistant</c>: who wrote <paramref name="Claim"/>.</param>
public sealed record CritiqueRisk(
    IReadOnlyList<Cited> About, string Claim, string RuleId, Guid SubjectId,
    string? FindingResult = null, string? FindingReason = null, string ClaimSource = "assistant");

/// <param name="Settles">The findings the decision would answer: only findings that the named rule gave for a named part.</param>
/// <param name="KeptAs">The proposed decision that a person made of it.</param>
public sealed record CritiqueProposedDecision(
    string Title, string Context, string Decision, IReadOnlyList<Alternative> Alternatives,
    IReadOnlyList<Settles> Settles, IReadOnlyList<Cited> About, Guid? KeptAs = null);

/// <summary>An item of the answer that is not shown, or a part of one that was dropped.</summary>
/// <param name="Where">For example <c>question:2</c>, <c>risk:0</c> or <c>proposedDecision:1.settlesRule</c>.</param>
public sealed record CritiqueLeftOut(string Where, string Reason);

public sealed record CritiqueContent(
    IReadOnlyList<CritiqueQuestion> Questions, IReadOnlyList<CritiqueRisk> Risks,
    IReadOnlyList<CritiqueProposedDecision> ProposedDecisions, IReadOnlyList<CritiqueLeftOut> LeftOut);

/// <summary>
/// The server-side check of a critique. The prompt asks for grounded output; this code makes
/// it so, whatever the model wrote:
/// <list type="bullet">
/// <item>Every alias of an item must be in the tables of the request. An item with another
/// reference is left out, with the reason.</item>
/// <item>An item that states that something is verified, proven, safe, secure, scalable,
/// compliant, guaranteed or resolved is left out. The assistant cannot establish that, and a
/// negation cannot be told from an assertion reliably, so the word alone decides.</item>
/// <item>A risk is shown as a risk only when its basis is <c>rule:&lt;id&gt;</c> and that rule
/// gave a concern or an unknown for one of the parts it names. Every other risk (no basis, a
/// rule that did not fire there, a basis in the model that no rule checks) is shown as a
/// question that says the model does not show it.</item>
/// <item>A proposed decision keeps <c>settlesRule</c> only for the findings that rule gave
/// for the parts it names.</item>
/// </list>
/// Nothing here writes: a critique makes no finding, no disposition and no decision.
/// </summary>
internal static partial class CritiqueGrounding
{
    public const int MaxQuestions = 20;
    public const int MaxRisks = 20;
    public const int MaxDecisions = 10;
    private const int MaxAbout = 20;

    /// <summary>The words that claim an assurance. See the summary of the class.</summary>
    public static readonly string[] AssuranceWords = ["verified", "proven", "safe", "secure", "scalable", "compliant", "guaranteed", "resolved"];

    [GeneratedRegex(@"\b(verified|proven|safe|secure|scalable|compliant|guaranteed|resolved)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Assurance();

    public static CritiqueContent Ground(CritiqueOutput output, IReadOnlyDictionary<string, Cited> aliases, IReadOnlyList<Finding> findings)
    {
        var fired = findings.Where(f => f.Fired).ToLookup(f => f.RuleId, StringComparer.Ordinal);
        var questions = new List<CritiqueQuestion>();
        var risks = new List<CritiqueRisk>();
        var decisions = new List<CritiqueProposedDecision>();
        var leftOut = new List<CritiqueLeftOut>();

        // The parts an item names, or null with the item left out when one of them names nothing.
        List<Cited>? About(string where, IReadOnlyList<string>? given)
        {
            var about = new List<Cited>();
            foreach (var alias in (given ?? []).Take(MaxAbout))
            {
                if (alias is null || !aliases.TryGetValue(alias.Trim(), out var cited))
                {
                    leftOut.Add(new(where, $"It names '{DesignText.Line(alias, 40)}', which is not in the tables of the request."));
                    return null;
                }
                if (about.All(c => c.Id != cited.Id))
                    about.Add(cited);
            }
            return about;
        }

        bool Assures(string where, params string?[] texts)
        {
            if (texts.Select(t => Assurance().Match(t ?? "")).FirstOrDefault(m => m.Success) is not { } match)
                return false;
            leftOut.Add(new(where, $"It states that something is '{match.Value.ToLowerInvariant()}'. The assistant cannot establish that."));
            return true;
        }

        foreach (var (question, i) in (output.Questions ?? []).Where(q => q is not null).Select((q, i) => (q, i)))
        {
            var where = $"question:{i}";
            var text = DesignText.Lines(question.Text);
            if (questions.Count >= MaxQuestions) { leftOut.Add(new(where, $"An answer holds at most {MaxQuestions} questions.")); continue; }
            if (text.Length == 0) { leftOut.Add(new(where, "It has no text.")); continue; }
            if (Assures(where, question.Text, question.Why) || About(where, question.About) is not { } about)
                continue;
            questions.Add(new(about, text, Optional(question.Why), "assistant", null));
        }

        foreach (var (risk, i) in (output.Risks ?? []).Where(r => r is not null).Select((r, i) => (r, i)))
        {
            var where = $"risk:{i}";
            var claim = DesignText.Lines(risk.Claim);
            if (questions.Count + risks.Count >= MaxQuestions + MaxRisks) { leftOut.Add(new(where, $"An answer holds at most {MaxRisks} risks.")); continue; }
            if (claim.Length == 0) { leftOut.Add(new(where, "It has no claim.")); continue; }
            if (Assures(where, risk.Claim) || About(where, risk.About) is not { } about)
                continue;

            var basis = (risk.Basis ?? "").Trim();
            string note;
            if (basis.StartsWith("rule:", StringComparison.Ordinal))
            {
                var ruleId = basis[5..].Trim();
                // The rule must have fired for a part the risk names: as the subject of a finding, or cited by one.
                var support = fired[ruleId].FirstOrDefault(f => about.Any(a => a.Id == f.Subject.Id))
                              ?? fired[ruleId].FirstOrDefault(f => about.Any(a => f.Cites.Any(c => c.Id == a.Id)));
                if (support is not null)
                {
                    risks.Add(new(about, claim, support.RuleId, support.Subject.Id, support.Result, support.Reason));
                    continue;
                }
                note = RuleSet.IsRule(ruleId)
                    ? $"The model does not show it: the rule {ruleId} gave no concern for the parts it names."
                    : $"The model does not show it: '{DesignText.Line(ruleId, 40)}' is not a review rule.";
            }
            else if (basis.StartsWith("model:", StringComparison.Ordinal))
            {
                note = aliases.TryGetValue(basis[6..].Trim(), out var thing)
                    ? $"The model does not show it: the assistant points at the {thing.Kind} '{thing.Name}', and no review rule checks this."
                    : $"The model does not show it: the assistant points at '{DesignText.Line(basis[6..], 40)}', which is not in the model.";
            }
            else
                note = "The model does not show it: the assistant gave no basis.";

            // Demoted: a claim with no finding behind it is something to ask, not something known.
            questions.Add(new(about, $"The assistant suspects: {claim}", null, "unsupported-risk", note));
        }

        foreach (var (proposed, i) in (output.ProposedDecisions ?? []).Where(d => d is not null).Select((d, i) => (d, i)))
        {
            var where = $"proposedDecision:{i}";
            var (title, decision) = (DesignText.Line(proposed.Title), DesignText.Lines(proposed.Decision, TextRule.MaxDescription));
            if (decisions.Count >= MaxDecisions) { leftOut.Add(new(where, $"An answer holds at most {MaxDecisions} proposed decisions.")); continue; }
            if (title.Length == 0 || decision.Length == 0) { leftOut.Add(new(where, "It has no title or no decision text.")); continue; }
            if (Assures(where, [proposed.Title, proposed.Context, proposed.Decision, .. proposed.Alternatives ?? []]) || About(where, proposed.About) is not { } about)
                continue;

            var settles = new List<Settles>();
            var ruleId = (proposed.SettlesRule ?? "").Trim();
            if (ruleId.Length > 0)
            {
                settles = [.. fired[ruleId].Where(f => about.Any(a => a.Id == f.Subject.Id)).Select(f => new Settles(f.RuleId, f.Subject.Id))];
                if (settles.Count == 0)
                {
                    leftOut.Add(new($"{where}.settlesRule", RuleSet.IsRule(ruleId)
                        ? $"The rule {ruleId} gave no concern for a part the proposal names, so the proposal settles nothing."
                        : $"'{DesignText.Line(ruleId, 40)}' is not a review rule, so the proposal settles nothing."));
                }
            }

            decisions.Add(new(
                title, DesignText.Lines(proposed.Context, TextRule.MaxDescription), decision,
                [.. DesignText.List(proposed.Alternatives, DecisionRules.MaxAlternatives).Select(Alternative)],
                settles, about));
        }

        return new CritiqueContent(questions, risks, decisions, leftOut);
    }

    /// <summary><c>&lt;option&gt;: &lt;why not&gt;</c> as an alternative. Text with no such form is the title alone.</summary>
    private static Alternative Alternative(string text)
    {
        var colon = text.IndexOf(": ", StringComparison.Ordinal);
        return colon is > 0 and <= TextRule.MaxName
            ? new Alternative(DesignText.Line(text[..colon]), Optional(text[(colon + 2)..]))
            : new Alternative(DesignText.Line(text), text.Length > TextRule.MaxName ? DesignText.Lines(text) : null);
    }

    private static string? Optional(string? text) => DesignText.Lines(text) is { Length: > 0 } clean ? clean : null;
}
