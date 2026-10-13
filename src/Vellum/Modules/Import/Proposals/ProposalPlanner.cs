using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Vellum.Modules.Import.Extraction;
using Vellum.Modules.Modelling.Model;

namespace Vellum.Modules.Import.Proposals;

/// <summary>An accepted fact, as the planner reads it from <c>import.fact_links</c>.</summary>
internal sealed record LinkInfo(
    string FactKey, string EntityKind, Guid EntityId, string CommitSha,
    IReadOnlyDictionary<string, string> Attributes,
    IReadOnlyDictionary<string, string?> Accepted,
    IReadOnlyList<EvidenceRef> Evidence);

/// <param name="Repository">With the host, for example <c>github.com/acme/orders</c>.</param>
/// <param name="IsComplete">False when the scan reached a bound. Then a missing fact proves nothing.</param>
/// <param name="Links">The accepted facts of this repository in this project, by fact key.</param>
/// <param name="TakenEntityIds">Entities that a fact of any repository is linked to. A fact never matches one of them by name.</param>
/// <param name="Rejections">Fact key and fingerprint of every proposal a person rejected.</param>
internal sealed record PlanInput(
    string Repository, string CommitSha, bool IsComplete,
    IReadOnlyList<ExtractedFact> Facts, ModelIndex Model,
    IReadOnlyDictionary<string, LinkInfo> Links,
    IReadOnlySet<Guid> TakenEntityIds,
    IReadOnlySet<(string FactKey, string Fingerprint)> Rejections);

internal sealed record FieldChange(string Field, string? From, string? To);

internal sealed record PlannedProposal(
    Guid Id, string Action, string EntityKind, Guid EntityId, string FactKey, string Name,
    JsonObject? Proposed, JsonObject? Current, IReadOnlyList<FieldChange> Changes,
    IReadOnlyList<EvidenceRef> Evidence, IReadOnlyDictionary<string, string> Attributes,
    IReadOnlyList<Guid> DependsOn, string Fingerprint);

/// <param name="MarkStale">Entities whose evidence a complete scan no longer finds.</param>
/// <param name="ClearStale">Entities with a stale mark whose evidence this scan found.</param>
internal sealed record Plan(
    IReadOnlyList<PlannedProposal> Proposals, IReadOnlyList<MappedFact> Facts,
    IReadOnlyList<Guid> MarkStale, IReadOnlyList<Guid> ClearStale);

/// <summary>
/// Compares the facts of one snapshot with the project's main model and with what was
/// accepted before, and says what a person could accept. It is a pure function: it reads its
/// input and writes nothing, so it cannot change the model.
/// <list type="bullet">
/// <item>A fact with no linked and no matching entity: <c>add</c>.</item>
/// <item>A linked entity whose observed values differ: <c>change</c>, or <c>conflict</c> when
/// the entity is not <c>observed-in-source</c> or a person edited it after it was accepted.</item>
/// <item>An unlinked fact that matches exactly one hand-made entity by
/// <see cref="ModelIndex.MatchKey"/>: a <c>change</c> or <c>conflict</c> that would link them.
/// Two or more matches are not a match.</item>
/// <item>A linked entity whose fact is missing from a complete snapshot: <c>remove</c>, and a
/// stale mark when the entity is <c>observed-in-source</c>. A partial snapshot gives neither.</item>
/// <item>A proposal that a person rejected is not made again while it says the same thing.</item>
/// </list>
/// </summary>
internal static class ProposalPlanner
{
    public const string ObservedInSource = "observed-in-source";
    private const int MaxChangeText = 2000;

    private sealed record Resolution(Guid? EntityId, Guid? AddProposalId, string? Name, string? Blocked);

