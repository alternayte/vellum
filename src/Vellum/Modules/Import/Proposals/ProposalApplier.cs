using System.Text.Json.Nodes;
using Vellum.Modules.Import.Extraction;
using Vellum.Modules.Modelling;
using Vellum.Modules.Schemas;
using Vellum.Modules.Trace.Engine;
using Vellum.Shared;
using Decision = Vellum.Shared.CommandResult<System.Collections.Generic.IReadOnlyList<object>>;

namespace Vellum.Modules.Import.Proposals;

/// <summary>
/// Turns accepted proposals into model events, through the same deciders as every other
/// write. It holds the model state of the locked stream and the states of the locked schema
/// streams, and appends nothing itself: the caller appends when every proposal applied.
/// <para>
/// A proposal is a statement about the model as it was when the scan completed. When the
/// entity changed since, the proposal is refused; a new scan compares against the new model.
/// </para>
/// </summary>
internal sealed class ProposalApplier(
    ModelState state, Dictionary<Guid, (SchemaState State, long Version)> schemas,
    Guid projectId, string repository, string commitSha, Guid acceptedBy, DateTimeOffset acceptedAt, Guid sourceId,
    bool contentNotHeld = false, IReadOnlySet<string>? notSame = null)
{
    /// <summary>The start of the reason that refuses an element addition whose name an element of the model took since the plan.</summary>
    public const string NameTaken = "The model has an element of this kind named";

    private static readonly HashSet<string> EmptySet = [];

    public const string ChangedSinceScan =
        "The model changed after this scan completed, so the proposal no longer describes it. Scan the repository again.";

    public ModelState State { get; private set; } = state;

    /// <summary>The model as the decision found it, before any of its proposals.</summary>
    private readonly ModelState _atStart = state;
    public List<object> ModelEvents { get; } = [];
    /// <summary>The events for each schema stream, with the version the stream was locked at.</summary>
    public List<(Guid SchemaId, long Version, IReadOnlyList<object> Events)> SchemaAppends { get; } = [];
    /// <summary>Schemas to delete. The delete has a guard of its own, which runs after the model append.</summary>
    public List<(Guid SchemaId, Guid ProposalId)> SchemaDeletes { get; } = [];
    /// <summary>Schemas that this decision creates. A message may name them before their rows exist.</summary>
    public HashSet<Guid> CreatedSchemas { get; } = [];
    /// <summary>Schemas that an added or changed message names, and the proposal that names each.</summary>
    public List<(Guid SchemaId, Guid ProposalId)> NamedSchemas { get; } = [];
    /// <summary>For each applied proposal, the entity's compared values after it: what the link remembers.</summary>
    public Dictionary<Guid, SortedDictionary<string, string?>> AcceptedValues { get; } = [];

    /// <summary>Why the proposal cannot be applied, or null when its events are added.</summary>
    public string? Apply(ProposalEntity proposal)
    {
        var proposed = proposal.Proposed is null ? null : JsonNode.Parse(proposal.Proposed)!.AsObject();
        var current = proposal.Current is null ? null : JsonNode.Parse(proposal.Current)!.AsObject();
        var provenance = proposal.Action == ProposalAction.Remove ? null : Provenance(proposal);
        if (proposal.Action != ProposalAction.Remove && provenance is null)
            return "The proposal has no evidence, so the fact would have no source pointer.";

        return proposal.EntityKind switch
        {
            StoredEntityKind.Element => Element(proposal, proposed, current, provenance),
            StoredEntityKind.Operation => Operation(proposal, proposed, current, provenance),
            StoredEntityKind.Channel => Channel(proposal, proposed, current, provenance),
            StoredEntityKind.Subscription => Subscription(proposal, proposed, current, provenance),
            StoredEntityKind.Binding => Binding(proposal, proposed, current, provenance),
            StoredEntityKind.Message => Message(proposal, proposed, current, provenance),
            StoredEntityKind.Schema => Schema(proposal, proposed, current),
            StoredEntityKind.Relationship => Relationship(proposal, proposed, current, provenance),
            _ => $"Entity kind '{EnumText.Of(proposal.EntityKind)}' is not one the importer writes.",
        };
    }

    private string? Element(ProposalEntity p, JsonObject? proposed, JsonObject? current, Provenance? provenance)
    {
        ElementId id = p.EntityId;
        if (p.Action == ProposalAction.Add)
        {
            if (!ModelEnum.TryParseDefined(Text(proposed, Field.Kind), out ElementKind kind))
                return $"'{Text(proposed, Field.Kind)}' is not an element kind.";
            // A model file can state a description and tags. They are set when the element is
            // added and never compared later: they are the intent of whoever wrote them.
            var tags = (Text(proposed, Field.Tags) ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.Ordinal).Take(TextRule.MaxTags).ToArray();
            // The plan asks "same as?" for an element whose name one element of the model has.
            // An addition whose name such an element took since the plan would be a second copy.
            var addedName = Text(proposed, Field.Name) ?? "";
            var sameName = _atStart.Elements.Values.Count(e => e.Kind == kind
                && ModelIndex.NormalName(e.Name) == ModelIndex.NormalName(addedName)
                && !e.Bindings.Any(b => b.SourceId == sourceId)
                // A person said this fact is not that element: the addition is their answer.
                && !(notSame ?? EmptySet).Contains(ProposalPlanner.BindFingerprint(FactMapping.KeyOf(p.Subject, p.FactKey), e.Id)));
            if (sameName == 1)
                return $"{NameTaken} '{addedName}' since this scan was planned. The scan is planned again: answer whether it is the same element.";
            return Run(p, ModelDecider.AddElement(State, new AddElementCommand(
                    id, kind, Text(proposed, Field.Name) ?? "", Text(proposed, Field.Description), Text(proposed, Field.Technology), null,
                    ElementStatus.Current, Id(proposed, Field.ParentId) is { } parent ? new ElementId(parent) : null, tags))
                .Then(State, after => ModelDecider.RecordElementProvenance(after, id, Label(p), provenance))
                .Then(State, after => ModelDecider.BindElementSource(after, id, sourceId, ExternalKeyOf(p))));
        }

        if (!State.Elements.TryGetValue(id, out var element) || Differs(ModelIndex.Of(element), current))
            return ChangedSinceScan;

        if (p.Action == ProposalAction.Remove)
        {
            // The element decider removes children, relationships and messages with the
            // element. A person accepted the removal of one thing, so anything that would go
            // with it stops the removal instead.
            var dependents = new List<string>();
            dependents.AddRange(State.Elements.Values.Where(e => e.ParentId == id).Select(e => $"element '{e.Name}'"));
            dependents.AddRange(State.Relationships.Values.Where(r => r.FromId == id || r.ToId == id).Select(r => $"relationship {r.Id}"));
            dependents.AddRange(State.Messages.Values.Where(m => m.ProducerId == id || m.ConsumerIds.Contains(id)).Select(m => $"message '{m.Name}'"));
            if (dependents.Count > 0)
            {
                return $"Element '{element.Name}' is still used by {string.Join(", ", dependents.Order(StringComparer.Ordinal).Take(10))}. " +
                    "Remove or reassign them first, or accept their removal in the same decision.";
            }
            return Run(p, ModelDecider.RemoveElement(State, id));
        }

        return Run(p, ModelDecider.UpdateElement(State, new UpdateElementCommand(
                id, Name: Text(proposed, Field.Name), SetName: true,
                Technology: Text(proposed, Field.Technology), SetTechnology: true))
            .Then(State, after => ModelDecider.RecordElementProvenance(after, id, Label(p), provenance))
            .Then(State, after => ModelDecider.BindElementSource(after, id, sourceId, ExternalKeyOf(p))));
    }

    /// <summary>
    /// A "yes" to "same as X?": the element is bound to the key of this Source, and nothing
    /// else changes. The values of the Source are proposed by the next plan of the scan.
    /// </summary>
    public string? Bind(ProposalEntity p)
    {
        if (p.Action != ProposalAction.Bind || p.EntityKind != StoredEntityKind.Element)
            return "This proposal is no question of the form 'same as this element?'.";
        if (!State.Elements.TryGetValue(p.EntityId, out var element)
            || Differs(ModelIndex.Of(element), p.Current is null ? null : JsonNode.Parse(p.Current)!.AsObject()))
            return ChangedSinceScan;
        var decision = ModelDecider.BindElementSource(State, p.EntityId, sourceId, ExternalKeyOf(p));
        if (decision is not Decision.Success success)
            return Describe(decision);
        ModelEvents.AddRange(success.Value);
        State = success.Value.Aggregate(State, ModelState.Evolve);
        return null;
    }

    private static string ExternalKeyOf(ProposalEntity p) => FactMapping.ExternalKey(FactMapping.KeyOf(p.Subject, p.FactKey));

    private string? Operation(ProposalEntity p, JsonObject? proposed, JsonObject? current, Provenance? provenance)
    {
        OperationId id = p.EntityId;
        if (p.Action != ProposalAction.Add
            && (!State.Operations.TryGetValue(id, out var operation) || Differs(ModelIndex.Of(operation), current)))
            return ChangedSinceScan;
        if (p.Action == ProposalAction.Remove)
            return Run(p, ContractDecider.RemoveOperation(State, id));

        if (!ModelEnum.TryParseDefined(Text(proposed, Field.Transport), out Transport transport))
            return $"'{Text(proposed, Field.Transport)}' is not a transport.";
        if (Id(proposed, Field.OwnerId) is not { } owner)
            return "The proposal names no owner.";

        var decision = p.Action == ProposalAction.Add
            ? ContractDecider.AddOperation(State, new AddOperationCommand(
                id, owner, Text(proposed, Field.Name) ?? "", transport, null, null, 0, RetryPolicy.None))
            : ContractDecider.UpdateOperation(State, new UpdateOperationCommand(
                id, Text(proposed, Field.Name), new ElementId(owner), transport));
        return Run(p, decision.Then(State, after =>
            ContractDecider.RecordOperationProvenance(after, id, Label(p), provenance)));
    }

    private string? Channel(ProposalEntity p, JsonObject? proposed, JsonObject? current, Provenance? provenance)
    {
        ChannelId id = p.EntityId;
        if (p.Action != ProposalAction.Add
            && (!State.Channels.TryGetValue(id, out var channel) || Differs(ModelIndex.Of(channel), current)))
            return ChangedSinceScan;
        if (p.Action == ProposalAction.Remove)
            return Run(p, ContractDecider.RemoveChannel(State, id));

        if (!ModelEnum.TryParseDefined(Text(proposed, Field.Kind), out ChannelKind kind))
            return $"'{Text(proposed, Field.Kind)}' is not a channel kind.";

        var decision = p.Action == ProposalAction.Add
            ? ContractDecider.AddChannel(State, new AddChannelCommand(id, Text(proposed, Field.Name) ?? "", kind))
            : ContractDecider.UpdateChannel(State, new UpdateChannelCommand(id, Text(proposed, Field.Name), kind));
        return Run(p, decision.Then(State, after =>
            ContractDecider.RecordChannelProvenance(after, id, Label(p), provenance)));
    }

    private string? Subscription(ProposalEntity p, JsonObject? proposed, JsonObject? current, Provenance? provenance)
    {
        SubscriptionId id = p.EntityId;
        if (p.Action != ProposalAction.Add
            && (!State.Subscriptions.TryGetValue(id, out var subscription) || Differs(ModelIndex.Of(State, subscription), current)))
            return ChangedSinceScan;
        if (p.Action == ProposalAction.Remove)
            return Run(p, ContractDecider.RemoveSubscription(State, id));

        if (Id(proposed, Field.ChannelId) is not { } channel || Id(proposed, Field.ConsumerId) is not { } consumer)
            return "The proposal names no channel or no consumer.";
        ChannelId? deadLetter = Id(proposed, Field.DeadLetterChannelId) is { } d ? new ChannelId(d) : null;

        var decision = p.Action == ProposalAction.Add
            ? ContractDecider.AddSubscription(State, new AddSubscriptionCommand(id, channel, consumer, RetryPolicy.None, deadLetter))
            : ContractDecider.UpdateSubscription(State, new UpdateSubscriptionCommand(
                id, new ChannelId(channel), new ElementId(consumer), null, deadLetter, SetDeadLetterChannelId: true));
        return Run(p, decision.Then(State, after =>
            ContractDecider.RecordSubscriptionProvenance(after, id, Label(p), provenance)));
    }

    private string? Binding(ProposalEntity p, JsonObject? proposed, JsonObject? current, Provenance? provenance)
    {
        BindingId id = p.EntityId;
        if (p.Action != ProposalAction.Add
            && (!State.Bindings.TryGetValue(id, out var binding) || Differs(ModelIndex.Of(State, binding), current)))
            return ChangedSinceScan;
        if (p.Action == ProposalAction.Remove)
            return Run(p, ContractDecider.RemoveBinding(State, id));

        if (Id(proposed, Field.SourceChannelId) is not { } source || Id(proposed, Field.TargetChannelId) is not { } target)
            return "The proposal names no source channel or no target channel.";
        var routingKey = Text(proposed, Field.RoutingKey);

        var decision = p.Action == ProposalAction.Add
            ? ContractDecider.AddBinding(State, new AddBindingCommand(id, source, target, routingKey))
            : ContractDecider.UpdateBinding(State, new UpdateBindingCommand(
                id, new ChannelId(source), new ChannelId(target), routingKey, SetRoutingKey: true));
        return Run(p, decision.Then(State, after =>
            ContractDecider.RecordBindingProvenance(after, id, Label(p), provenance)));
    }

    /// <summary>
    /// A runtime edge between two elements: behavioural, since it records calls or messages
    /// that ran. A change moves its ends only; its label stays what a person made it.
    /// </summary>
    private string? Relationship(ProposalEntity p, JsonObject? proposed, JsonObject? current, Provenance? provenance)
    {
        RelationshipId id = p.EntityId;
        if (p.Action != ProposalAction.Add
            && (!State.Relationships.TryGetValue(id, out var relationship) || Differs(ModelIndex.Of(State, relationship), current)))
            return ChangedSinceScan;
        if (p.Action == ProposalAction.Remove)
            return Run(p, ModelDecider.RemoveRelationship(State, id));

        if (Id(proposed, Field.FromId) is not { } from || Id(proposed, Field.ToId) is not { } to)
            return "The proposal names no source element or no target element.";
        if (p.Action == ProposalAction.Add)
        {
            // A runtime edge ran, so it is behavioural; a model file says which kind it is.
            var kind = Text(proposed, Field.Kind) == "structural" ? RelationshipKind.Structural : RelationshipKind.Behavioural;
            return Run(p, ModelDecider.AddRelationship(State, new AddRelationshipCommand(
                    id, from, to, Text(proposed, Field.Label), Text(proposed, Field.Technology), kind))
                .Then(State, after => ModelDecider.RecordRelationshipProvenance(after, id, Label(p), provenance)));
        }

        // The model has no command that moves an edge. One whose ends changed is a new edge,
        // which the next scan proposes after this one is removed.
        var existing = State.Relationships[id];
        if (existing.FromId != new ElementId(from) || existing.ToId != new ElementId(to))
            return "The ends of this relationship changed. Remove it, then scan again to add the new one.";
        return Run(p, ModelDecider.RecordRelationshipProvenance(State, id, Label(p), provenance));
    }

    /// <summary>The truth label the Source gives the fact of the proposal (<see cref="FactMapping.LabelOf"/>).</summary>
    private static FactStatus Label(ProposalEntity p) =>
        FactStatusFormat.TryParseApiString(FactMapping.LabelOf(ProposalJson.ReadAttributes(p.Attributes), ProposalJson.ReadEvidence(p.Evidence)), out var status) ? status : FactStatus.ObservedInSource;

    private string? Message(ProposalEntity p, JsonObject? proposed, JsonObject? current, Provenance? provenance)
    {
        MessageId id = p.EntityId;
        if (p.Action != ProposalAction.Add
            && (!State.Messages.TryGetValue(id, out var message) || Differs(ModelIndex.Of(message), current)))
            return ChangedSinceScan;
        if (p.Action == ProposalAction.Remove)
            return Run(p, ModelDecider.RemoveMessage(State, id));

        if (Id(proposed, Field.ProducerId) is not { } producer)
            return "The proposal names no producer.";
        var consumers = (proposed?[Field.ConsumerIds] as JsonArray ?? [])
            .Select(node => new ElementId(Guid.Parse(node!.GetValue<string>()))).ToArray();
        SchemaId? schema = Id(proposed, Field.SchemaId) is { } s ? new SchemaId(s) : null;
        if (schema is { } named)
            NamedSchemas.Add((named, p.Id));

        var decision = p.Action == ProposalAction.Add
            ? ModelDecider.AddMessage(State, new AddMessageCommand(id, Text(proposed, Field.Name) ?? "", null, producer, consumers, schema, []))
            : ModelDecider.UpdateMessage(State, new UpdateMessageCommand(
                id, Name: Text(proposed, Field.Name), SetName: true,
                ProducerId: new ElementId(producer), SetProducerId: true,
                ConsumerIds: consumers, SetConsumerIds: true,
                SchemaId: schema, SetSchemaId: true));
        return Run(p, decision.Then(State, after =>
            ModelDecider.RecordMessageProvenance(after, id, Label(p), provenance)));
    }

    /// <summary>
    /// A schema is its own stream and has no truth label, so accepting writes its name and
    /// content only. Its provenance is the link row.
    /// </summary>
    private string? Schema(ProposalEntity p, JsonObject? proposed, JsonObject? current)
    {
        if (!schemas.TryGetValue(p.EntityId, out var locked))
            return "The schema's stream was not locked for this decision.";
        var entry = locked.State.Schema;

        Decision decision;
        if (p.Action == ProposalAction.Add)
        {
            decision = SchemaDecider.Create(locked.State, new CreateSchemaCommand(
                p.EntityId, Text(proposed, Field.Name) ?? "", null, Text(proposed, Field.Content) ?? "", projectId));
            if (decision is Decision.Success)
                CreatedSchemas.Add(p.EntityId);
        }
        else
        {
            if (entry is null || entry.ProjectId != projectId
                || Differs(ModelIndex.Of(new SchemaRow(entry.Id, entry.Name, entry.Content)), current))
                return ChangedSinceScan;

            if (p.Action == ProposalAction.Remove)
            {
                SchemaDeletes.Add((p.EntityId, p.Id));
                return null;
            }

            decision = SchemaDecider.Update(locked.State, new UpdateSchemaCommand(
                p.EntityId, Name: Text(proposed, Field.Name), SetName: true,
                Content: Text(proposed, Field.Content), SetContent: true));
        }

        if (decision is not Decision.Success success)
            return Describe(decision);

        if (success.Value.Count > 0)
            SchemaAppends.Add((p.EntityId, locked.Version, success.Value));
        AcceptedValues[p.Id] = Values((Field.Name, Text(proposed, Field.Name)), (Field.Content, Text(proposed, Field.Content)));
        return null;
    }

    private string? Run(ProposalEntity proposal, Decision decision)
    {
        if (decision is not Decision.Success success)
            return Describe(decision);

        ModelEvents.AddRange(success.Value);
        State = success.Value.Aggregate(State, ModelState.Evolve);

        if (proposal.Action != ProposalAction.Remove)
        {
            var entity = ModelIndex.Read(State, EnumText.Of(proposal.EntityKind), proposal.EntityId);
            var accepted = new SortedDictionary<string, string?>(StringComparer.Ordinal);
            foreach (var field in ModelIndex.ComparedFields(EnumText.Of(proposal.EntityKind)))
                accepted[field] = entity?.Values.GetValueOrDefault(field);
            AcceptedValues[proposal.Id] = accepted;
        }
        return null;
    }

    /// <summary>
    /// True when the entity no longer has the values the proposal was computed against. The
    /// compared fields and the truth label are checked; an edit of a description or a tag is
    /// not a change of what the proposal is about.
    /// </summary>
    private static bool Differs(ModelEntity live, JsonObject? current)
    {
        if (current is null)
            return true;
        foreach (var field in ModelIndex.ComparedFields(live.EntityKind))
        {
            var then = field == Field.ConsumerIds
                ? ModelIndex.JoinIds((current[field] as JsonArray ?? []).Select(node => Guid.Parse(node!.GetValue<string>())))
                : Text(current, field);
            var now = live.Values.GetValueOrDefault(field);
            if (field == Field.ConsumerIds)
                now = ModelIndex.JoinIds(ModelIndex.SplitIds(now));
            if (!string.Equals(then, now, StringComparison.Ordinal))
                return true;
        }
        return live.FactStatus is not null && Text(current, "factStatus") != live.FactStatus;
    }

    /// <summary>The first evidence is the pointer on the fact. All of it stays on the link row.</summary>
    private Provenance? Provenance(ProposalEntity proposal)
    {
        var first = ProposalJson.ReadEvidence(proposal.Evidence).FirstOrDefault();
        return first is null
            ? null
            : new Provenance(EvidenceLinks.Pointer(repository, commitSha, first), commitSha, null, acceptedBy, acceptedAt, ContentNotHeld: contentNotHeld);
    }

    public static string Describe(Decision decision) => decision switch
    {
        Decision.Invalid invalid => string.Join("; ", invalid.Errors.Select(e => $"{e.Field}: {e.Message}")),
        Decision.Conflict conflict => conflict.Message,
        Decision.NotFound notFound => notFound.Message,
        _ => "The model refused the change.",
    };

    private static string? Text(JsonObject? json, string field) =>
        json is not null && json.TryGetPropertyValue(field, out var node) && node is JsonValue value && value.TryGetValue<string>(out var text)
            ? text
            : null;

    private static Guid? Id(JsonObject? json, string field) =>
        Guid.TryParse(Text(json, field), out var id) ? id : null;

    private static SortedDictionary<string, string?> Values(params (string Name, string? Value)[] values)
    {
        var result = new SortedDictionary<string, string?>(StringComparer.Ordinal);
        foreach (var (name, value) in values)
            result[name] = value;
        return result;
    }
}
