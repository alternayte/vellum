using Deedbox;
using Microsoft.EntityFrameworkCore;
using Vellum.Modules.Import.Extraction;
using Vellum.Modules.Import.Proposals;
using Vellum.Modules.Modelling;
using Vellum.Shared;

namespace Vellum.Modules.Import.Sources;

/// <summary>
/// A dependency that one Source declares and another Source does not confirm: the other Source
/// observes or measures the element the dependency starts at, and has no such edge from it.
/// It is a disagreement to show, not a refusal: the declared dependency can still be accepted.
/// </summary>
/// <param name="RelationshipId">The relationship of the model, when the declared edge was accepted. Null when it is still a proposal.</param>
/// <param name="Citation"><c>&lt;source&gt;@&lt;revision&gt;:path:start-end</c> of the declaration.</param>
/// <param name="UnconfirmedLabel">The truth label the other Source gives the start element: <c>observed-in-source</c> or <c>measured</c>.</param>
public sealed record SourceDisagreementDto(
    Guid? RelationshipId, Guid FromId, string FromName, Guid ToId, string ToName, string? Label,
    Guid DeclaredBySourceId, string DeclaredBy, string? Citation,
    Guid UnconfirmedBySourceId, string UnconfirmedBy, string UnconfirmedLabel);

/// <summary>
/// Finds the dependencies of <see cref="SourceDisagreementDto"/> in a project. It reads the
/// newest Snapshot of each Source and maps its fact keys to elements through the Source's
/// accepted facts and its source bindings, so a dependency counts whether or not its
/// relationship was accepted. An edge whose end has no element yet in the other Source is
/// compared by the normalised name of that end.
/// </summary>
internal static class SourceDisagreements
{
    public const int Max = 200;

    private sealed record Edge(Guid From, Guid? To, string ToName, string Label, string? Citation, string? Kind);

    private sealed record SourceView(
        SourceEntity Source, Dictionary<Guid, string> Holds, List<Edge> Edges);

    public static async Task<List<SourceDisagreementDto>> ComputeAsync(
        IServiceProvider services, AppDbContext db, Guid projectId, IReadOnlyList<SourceEntity> sources, CancellationToken ct)
    {
        if (sources.Count < 2)
            return [];
        var streamId = await db.Projects.AsNoTracking().Where(p => p.Id == projectId).Select(p => p.StreamId).FirstAsync(ct);
        var (model, _) = await services.GetRequiredService<IEventStore>().ForProject(projectId).Load<ModelState>(StreamId.From(streamId), ct);
        var links = await db.FactLinks.AsNoTracking()
            .Where(l => l.ProjectId == projectId && l.EntityKind == StoredEntityKind.Element)
            .Select(l => new { l.Host, l.Repository, l.Subject, l.FactKey, l.EntityId }).ToListAsync(ct);

        var views = new List<SourceView>();
        foreach (var source in sources)
        {
            var snapshot = await db.ImportSnapshots.AsNoTracking().Where(s => s.SourceId == source.Id)
                .OrderByDescending(s => s.CreatedAt).ThenByDescending(s => s.ScanId).FirstOrDefaultAsync(ct);
            if (snapshot is null)
                continue;
            var facts = await db.SnapshotFacts.AsNoTracking().Where(f => f.ScanId == snapshot.ScanId)
                .Select(f => new { f.Kind, f.FactKey, f.Name, f.Attributes, f.Evidence }).ToListAsync(ct);

            // The elements this Source holds, by its fact keys.
            var byKey = new Dictionary<string, Guid>(StringComparer.Ordinal);
            foreach (var link in links.Where(l => l.Host == source.Host && l.Repository == source.Locator))
                byKey.TryAdd(FactMapping.KeyOf(link.Subject, link.FactKey), link.EntityId);
            foreach (var element in model.Elements.Values)
            {
                foreach (var binding in element.Bindings.Where(b => b.SourceId == source.Id))
                    byKey.TryAdd(binding.ExternalKey == FactMapping.ExternalKey(FactMapping.RepositoryKey) ? FactMapping.RepositoryKey : binding.ExternalKey, element.Id);
            }

            var names = facts.GroupBy(f => f.FactKey, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().Name, StringComparer.Ordinal);
            var holds = new Dictionary<Guid, string>();
            foreach (var fact in facts.Where(f => f.Kind == FactKind.Element || f.Kind == FactKind.Service || f.Kind == FactKind.DataStore))
            {
                if (byKey.TryGetValue(fact.FactKey, out var elementId) && model.Elements.ContainsKey(elementId))
                    holds.TryAdd(elementId, Label(fact.Attributes, fact.Evidence));
            }

            var edges = new List<Edge>();
            foreach (var fact in facts.Where(f => f.Kind == FactKind.Relationship || f.Kind == FactKind.Interaction))
            {
                var attributes = ProposalJson.ReadAttributes(fact.Attributes);
                if (!attributes.TryGetValue("from", out var fromKey) || !attributes.TryGetValue("to", out var toKey)
                    || !byKey.TryGetValue(fromKey, out var from))
                    continue;
                Guid? to = byKey.TryGetValue(toKey, out var toId) ? toId : null;
                var evidence = ProposalJson.ReadEvidence(fact.Evidence).FirstOrDefault();
                edges.Add(new Edge(from, to, ModelIndex.NormalName(names.GetValueOrDefault(toKey) ?? toKey),
                    Label(fact.Attributes, fact.Evidence),
                    evidence is null ? null : EvidenceLinks.Pointer(source.CitationName, snapshot.CommitSha, evidence),
                    attributes.GetValueOrDefault("label")));
            }
            views.Add(new SourceView(source, holds, edges));
        }

        var found = new List<SourceDisagreementDto>();
        foreach (var declaring in views)
        {
            foreach (var edge in declaring.Edges.Where(e => e.Label == "declared" && e.To is not null))
            {
                if (!model.Elements.TryGetValue(edge.From, out var fromElement) || !model.Elements.TryGetValue(edge.To!.Value, out var toElement))
                    continue;
                var toName = ModelIndex.NormalName(toElement.Name);
                foreach (var other in views.Where(v => v.Source.Id != declaring.Source.Id))
                {
                    if (!other.Holds.TryGetValue(edge.From, out var label) || label is not ("observed-in-source" or "measured"))
                        continue;
                    var confirmed = other.Edges.Any(e => e.From == edge.From && (e.To == edge.To || (e.To is null && e.ToName == toName)));
                    if (confirmed)
                        continue;
                    var relationship = model.Relationships.Values.FirstOrDefault(r => r.FromId.Value == edge.From && r.ToId.Value == edge.To!.Value);
                    found.Add(new SourceDisagreementDto(
                        relationship?.Id.Value, edge.From, fromElement.Name, edge.To.Value, toElement.Name, edge.Kind,
                        declaring.Source.Id, declaring.Source.Name, edge.Citation,
                        other.Source.Id, other.Source.Name, label));
                    if (found.Count == Max)
                        return found;
                }
            }
        }
        return found;
    }

    private static string Label(string attributes, string evidence) =>
        FactMapping.LabelOf(ProposalJson.ReadAttributes(attributes), ProposalJson.ReadEvidence(evidence));
}
