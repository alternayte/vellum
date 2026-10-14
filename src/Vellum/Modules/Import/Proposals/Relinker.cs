namespace Vellum.Modules.Import.Proposals;

/// <param name="Link">The link as stored, under its old key.</param>
/// <param name="FactKey">The key it moves to.</param>
internal sealed record Relink(LinkInfo Link, string FactKey);

/// <param name="Options">The candidates the rule found, when it found more than one or none exactly.</param>
internal sealed record UnresolvedLink(LinkInfo Link, IReadOnlyList<Candidate> Options);

/// <param name="Links">The links the comparison may use, by fact key: those with the current rules, and the moved ones under their new key.</param>
/// <param name="Held">Candidates that an unresolved link may be. No <c>add</c> is proposed for them until a person decides.</param>
internal sealed record RelinkResult(
    IReadOnlyDictionary<string, LinkInfo> Links,
    IReadOnlyList<Relink> Moved,
    IReadOnlyList<UnresolvedLink> Unresolved,
    IReadOnlySet<string> Held);

/// <summary>
/// A fact key means one thing under one set of extraction rules. When the rules of a snapshot
/// are not the rules a link was accepted with, the link's key may no longer exist although
/// the thing it names does, so a missing key proves nothing. This pass runs first and finds,
/// for every such link, the fact of the snapshot that is the same thing.
/// <para>The rule is exact. A link and a candidate fact are the same thing when they have the
/// same subject and the same entity kind, and one of these holds:</para>
/// <list type="number">
/// <item>They share evidence: the same path and the same rule ID, with line ranges that overlap.</item>
/// <item>The candidate has the match key of the link's entity (<see cref="ModelIndex.MatchKey"/>):
/// kind and normalised name for an element, owner and method with route template for an
/// operation, normalised name for a channel, a message or a schema, channel and consumer
/// for a subscription. The owner, channel and consumer are entities, so "within the same
/// service" is part of the key. This rule is asked only for a link that shares evidence
/// with no candidate.</item>
/// </list>
/// <para>A link moves only when the rule names one candidate for the link and one link for
/// the candidate. The link of the repository itself moves to the repository's candidate.
/// Everything else is unresolved: it is left exactly as it is and shown to a person.</para>
/// </summary>
internal static class Relinker
{
    public static RelinkResult Run(
        IReadOnlyList<Candidate> candidates, IReadOnlyList<LinkInfo> links, string ruleFingerprint, ModelIndex model)
    {
        var effective = new Dictionary<string, LinkInfo>(StringComparer.Ordinal);
        var outdated = new List<LinkInfo>();
        foreach (var link in links)
        {
            if (link.RuleFingerprint == ruleFingerprint)
                effective[link.FactKey] = link;
            else
                outdated.Add(link);
        }
        if (outdated.Count == 0)
            return new RelinkResult(effective, [], [], new HashSet<string>());

        var moved = new List<Relink>();
        var resolved = new HashSet<Guid>();
        var options = new Dictionary<Guid, List<Candidate>>();

        // The repository is the repository under any rules.
        if (outdated.FirstOrDefault(l => l.FactKey == FactMapping.RepositoryKey) is { } repositoryLink)
        {
            Move(repositoryLink, FactMapping.RepositoryKey);
            resolved.Add(repositoryLink.LinkId);
        }

        var byEvidence = new Dictionary<(string Kind, string Path, string Rule), List<LinkInfo>>();
        var byMatchKey = new Dictionary<(string Kind, string MatchKey), List<LinkInfo>>();
        foreach (var link in outdated.Where(l => l.FactKey != FactMapping.RepositoryKey))
        {
            foreach (var key in link.Evidence.Select(e => (link.EntityKind, e.Path, e.Rule)).Distinct())
                Bucket(byEvidence, key).Add(link);
            if (model.Find(link.EntityKind, link.EntityId) is { } entity)
                Bucket(byMatchKey, (link.EntityKind, model.MatchKeyOf(entity))).Add(link);
        }

        // Rank by rank: a later rank's match key holds the entity of an earlier rank's link.
        foreach (var rank in candidates.Where(c => c.FactKey != FactMapping.RepositoryKey).GroupBy(c => c.Rank).OrderBy(g => g.Key))
        {
            // Shared evidence first. The match key is asked only for a link that shares
            // evidence with no fact: a name can fit several facts, a line of a file cannot.
            var sharesEvidence = new HashSet<Guid>();
            for (var phase = 1; phase <= 2; phase++)
            {
                var pairs = new Dictionary<string, HashSet<Guid>>(StringComparer.Ordinal);
                var reverse = new Dictionary<Guid, List<Candidate>>();
                var byId = new Dictionary<Guid, LinkInfo>();
                foreach (var candidate in rank)
                {
                    // A fact that a link with the current rules holds is not free to take.
                    if (effective.TryGetValue(candidate.FactKey, out var holder) && holder.EntityKind == candidate.EntityKind)
                        continue;

                    var found = new HashSet<Guid>();
                    foreach (var evidence in phase == 1 ? candidate.Evidence : [])
                    {
                        if (!byEvidence.TryGetValue((candidate.EntityKind, evidence.Path, evidence.Rule), out var sharing))
                            continue;
                        foreach (var link in sharing)
                        {
                            if (!resolved.Contains(link.LinkId) && link.Evidence.Any(e =>
                                    e.Path == evidence.Path && e.Rule == evidence.Rule
                                    && e.StartLine <= evidence.EndLine && evidence.StartLine <= e.EndLine))
                            {
                                found.Add(link.LinkId);
                                byId[link.LinkId] = link;
                            }
                        }
                    }

                    if (phase == 2 && byMatchKey.TryGetValue((candidate.EntityKind, MatchKey(candidate, effective)), out var sameKey))
                    {
                        foreach (var link in sameKey.Where(l => !resolved.Contains(l.LinkId) && !sharesEvidence.Contains(l.LinkId)))
                        {
                            found.Add(link.LinkId);
                            byId[link.LinkId] = link;
                        }
                    }

                    if (found.Count == 0)
                        continue;
                    pairs[candidate.FactKey] = found;
                    foreach (var linkId in found)
                        Bucket(reverse, linkId).Add(candidate);
                }

                foreach (var (linkId, matching) in reverse)
                {
                    if (matching.Count == 1 && pairs[matching[0].FactKey].Count == 1)
                    {
                        Move(byId[linkId], matching[0].FactKey);
                        resolved.Add(linkId);
                    }
                    else
                        Bucket(options, linkId).AddRange(matching);
                }
                sharesEvidence.UnionWith(reverse.Keys);
            }
        }

        var unresolved = new List<UnresolvedLink>();
        var held = new HashSet<string>(StringComparer.Ordinal);
        var sameKeyCandidates = candidates.ToLookup(c => c.FactKey, StringComparer.Ordinal);
        foreach (var link in outdated.Where(l => !resolved.Contains(l.LinkId)).OrderBy(l => l.FactKey, StringComparer.Ordinal))
        {
            var choices = options.TryGetValue(link.LinkId, out var found) ? found : [];
            // A candidate with the link's old key and kind is offered too. The key alone moves nothing.
            foreach (var candidate in sameKeyCandidates[link.FactKey])
            {
                if (candidate.EntityKind == link.EntityKind && !choices.Contains(candidate)
                    && !(effective.TryGetValue(candidate.FactKey, out var holder) && holder.EntityKind == candidate.EntityKind))
                    choices.Add(candidate);
            }
            choices = [.. choices.DistinctBy(c => c.FactKey).OrderBy(c => c.FactKey, StringComparer.Ordinal)];
            unresolved.Add(new UnresolvedLink(link, choices));
            held.UnionWith(choices.Select(c => c.FactKey));
        }

        return new RelinkResult(effective, moved, unresolved, held);

        void Move(LinkInfo link, string factKey)
        {
            moved.Add(new Relink(link, factKey));
            effective[factKey] = link with { FactKey = factKey, RuleFingerprint = ruleFingerprint };
        }
    }

    /// <summary>
    /// The match key of a candidate, with each reference as the entity of the link that holds
    /// the referenced fact. A reference that no link holds stays empty, and the key then
    /// equals no entity's key.
    /// </summary>
    private static string MatchKey(Candidate candidate, Dictionary<string, LinkInfo> links)
    {
        var values = new Dictionary<string, string?>(candidate.Fields, StringComparer.Ordinal);
        foreach (var reference in candidate.References)
        {
            if (reference.FactKey is not null && links.TryGetValue(reference.FactKey, out var target))
                values[reference.Field] = target.EntityId.ToString();
        }
        if (candidate.MatchRoute is not null)
            values[Field.Name] = candidate.MatchRoute;
        return ModelIndex.MatchKey(candidate.EntityKind, values);
    }

    private static List<TValue> Bucket<TKey, TValue>(Dictionary<TKey, List<TValue>> buckets, TKey key) where TKey : notnull
    {
        if (!buckets.TryGetValue(key, out var bucket))
            buckets[key] = bucket = [];
        return bucket;
    }
}
