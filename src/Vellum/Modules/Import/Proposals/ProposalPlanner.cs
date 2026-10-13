using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Vellum.Modules.Import.Extraction;
using Vellum.Modules.Import.Scans;

namespace Vellum.Modules.Import.Proposals;

/// <summary>An accepted fact, as the planner reads it from <c>import.fact_links</c>.</summary>
/// <param name="FactKey">The in-memory key (<see cref="FactMapping.KeyOf"/>).</param>
/// <param name="RuleFingerprint">Null when the rules the key was made with are not known.</param>
internal sealed record LinkInfo(
    Guid LinkId, string FactKey, string EntityKind, Guid EntityId, string CommitSha, string? RuleFingerprint,
    IReadOnlyDictionary<string, string> Attributes,
    IReadOnlyList<string> ConsumerKeys,
    IReadOnlyDictionary<string, string?> Accepted,
    IReadOnlyList<EvidenceRef> Evidence);

/// <param name="Repository">With the host, for example <c>github.com/acme/orders</c>.</param>
/// <param name="RuleFingerprint">The fingerprint of the rules that made <paramref name="Facts"/>.</param>
/// <param name="Links">The accepted facts of this repository in this project.</param>
/// <param name="TakenEntityIds">Entities that a fact of any repository is linked to. A fact never matches one of them by name.</param>
/// <param name="Rejections">Fact key and fingerprint of every proposal a person rejected.</param>
/// <param name="Coverage">Where the scan looked. A missing fact is judged only through it.</param>
internal sealed record PlanInput(
    string Repository, string CommitSha, string RuleFingerprint,
    IReadOnlyList<ExtractedFact> Facts, ModelIndex Model,
    IReadOnlyList<LinkInfo> Links,
    IReadOnlySet<Guid> TakenEntityIds,
    IReadOnlySet<(string FactKey, string Fingerprint)> Rejections,
    ScanCoverage Coverage);

internal sealed record FieldChange(string Field, string? From, string? To);

internal sealed record PlannedProposal(
    Guid Id, string Action, string EntityKind, Guid EntityId, string FactKey, string Name,
    JsonObject? Proposed, JsonObject? Current, IReadOnlyList<FieldChange> Changes,
    IReadOnlyList<EvidenceRef> Evidence, IReadOnlyDictionary<string, string> Attributes,
    IReadOnlyList<string> ConsumerKeys, IReadOnlyList<Guid> DependsOn, string Fingerprint);

/// <param name="MarkStale">Entities whose evidence a scan that looked for it no longer finds.</param>
/// <param name="ClearStale">Entities with a stale mark whose evidence this scan found.</param>
/// <param name="Relinked">Links to move to a new key. Nothing of the model changes with them.</param>
/// <param name="NeedsReview">Links that the exact rule could not move. Each has a <c>relink</c> proposal.</param>
/// <param name="Coverage">How many links the scan could judge, and why not the others.</param>
/// <param name="Refused">Facts that were not kept (<see cref="FactMapping.Refusal"/>).</param>
internal sealed record Plan(
    IReadOnlyList<PlannedProposal> Proposals, IReadOnlyList<MappedFact> Facts,
    IReadOnlyList<Guid> MarkStale, IReadOnlyList<Guid> ClearStale,
    IReadOnlyList<Relink> Relinked, int NeedsReview, LinkCoverage Coverage,
    IReadOnlyList<ExtractedFact> Refused);

/// <summary>
/// Compares the facts of one snapshot with the project's main model and with what was
/// accepted before, and says what a person could accept. It is a pure function: it reads its
/// input and writes nothing, so it cannot change the model. Every lookup is a dictionary
/// read on a key computed once, so the work grows with the number of facts and entities and
/// not with their product.
/// <list type="bullet">
/// <item>First, links made with other extraction rules are moved to their new keys by
/// <see cref="Relinker"/>. A link it cannot move is left alone and gets a <c>relink</c>
/// proposal; nothing else is concluded about it.</item>
/// <item>A fact with no linked and no matching entity: <c>add</c>.</item>
/// <item>A linked entity whose observed values differ: <c>change</c>, or <c>conflict</c> when
/// the entity is not <c>observed-in-source</c> or a person edited it after it was accepted.</item>
/// <item>An unlinked fact that matches exactly one hand-made entity by
/// <see cref="ModelIndex.MatchKey"/>: a <c>change</c> or <c>conflict</c> that would link them.
/// Two or more matches are not a match.</item>
/// <item>A linked entity whose fact is missing, when the scan looked at every path of the
/// evidence the link was accepted with (<see cref="ScanCoverage"/>): <c>remove</c>, and a
/// stale mark when the entity is <c>observed-in-source</c>. When the scan did not look, the
/// fact is "not covered": no removal, no mark, and an existing mark stays.</item>
/// <item>A proposal that a person rejected is not made again while it says the same thing.</item>
/// </list>
/// </summary>
internal static class ProposalPlanner
{
    public const string ObservedInSource = "observed-in-source";
    public const string RelinkPending = "relink-pending";

