using System.Security.Cryptography;
using System.Text;
using Deedbox;
using Microsoft.EntityFrameworkCore;
using Vellum.Modules.Import.Extraction;
using Vellum.Modules.Import.Scans;
using Vellum.Modules.Review;
using Vellum.Shared;

namespace Vellum.Modules.Import.Proposals;

/// <summary>
/// Turns the ADR facts of a tracking scan into proposed decisions of the project's review. No
/// AI is involved, and nothing is accepted: a decision stays <c>proposed</c> until a person
/// with the Owner role accepts it, as every other decision. Each decision cites its file and
/// lines and carries the label <c>declared</c> (<see cref="DecisionSource"/>).
/// <list type="bullet">
/// <item>An ADR that no decision was proposed from yet: a new proposed decision.</item>
/// <item>The same text as before: nothing.</item>
/// <item>Other text, and the decision is still proposed and only this import wrote it: the
/// decision's text changes to the new text.</item>
/// <item>Other text, and a person changed, accepted or rejected the decision: a new proposed
/// decision. When the earlier one is accepted the new one names it in <c>supersedes</c>, so
/// accepting it replaces it; nothing changes until a person accepts.</item>
/// </list>
/// A comparison scan (another ref, a commit, a pull request) proposes nothing: it is a preview.
/// An ADR that a later scan no longer finds leaves its decision as it is.
/// </summary>
internal static class AdrDecisions
{
    public const string Actor = "system:import-scan";

    /// <returns>A note for each ADR whose decision the project refused. Nothing is dropped without one.</returns>
    public static async Task<IReadOnlyList<ScanNote>> ProposeAsync(IServiceProvider services, ScanEntity scan, IReadOnlyList<ExtractedFact> facts, CancellationToken ct)
    {
        var adrs = facts.Where(f => f.Kind == FactKind.Decision).OrderBy(f => f.Key, StringComparer.Ordinal).ToList();
        var refused = new List<ScanNote>();
        if (adrs.Count == 0)
            return refused;
        var db = services.GetRequiredService<AppDbContext>();
        var citationName = $"{scan.Host}/{scan.Repository}";
        var revision = scan.CommitSha!;
        var time = services.GetRequiredService<TimeProvider>();

        foreach (var fact in adrs)
        {
            var path = fact.Evidence[0].Path;
            if (Content(fact) is not { } content)
            {
                refused.Add(ScanNotes.Of("adr-not-proposed", path, fact.Evidence[0].StartLine));
                continue;
            }
            var pathHash = KeyHash.Of(path);
            var hash = Hash(fact);
            var source = Source(fact, citationName, revision);
            var now = time.GetUtcNow();
            var link = await db.DecisionLinks.FirstOrDefaultAsync(
                l => l.ProjectId == scan.ProjectId && l.SourceId == scan.SourceId && l.PathHash == pathHash, ct);
            if (link is not null && link.ContentHash == hash)
                continue;

            Guid? decided = null;
            if (link is not null)
            {
                // Only a decision that is still proposed and that only this import wrote takes the
                // new text. The test runs on the state of the locked stream, so a person who edits,
                // accepts or rejects it at the same time is never overwritten: the import then
                // proposes a new decision instead.
                var changed = await DecisionEndpoints.DecideAsync(services.GetRequiredService<IEventStore>(), link.DecisionId,
                    state => state.Status == DecisionStatus.Proposed && state.History.All(h => h.By == Actor)
                        ? DecisionDecider.Change(state, scan.ProjectId, content, Actor, now, source)
                        : new CommandResult<IReadOnlyList<object>>.Conflict("A person wrote this decision."), ct);
                if (changed is CommandResult<DecisionState>.Success)
                    decided = link.DecisionId;
                else if ((await DecisionReads.CurrentAsync(db, scan.ProjectId, ct)).FirstOrDefault(d => d.Id == link.DecisionId)
                    is { State.Status: DecisionStatus.Accepted })
                    content = content with { Supersedes = link.DecisionId };
            }

            if (decided is null)
            {
                var (dto, _) = await DecisionEndpoints.CreateAsync(services, scan.ProjectId, content, DecisionOrigin.Imported, Actor, now, ct, source);
                if (dto is null)
                {
                    refused.Add(ScanNotes.Of("adr-not-proposed", path, fact.Evidence[0].StartLine));
                    continue;
                }
                decided = dto.Id;
            }

            if (link is null)
            {
                link = new DecisionLinkEntity { ProjectId = scan.ProjectId, SourceId = scan.SourceId, PathHash = pathHash, Path = path };
                db.DecisionLinks.Add(link);
            }
            link.DecisionId = decided.Value;
            link.ContentHash = hash;
            link.ScanId = scan.Id;
            link.UpdatedAt = now;
            await db.SaveChangesAsync(ct);
        }
        return refused;
    }

    /// <summary>
    /// The content of the decision, with every control character a decision cannot hold taken out
    /// (all of them in the title; all but tab and line breaks in the text) and the title cut to
    /// its bound. Null when the ADR has no title or no decision text left.
    /// </summary>
    private static DecisionContent? Content(ExtractedFact fact)
    {
        var a = fact.Attributes;
        var title = Clean(a.GetValueOrDefault("title"), multiline: false);
        var decision = Clean(a.GetValueOrDefault("decision"), multiline: true);
        if (title.Length == 0 || decision.Length == 0)
            return null;
        var consequences = Clean(a.GetValueOrDefault("consequences"), multiline: true);
        return new DecisionContent(
            title[..Math.Min(title.Length, TextRule.MaxName)], Clean(a.GetValueOrDefault("context"), multiline: true), decision, [],
            consequences.Length == 0 ? null : consequences, [], [], null);
    }

    private static string Clean(string? text, bool multiline) =>
        new string((text ?? "").Where(c => !char.IsControl(c) || (multiline && c is '\t' or '\n' or '\r')).ToArray()).Trim();

    private static DecisionSource Source(ExtractedFact fact, string citationName, string revision)
    {
        var evidence = fact.Evidence[0];
        string Cite(string lines) => $"{citationName}@{revision}:{evidence.Path}:{lines}";
        var sections = new List<DecisionSection>();
        foreach (var name in new[] { "title", "status", "context", "decision", "consequences" })
        {
            if (fact.Attributes.GetValueOrDefault("lines." + name) is { } lines)
                sections.Add(new DecisionSection(name, Cite(lines)));
        }
        return new DecisionSource(TruthLabels.Declared, Cite($"{evidence.StartLine}-{evidence.EndLine}"),
            fact.Attributes.GetValueOrDefault("status"), fact.Attributes.GetValueOrDefault("date"), sections);
    }

    /// <summary>What the file says: the decision's text and the status and date it records.</summary>
    private static string Hash(ExtractedFact fact)
    {
        var text = new StringBuilder();
        foreach (var name in new[] { "title", "status", "date", "context", "decision", "consequences" })
        {
            var value = fact.Attributes.GetValueOrDefault(name) ?? "";
            text.Append(name).Append(':').Append(value.Length).Append(':').Append(value).Append('\n');
        }
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }
}
