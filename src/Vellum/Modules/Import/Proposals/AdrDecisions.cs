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

    /// <returns>How many decisions were proposed or changed.</returns>
    public static async Task<int> ProposeAsync(IServiceProvider services, ScanEntity scan, IReadOnlyList<ExtractedFact> facts, CancellationToken ct)
    {
        var adrs = facts.Where(f => f.Kind == FactKind.Decision).OrderBy(f => f.Key, StringComparer.Ordinal).ToList();
        if (adrs.Count == 0)
            return 0;
        var db = services.GetRequiredService<AppDbContext>();
        var citationName = $"{scan.Host}/{scan.Repository}";
        var revision = scan.CommitSha!;
        var time = services.GetRequiredService<TimeProvider>();
        var written = 0;

        foreach (var fact in adrs)
        {
            if (Content(fact) is not { } content)
                continue;
            var path = fact.Evidence[0].Path;
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
                var current = (await DecisionReads.CurrentAsync(db, scan.ProjectId, ct)).FirstOrDefault(d => d.Id == link.DecisionId);
                if (current is not null && current.State.Status == DecisionStatus.Proposed && current.State.History.All(h => h.By == Actor))
                {
                    var changed = await DecisionEndpoints.DecideAsync(services.GetRequiredService<IEventStore>(), link.DecisionId,
                        state => DecisionDecider.Change(state, scan.ProjectId, content, Actor, now, source), ct);
                    if (changed is CommandResult<DecisionState>.Success)
                        decided = link.DecisionId;
                }
                else if (current is not null && current.State.Status == DecisionStatus.Accepted)
                {
                    content = content with { Supersedes = link.DecisionId };
                }
            }

            if (decided is null)
            {
                var (dto, _) = await DecisionEndpoints.CreateAsync(services, scan.ProjectId, content, DecisionOrigin.Imported, Actor, now, ct, source);
                if (dto is null)
                    continue;
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
            written++;
        }
        return written;
    }

    /// <summary>The content of the decision, or null when the ADR has no decision text.</summary>
    private static DecisionContent? Content(ExtractedFact fact)
    {
        var a = fact.Attributes;
        if (a.GetValueOrDefault("title") is not { Length: > 0 } title || a.GetValueOrDefault("decision") is not { Length: > 0 } decision)
            return null;
        return new DecisionContent(
            TextRule.Line("title", title) is null ? title : title[..Math.Min(title.Length, TextRule.MaxName)],
            a.GetValueOrDefault("context") ?? "", decision, [], a.GetValueOrDefault("consequences"), [], [], null);
    }

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