    public static Plan Compute(PlanInput input, Func<Guid> newId)
    {
        var mapping = FactMapping.Map(input.Repository, input.Facts);
        var facts = mapping.Facts.ToList();
        var proposals = new List<PlannedProposal>();
        var resolved = new Dictionary<string, Resolution>(StringComparer.Ordinal);
        var claimed = new HashSet<Guid>();
        var markStale = new List<Guid>();
        var clearStale = new List<Guid>();
        // Change proposals with the entity's values before and after: a change can be what
        // stops pointing at an entity that another proposal removes.
        var releases = new List<(Guid ProposalId, IReadOnlyDictionary<string, string?> Before, IReadOnlyDictionary<string, string?> After)>();

        foreach (var candidate in mapping.Candidates)
        {
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
            var link = input.Links.GetValueOrDefault(candidate.FactKey);
            var entity = link is not null && link.EntityKind == candidate.EntityKind
                ? input.Model.Find(candidate.EntityKind, link.EntityId)
                : null;
            var linked = entity is not null;
            if (entity is null && blocked is null)
            {
                var key = ModelIndex.MatchKey(candidate.EntityKind, values);
                var matches = input.Model.OfKind(candidate.EntityKind)
                    .Where(e => !input.TakenEntityIds.Contains(e.Id) && !claimed.Contains(e.Id)
                        && ModelIndex.MatchKey(e.EntityKind, e.Values) == key)
                    .Take(2).ToList();
                if (matches.Count == 1)
                    entity = matches[0];
            }

            if (entity is null)
            {
                if (blocked is not null || candidate.NotAddable is not null)
                {
                    resolved[candidate.FactKey] = new Resolution(null, null, null, "dependency-unavailable");
                    Reclassify(facts, candidate.FactKey,
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
                    [.. dependsOn.Distinct()], addFingerprint));
                resolved[candidate.FactKey] = new Resolution(entityId, proposalId, candidate.Name, null);
                continue;
            }

            // The fact is present, so whatever follows, later facts can point at its entity.
            resolved[candidate.FactKey] = new Resolution(entity.Id, null, entity.Name, null);
            claimed.Add(entity.Id);
            if (linked && entity.Stale is not null)
                clearStale.Add(entity.Id);
            if (blocked is not null)
                continue;

            if (candidate.EntityKind == EntityKinds.Message)
            {
                // A scan adds a consumer it finds. It takes away only a consumer that an
                // accepted scan observed and a complete scan no longer finds; a consumer that
                // a person declared has no subscription in the source to be missing.
                var gone = new HashSet<Guid>();
                if (linked && input.IsComplete && link!.Attributes.GetValueOrDefault(FactMapping.ObservedConsumers) is { } before)
                {
                    foreach (var key in before.Split(',').Except(candidate.ConsumerKeys, StringComparer.Ordinal))
                    {
                        if (input.Links.GetValueOrDefault(key) is { } consumer)
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
            releases.Add((changeId, entity.Values, after));
            proposals.Add(new PlannedProposal(
                changeId, action, candidate.EntityKind, entity.Id, candidate.FactKey, candidate.Name,
                ToJson(after, afterNames, candidate.Observed), currentJson,
                [.. changes.Select(Bounded)], candidate.Evidence, candidate.Attributes,
                [.. dependsOn.Distinct()], fingerprint));
        }

        if (input.IsComplete)
            PlanRemovals(input, mapping, proposals, releases, markStale, newId);

        return new Plan(proposals, facts, markStale, clearStale);
    }

    /// <summary>
    /// A complete snapshot that lacks a fact shows that the evidence the entity was accepted
    /// with is gone. The entity gets a stale mark, which deletes nothing, and a person may
    /// accept its removal.
    /// </summary>
    private static void PlanRemovals(
        PlanInput input, MappingResult mapping, List<PlannedProposal> proposals,
        List<(Guid ProposalId, IReadOnlyDictionary<string, string?> Before, IReadOnlyDictionary<string, string?> After)> releases,
        List<Guid> markStale, Func<Guid> newId)
    {
        var present = input.Facts.Select(f => f.Key).Concat(mapping.Candidates.Select(c => c.FactKey))
            .ToHashSet(StringComparer.Ordinal);

        var removals = new List<(PlannedProposal Proposal, ModelEntity Entity)>();
        foreach (var link in input.Links.Values.OrderBy(l => l.FactKey, StringComparer.Ordinal))
        {
            if (present.Contains(link.FactKey) || input.Model.Find(link.EntityKind, link.EntityId) is not { } entity)
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
                null, current, [], evidence, link.Attributes, [], fingerprint), entity));
        }

        // An entity is removed after the entities that point at it are removed, and after
        // the changes that make an entity stop pointing at it.
        foreach (var (proposal, entity) in removals)
        {
            var id = entity.Id.ToString();
            var first = removals
                .Where(other => other.Entity.Id != entity.Id && PointsAt(other.Entity.Values, id))
                .Select(other => other.Proposal.Id)
                .Concat(releases.Where(r => PointsAt(r.Before, id) && !PointsAt(r.After, id)).Select(r => r.ProposalId))
                .ToList();
            proposals.Add(proposal with { DependsOn = first });
        }
    }

    private static bool PointsAt(IReadOnlyDictionary<string, string?> values, string id) => values.Any(v =>
        v.Value is not null && (v.Key == Field.ConsumerIds
            ? v.Value.Contains(id, StringComparison.Ordinal)
            : Field.References.Contains(v.Key) && v.Value == id));

    private static void Reclassify(List<MappedFact> facts, string factKey, string disposition, string? reason)
    {
        var index = facts.FindIndex(f => f.Fact.Key == factKey && f.Disposition == FactDisposition.Mapped);
        if (index >= 0)
            facts[index] = facts[index] with { Disposition = disposition, Reason = reason };
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
        string.Join("\n", candidate.References.Select(r => $"{r.Field}={r.FactKey}")),
        string.Join(",", candidate.ConsumerKeys),
        string.Join("\n", candidate.Observed.Select(o => $"{o.Key}={o.Value}")),
        current is null ? "" : Canonical(current),
    ]);

    private static string Canonical(IReadOnlyDictionary<string, string?> values) =>
        string.Join("\n", values.OrderBy(v => v.Key, StringComparer.Ordinal).Select(v => $"{v.Key}={v.Value}"));

    private static string Hash(IEnumerable<string> parts)
    {
        // Each part is written with its length, so no two lists of parts give the same bytes.
        var text = new StringBuilder();
        foreach (var part in parts)
            text.Append(part.Length).Append(':').Append(part).Append('\n');
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }
}