    /// <summary>The fact matches an entity whose accepted fact this scan did not look at. It may be that fact, seen with less of the repository.</summary>
    public const string AcceptedNotCovered = "accepted-not-covered";
    private const int MaxChangeText = 2000;

    private sealed record Resolution(Guid? EntityId, Guid? AddProposalId, string? Name, string? Blocked);

    private sealed record Release(Guid ProposalId, IReadOnlyDictionary<string, string?> Before, IReadOnlyDictionary<string, string?> After);

    public static Plan Compute(PlanInput input, Func<Guid> newId)
    {
        var mapping = FactMapping.Map(input.Repository, input.Facts);
        // A fact that was not kept leaves its files looked at only in part.
        foreach (var refused in mapping.Refused)
        {
            foreach (var evidence in refused.Evidence)
                input.Coverage.Exclude(evidence.Path, CoverageGap.FactRefused);
        }

        var facts = mapping.Facts.ToList();
        var mappedAt = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < facts.Count; i++)
        {
            if (facts[i].Disposition == FactDisposition.Mapped)
                mappedAt.TryAdd(facts[i].Fact.Key, i);
        }

        var relinked = Relinker.Run(mapping.Candidates, input.Links, input.RuleFingerprint, input.Model);
        var links = relinked.Links;

        var proposals = new List<PlannedProposal>();
        var resolved = new Dictionary<string, Resolution>(StringComparer.Ordinal);
        var claimed = new HashSet<Guid>();
        var markStale = new List<Guid>();
        var clearStale = new List<Guid>();
        // Change proposals with the entity's values before and after: a change can be what
        // stops pointing at an entity that another proposal removes.
        var releases = new List<Release>();

        // The entities whose accepted fact is absent from this scan and was not looked at.
        // A scan of some folders gives a fact another key when a type it names is outside
        // them, so a fact that matches such an entity is not a new thing.
        var presentKeys = mapping.Candidates.Select(c => c.FactKey).ToHashSet(StringComparer.Ordinal);
        var notJudged = links.Values
            .Where(l => !presentKeys.Contains(l.FactKey) && input.Coverage.GapFor(l.Evidence) is not null)
            .Select(l => (l.EntityKind, l.EntityId))
            .ToHashSet();

        foreach (var candidate in mapping.Candidates)
        {
            // An accepted fact may be this one, and a person has not said yet. Until then
            // nothing is proposed for it, so the model gets no second copy.
            if (relinked.Held.Contains(candidate.FactKey))
            {
                resolved[candidate.FactKey] = new Resolution(null, null, null, "dependency-unavailable");
                Reclassify(candidate.FactKey, FactDisposition.Blocked, RelinkPending);
                continue;
            }

            // 1. The values the source establishes, with each reference as an entity ID.
            var values = new SortedDictionary<string, string?>(candidate.Fields, StringComparer.Ordinal);
            var names = new Dictionary<string, string?>(StringComparer.Ordinal);
            var dependsOn = new List<Guid>();
            string? blocked = null;
            foreach (var reference in candidate.References)
            {
                if (reference.FactKey is null)
                    continue;
                if (!resolved.TryGetValue(reference.FactKey, out var target) || target.EntityId is null)
                {
                    if (reference.RequiredToAdd)
                        blocked ??= target?.Blocked ?? "dependency-unavailable";
                    continue;
                }
                values[reference.Field] = target.EntityId.Value.ToString();
                names[reference.Field] = target.Name;
                if (target.AddProposalId is { } needed)
                    dependsOn.Add(needed);
            }

            var consumerIds = new List<Guid>();
            foreach (var key in candidate.ConsumerKeys)
            {
                if (!resolved.TryGetValue(key, out var consumer) || consumer.EntityId is null)
                    continue;
                consumerIds.Add(consumer.EntityId.Value);
                if (consumer.AddProposalId is { } needed)
                    dependsOn.Add(needed);
            }

            // 2. The entity the fact belongs to: the linked one, else the one exact match.
            var link = links.GetValueOrDefault(candidate.FactKey);
            var entity = link is not null && link.EntityKind == candidate.EntityKind
                ? input.Model.Find(candidate.EntityKind, link.EntityId)
                : null;
            var linked = entity is not null;
            if (entity is null && blocked is null)
            {
                // An operation is matched on its route, which its attributes state more exactly than its name.
                var matched = candidate.MatchRoute is null
                    ? values
                    : new SortedDictionary<string, string?>(values, StringComparer.Ordinal) { [Field.Name] = candidate.MatchRoute };
                ModelEntity? only = null;
                var count = 0;
                foreach (var match in input.Model.Matching(candidate.EntityKind, ModelIndex.MatchKey(candidate.EntityKind, matched)))
                {
                    if (notJudged.Contains((candidate.EntityKind, match.Id)))
                        blocked ??= AcceptedNotCovered;
                    if (input.TakenEntityIds.Contains(match.Id) || claimed.Contains(match.Id))
                        continue;
                    only = match;
                    if (++count == 2)
                        break;
                }
                if (count == 1)
                    entity = only;
            }

            if (entity is null)
            {
                if (blocked is not null || candidate.NotAddable is not null)
                {
                    resolved[candidate.FactKey] = new Resolution(null, null, null, "dependency-unavailable");
                    Reclassify(candidate.FactKey,
                        blocked is not null ? FactDisposition.Blocked : FactDisposition.EvidenceOnly,
                        blocked ?? candidate.NotAddable);
                    continue;
                }

                if (candidate.EntityKind == EntityKinds.Message)
                    values[Field.ConsumerIds] = ModelIndex.JoinIds(consumerIds);

                var addFingerprint = Fingerprint(ProposalAction.Add, candidate, null);
                if (input.Rejections.Contains((candidate.FactKey, addFingerprint)))
                {
                    resolved[candidate.FactKey] = new Resolution(null, null, null, "dependency-rejected");
                    continue;
                }

                var entityId = newId();
                var proposalId = newId();
                proposals.Add(new PlannedProposal(
                    proposalId, ProposalAction.Add, candidate.EntityKind, entityId, candidate.FactKey, candidate.Name,
                    ToJson(values, names, candidate.Observed), null, [], candidate.Evidence, candidate.Attributes,
                    candidate.ConsumerKeys, [.. dependsOn.Distinct()], addFingerprint));
                resolved[candidate.FactKey] = new Resolution(entityId, proposalId, candidate.Name, null);
                continue;
            }

            // The fact is present, so whatever follows, later facts can point at its entity.
            resolved[candidate.FactKey] = new Resolution(entity.Id, null, entity.Name, null);
            claimed.Add(entity.Id);
            // The scan read the files of this fact and found it: the evidence is there.
            if (linked && entity.Stale is not null)
                clearStale.Add(entity.Id);
            if (blocked is not null)
                continue;

            if (candidate.EntityKind == EntityKinds.Message)
            {
                // A scan adds a consumer it finds. It takes away only a consumer that an
                // accepted scan observed and that this scan no longer finds after it looked
                // at every path the message was accepted with; a consumer that a person
                // declared has no subscription in the source to be missing.
                var gone = new HashSet<Guid>();
                if (linked && link!.ConsumerKeys.Count > 0 && input.Coverage.GapFor(link.Evidence) is null)
                {
                    foreach (var key in link.ConsumerKeys.Except(candidate.ConsumerKeys, StringComparer.Ordinal))
                    {
                        if (links.GetValueOrDefault(key) is { } consumer)
                            gone.Add(consumer.EntityId);
                    }
                }
                var union = ModelIndex.SplitIds(entity.Values.GetValueOrDefault(Field.ConsumerIds))
                    .Where(id => !gone.Contains(id)).Concat(consumerIds);
                values[Field.ConsumerIds] = ModelIndex.JoinIds(union);
            }

            var compared = ModelIndex.ComparedFields(candidate.EntityKind);
            var changes = new List<FieldChange>();
            foreach (var field in compared)
            {
                // A null value is one the source does not establish. It proposes nothing.
                if (values.GetValueOrDefault(field) is not { } proposed)
                    continue;
                var current = entity.Values.GetValueOrDefault(field);
                if (!string.Equals(proposed, current, StringComparison.Ordinal))
                    changes.Add(new FieldChange(field, current, proposed));
            }
            var modelChanges = changes.Count;

            var acceptedObserved = new SortedDictionary<string, string>(StringComparer.Ordinal);
            if (linked)
            {
                foreach (var (attribute, value) in candidate.Observed)
                {
                    var before = link!.Attributes.GetValueOrDefault(attribute);
                    if (before is not null)
                        acceptedObserved[attribute] = before;
                    if (!string.Equals(before, value, StringComparison.Ordinal))
                        changes.Add(new FieldChange("observed." + attribute, before, value));
                }
                foreach (var (attribute, before) in link!.Attributes.OrderBy(a => a.Key, StringComparer.Ordinal))
                {
                    if (candidate.Attributes.ContainsKey(attribute))
                        continue;
                    acceptedObserved[attribute] = before;
                    changes.Add(new FieldChange("observed." + attribute, before, null));
                }
            }

            if (linked && changes.Count == 0)
                continue;

            // A schema has no truth label of its own. A linked schema came from an import; an
            // unlinked one was written by a person.
            var status = entity.FactStatus ?? (linked ? ObservedInSource : "declared");
            var editedSince = linked && compared.Any(field =>
                link!.Accepted.TryGetValue(field, out var accepted)
                && !string.Equals(accepted, entity.Values.GetValueOrDefault(field), StringComparison.Ordinal));
            var action = modelChanges > 0 && (status != ObservedInSource || editedSince)
                ? ProposalAction.Conflict
                : ProposalAction.Change;

            // Accepting a link gives the entity the label of the source. That is a change of
            // the entity even when every value already agrees, so it is shown as one.
            if (entity.FactStatus is { } label && label != ObservedInSource)
                changes.Add(new FieldChange("factStatus", label, ObservedInSource));

            var fingerprint = Fingerprint(action, candidate, entity.Values);
            if (input.Rejections.Contains((candidate.FactKey, fingerprint)))
                continue;

            var currentNames = ReferenceNames(input.Model, entity.Values);
            var currentJson = ToJson(entity.Values, currentNames, acceptedObserved);
            if (entity.FactStatus is not null)
                currentJson["factStatus"] = entity.FactStatus;

            // What accepting would write: the entity as it is, with the changed values.
            var after = new SortedDictionary<string, string?>(entity.Values, StringComparer.Ordinal);
            var afterNames = new Dictionary<string, string?>(currentNames, StringComparer.Ordinal);
            foreach (var change in changes.Take(modelChanges))
            {
                after[change.Field] = change.To;
                if (names.TryGetValue(change.Field, out var name))
                    afterNames[change.Field] = name;
            }

            var changeId = newId();
            releases.Add(new Release(changeId, entity.Values, after));
            proposals.Add(new PlannedProposal(
                changeId, action, candidate.EntityKind, entity.Id, candidate.FactKey, candidate.Name,
                ToJson(after, afterNames, candidate.Observed), currentJson,
                [.. changes.Select(Bounded)], candidate.Evidence, candidate.Attributes,
                candidate.ConsumerKeys, [.. dependsOn.Distinct()], fingerprint));
        }

        var coverage = PlanRemovals(input, mapping, links, proposals, releases, markStale, newId);
        PlanReviews(input, relinked.Unresolved, proposals, newId);

        return new Plan(proposals, facts, markStale, clearStale, relinked.Moved, relinked.Unresolved.Count, coverage, mapping.Refused);

        void Reclassify(string factKey, string disposition, string? reason)
        {
            if (mappedAt.TryGetValue(factKey, out var index))
                facts[index] = facts[index] with { Disposition = disposition, Reason = reason };
        }
    }

    /// <summary>
    /// A link whose fact is missing is judged only when the scan looked at every path of the
    /// evidence the link was accepted with. Then the evidence is gone: the entity gets a
    /// stale mark, which deletes nothing, and a person may accept its removal. When the scan
    /// did not look, the link is counted as not covered and nothing else happens to it.
    /// </summary>
    private static LinkCoverage PlanRemovals(
        PlanInput input, MappingResult mapping, IReadOnlyDictionary<string, LinkInfo> links,
        List<PlannedProposal> proposals, List<Release> releases, List<Guid> markStale, Func<Guid> newId)
    {
        var present = input.Facts.Select(f => f.Key).Concat(mapping.Candidates.Select(c => c.FactKey))
            .ToHashSet(StringComparer.Ordinal);
        present.ExceptWith(mapping.Refused.Select(f => f.Key));

        var gaps = new Dictionary<string, int>(StringComparer.Ordinal);
        var covered = 0;
        var removals = new List<(PlannedProposal Proposal, ModelEntity Entity)>();
        foreach (var link in links.Values.OrderBy(l => l.FactKey, StringComparer.Ordinal))
        {
            if (present.Contains(link.FactKey))
            {
                covered++;
                continue;
            }
            if (input.Coverage.GapFor(link.Evidence) is { } gap)
            {
                gaps[gap] = gaps.GetValueOrDefault(gap) + 1;
                continue;
            }
            covered++;
            if (input.Model.Find(link.EntityKind, link.EntityId) is not { } entity)
                continue;

            // A fact that a person declared, or took over by changing its label, rests on
            // their word and not on the evidence. A scan leaves no mark on it.
            if (entity.FactStatus == ObservedInSource && entity.Stale is null)
                markStale.Add(entity.Id);

            var fingerprint = Hash([ProposalAction.Remove, link.EntityKind, link.FactKey, Canonical(entity.Values)]);
            if (input.Rejections.Contains((link.FactKey, fingerprint)))
                continue;

            var current = ToJson(entity.Values, ReferenceNames(input.Model, entity.Values), Sorted(link.Attributes));
            if (entity.FactStatus is not null)
                current["factStatus"] = entity.FactStatus;

            // The evidence of a removal is the evidence the fact was accepted with, at the
            // commit it was accepted from. It is what the new commit no longer has.
            var evidence = link.Evidence.Select(e => e with { CommitSha = e.CommitSha ?? link.CommitSha }).ToList();
            removals.Add((new PlannedProposal(
                newId(), ProposalAction.Remove, link.EntityKind, entity.Id, link.FactKey, entity.Name,
                null, current, [], evidence, link.Attributes, link.ConsumerKeys, [], fingerprint), entity));
        }

        // An entity is removed after the entities that point at it are removed, and after
        // the changes that make an entity stop pointing at it.
        var first = new Dictionary<Guid, List<Guid>>();
        foreach (var (proposal, entity) in removals)
        {
            foreach (var target in Targets(entity.Values))
            {
                if (target != entity.Id)
                    Bucket(first, target).Add(proposal.Id);
            }
        }
        foreach (var release in releases)
        {
            var after = Targets(release.After).ToHashSet();
            foreach (var target in Targets(release.Before).Where(t => !after.Contains(t)).Distinct())
                Bucket(first, target).Add(release.ProposalId);
        }
        foreach (var (proposal, entity) in removals)
            proposals.Add(proposal with { DependsOn = first.TryGetValue(entity.Id, out var needed) ? [.. needed.Distinct()] : [] });

        return new LinkCoverage(covered,
            gaps.GetValueOrDefault(CoverageGap.Scope), gaps.GetValueOrDefault(CoverageGap.Limit),
            gaps.GetValueOrDefault(CoverageGap.Withheld), gaps.GetValueOrDefault(CoverageGap.Failed));
    }

    /// <summary>
    /// One <c>relink</c> proposal for each link that <see cref="Relinker"/> could not move.
    /// It lists the facts the link may be. Accepting it names one of them, or none.
    /// </summary>
    private static void PlanReviews(
        PlanInput input, IReadOnlyList<UnresolvedLink> unresolved, List<PlannedProposal> proposals, Func<Guid> newId)
    {
        foreach (var (link, options) in unresolved)
        {
            var entity = input.Model.Find(link.EntityKind, link.EntityId);
            var candidates = new JsonArray();
            foreach (var option in options)
            {
                candidates.Add(new JsonObject
                {
                    ["factKey"] = option.FactKey,
                    ["name"] = option.Name,
                    ["evidence"] = JsonSerializer.SerializeToNode(option.Evidence, JsonSerializerOptions.Web),
                });
            }

            JsonObject? current = null;
            if (entity is not null)
            {
                current = ToJson(entity.Values, ReferenceNames(input.Model, entity.Values), Sorted(link.Attributes));
                if (entity.FactStatus is not null)
                    current["factStatus"] = entity.FactStatus;
            }

            var evidence = link.Evidence.Select(e => e with { CommitSha = e.CommitSha ?? link.CommitSha }).ToList();
            proposals.Add(new PlannedProposal(
                newId(), ProposalAction.Relink, link.EntityKind, link.EntityId, link.FactKey, entity?.Name ?? link.FactKey,
                new JsonObject { ["candidates"] = candidates }, current, [], evidence, link.Attributes, link.ConsumerKeys, [],
                Hash([ProposalAction.Relink, link.EntityKind, link.FactKey, input.RuleFingerprint, .. options.Select(o => o.FactKey)])));
        }
    }

    /// <summary>The entities that these values point at.</summary>
    private static IEnumerable<Guid> Targets(IReadOnlyDictionary<string, string?> values)
    {
        foreach (var (field, value) in values)
        {
            if (value is null)
                continue;
            if (field == Field.ConsumerIds)
            {
                foreach (var id in ModelIndex.SplitIds(value))
                    yield return id;
            }
            else if (Array.IndexOf(Field.References, field) >= 0 && Guid.TryParse(value, out var id))
                yield return id;
        }
    }

    private static List<TValue> Bucket<TKey, TValue>(Dictionary<TKey, List<TValue>> buckets, TKey key) where TKey : notnull
    {
        if (!buckets.TryGetValue(key, out var bucket))
            buckets[key] = bucket = [];
        return bucket;
    }

    private static Dictionary<string, string?> ReferenceNames(ModelIndex model, IReadOnlyDictionary<string, string?> values)
    {
        var names = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var field in Field.References)
        {
            if (values.GetValueOrDefault(field) is { } id && Guid.TryParse(id, out var parsed))
                names[field] = model.NameOf(parsed);
        }
        return names;
    }

    /// <summary>
    /// The JSON a proposal shows: each value under its model field name, the name of each
    /// referenced entity next to its ID (<c>ownerId</c>, <c>ownerName</c>), and the attributes
    /// no model field holds under <c>observed</c>.
    /// </summary>
    private static JsonObject ToJson(
        IReadOnlyDictionary<string, string?> values, IReadOnlyDictionary<string, string?> names,
        IReadOnlyDictionary<string, string> observed)
    {
        var json = new JsonObject();
        foreach (var (field, value) in values)
        {
            if (field == Field.ConsumerIds)
            {
                json[field] = new JsonArray([.. ModelIndex.SplitIds(value).Select(id => (JsonNode)id.ToString())]);
                continue;
            }
            json[field] = value;
            if (names.TryGetValue(field, out var name) && name is not null)
                json[field[..^2] + "Name"] = name;
        }

        if (observed.Count > 0)
        {
            var attributes = new JsonObject();
            foreach (var (attribute, value) in observed)
                attributes[attribute] = value;
            json["observed"] = attributes;
        }
        return json;
    }

    private static SortedDictionary<string, string> Sorted(IReadOnlyDictionary<string, string> values) =>
        new(values.ToDictionary(v => v.Key, v => v.Value), StringComparer.Ordinal);

    private static FieldChange Bounded(FieldChange change) =>
        change with { From = Bounded(change.From), To = Bounded(change.To) };

    private static string? Bounded(string? value) =>
        value is { Length: > MaxChangeText } ? value[..MaxChangeText] + "…" : value;

    /// <summary>
    /// Says what a proposal claims, in a form that stays the same from scan to scan: fact
    /// keys in place of new entity IDs. A changed fact, or an entity that a person edited
    /// again, gives another fingerprint, and the proposal is then made again.
    /// </summary>
    private static string Fingerprint(string action, Candidate candidate, IReadOnlyDictionary<string, string?>? current) => Hash(
    [
        action, candidate.EntityKind, candidate.FactKey,
        Canonical(candidate.Fields),
        Hash(candidate.References.Select(r => $"{r.Field}={r.FactKey}")),
        Hash(candidate.ConsumerKeys),
        Hash(candidate.Observed.Select(o => $"{o.Key}={o.Value}")),
        current is null ? "" : Canonical(current),
    ]);

    private static string Canonical(IReadOnlyDictionary<string, string?> values) =>
        Hash(values.OrderBy(v => v.Key, StringComparer.Ordinal).Select(v => $"{v.Key}={v.Value}"));

    private static string Hash(IEnumerable<string> parts)
    {
        // Each part is written with its length, so no two lists of parts give the same bytes.
        var text = new StringBuilder();
        foreach (var part in parts)
            text.Append(part.Length).Append(':').Append(part).Append('\n');
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }
}
