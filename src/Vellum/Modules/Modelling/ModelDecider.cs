using Vellum.Shared;

namespace Vellum.Modules.Modelling;

public sealed record AddElementCommand(
    ElementId Id, ElementKind Kind, string Name, string? Description,
    string? Technology, Guid? OwnerId, ElementStatus Status,
    ElementId? ParentId, string[] Tags);

public sealed record UpdateElementCommand(
    ElementId ElementId,
    string? Name = null, bool SetName = false,
    string? Description = null, bool SetDescription = false,
    string? Technology = null, bool SetTechnology = false,
    Guid? OwnerId = null, bool SetOwnerId = false,
    ElementId? ParentId = null, bool SetParentId = false,
    ElementStatus? Status = null,
    string[]? Tags = null,
    string? Icon = null, bool SetIcon = false);

public sealed record AddRelationshipCommand(
    RelationshipId Id, ElementId FromId, ElementId ToId, string? Label, string? Technology,
    RelationshipKind Kind = RelationshipKind.Structural);

public sealed record UpdateRelationshipCommand(
    RelationshipId RelationshipId,
    string? Label = null, bool SetLabel = false,
    string? Technology = null, bool SetTechnology = false,
    string? LineShape = null, bool SetLineShape = false,
    RelationshipKind? Kind = null, bool SetKind = false);

public sealed record AddMessageCommand(
    MessageId Id, string Name, string? Description,
    ElementId ProducerId, ElementId[] ConsumerIds,
    SchemaId? SchemaId, string[] Tags);

public sealed record UpdateMessageCommand(
    MessageId MessageId,
    string? Name = null, bool SetName = false,
    string? Description = null, bool SetDescription = false,
    ElementId? ProducerId = null, bool SetProducerId = false,
    ElementId[]? ConsumerIds = null, bool SetConsumerIds = false,
    SchemaId? SchemaId = null, bool SetSchemaId = false,
    string[]? Tags = null);

public static class ModelDecider
{
    public static CommandResult<IReadOnlyList<object>> AddElement(ModelState state, AddElementCommand cmd)
    {
        if (string.IsNullOrWhiteSpace(cmd.Name))
            return new CommandResult<IReadOnlyList<object>>.Invalid([new ValidationError("name", "Name is required")]);

        var textError = TextRule.Line("name", cmd.Name)
            ?? TextRule.Lines("description", cmd.Description)
            ?? TextRule.Line("technology", cmd.Technology)
            ?? TextRule.Tags("tags", cmd.Tags);
        if (textError is not null)
            return new CommandResult<IReadOnlyList<object>>.Invalid([textError]);

        if (state.RefuseNewId(cmd.Id) is { } idRefusal)
            return idRefusal;

        var parentError = ModelInvariants.Containment(state, cmd.Kind, cmd.ParentId);
        if (parentError is not null)
            return new CommandResult<IReadOnlyList<object>>.Invalid([parentError]);

        return new CommandResult<IReadOnlyList<object>>.Success(
            [new ModelEvent.ElementAdded(cmd.Id, cmd.Kind, cmd.Name, cmd.Description,
                cmd.Technology, cmd.OwnerId, cmd.Status, cmd.ParentId, cmd.Tags)]);
    }

    public static CommandResult<IReadOnlyList<object>> UpdateElement(ModelState state, UpdateElementCommand cmd)
    {
        if (!state.Elements.TryGetValue(cmd.ElementId, out var element))
            return new CommandResult<IReadOnlyList<object>>.NotFound("Element not found");

        var textError =
            (cmd.SetName ? TextRule.Line("name", cmd.Name) : null)
            ?? (cmd.SetDescription ? TextRule.Lines("description", cmd.Description) : null)
            ?? (cmd.SetTechnology ? TextRule.Line("technology", cmd.Technology) : null)
            ?? TextRule.Tags("tags", cmd.Tags)
            ?? (cmd.SetIcon ? TextRule.Line("icon", cmd.Icon) : null);
        if (textError is not null)
            return new CommandResult<IReadOnlyList<object>>.Invalid([textError]);

        var events = new List<object>();

        if (cmd.SetName && cmd.Name != element.Name)
        {
            if (string.IsNullOrWhiteSpace(cmd.Name))
                return new CommandResult<IReadOnlyList<object>>.Invalid([new ValidationError("name", "Name is required")]);
            events.Add(new ModelEvent.ElementRenamed(cmd.ElementId, cmd.Name!));
        }

        if (cmd.SetDescription && cmd.Description != element.Description)
            events.Add(new ModelEvent.ElementDescriptionChanged(cmd.ElementId, cmd.Description));

        if (cmd.SetTechnology && cmd.Technology != element.Technology)
            events.Add(new ModelEvent.ElementTechnologyChanged(cmd.ElementId, cmd.Technology));

        if (cmd.SetOwnerId && cmd.OwnerId != element.OwnerId)
            events.Add(new ModelEvent.ElementOwnerChanged(cmd.ElementId, cmd.OwnerId));

        if (cmd.SetParentId && cmd.ParentId != element.ParentId)
        {
            var parentError = ModelInvariants.Containment(state, element.Kind, cmd.ParentId);
            if (parentError is not null)
                return new CommandResult<IReadOnlyList<object>>.Invalid([parentError]);
            events.Add(new ModelEvent.ElementReparented(cmd.ElementId, cmd.ParentId));
        }

        if (cmd.Status.HasValue && cmd.Status.Value != element.Status)
            events.Add(new ModelEvent.ElementStatusChanged(cmd.ElementId, cmd.Status.Value));

        if (cmd.Tags is not null && !cmd.Tags.SequenceEqual(element.Tags))
            events.Add(new ModelEvent.ElementRetagged(cmd.ElementId, cmd.Tags));

        if (cmd.SetIcon && cmd.Icon != element.Icon)
            events.Add(new ModelEvent.ElementIconChanged(cmd.ElementId, cmd.Icon));

        return new CommandResult<IReadOnlyList<object>>.Success(events);
    }

    public static CommandResult<IReadOnlyList<object>> RemoveElement(ModelState state, ElementId elementId)
    {
        if (!state.Elements.ContainsKey(elementId))
            return new CommandResult<IReadOnlyList<object>>.NotFound("Element not found");

        // Relationships and messages go with the element. A contract does not: a scenario
        // plays against it, so its owner must be removed or reassigned on purpose.
        var subtree = new HashSet<ElementId>();
        CollectSubtree(state, elementId, subtree);
        var references = ContractDecider.ReferencesToElements(state, subtree);
        if (references.Count > 0)
        {
            return new CommandResult<IReadOnlyList<object>>.Conflict(
                $"Element '{state.Elements[elementId].Name}' cannot be removed. It, or an element inside it, " +
                $"is referenced by {string.Join(", ", references)}. Remove or reassign them first.");
        }

        var events = new List<object>();
        CollectCascadeRemovals(state, elementId, events, new HashSet<Guid>());
        return new CommandResult<IReadOnlyList<object>>.Success(events);
    }

    private static void CollectSubtree(ModelState state, ElementId elementId, HashSet<ElementId> subtree)
    {
        if (!subtree.Add(elementId))
            return;
        foreach (var child in state.Elements.Values.Where(e => e.ParentId == elementId))
            CollectSubtree(state, child.Id, subtree);
    }

    public static CommandResult<IReadOnlyList<object>> AddRelationship(ModelState state, AddRelationshipCommand cmd)
    {
        if ((TextRule.Line("label", cmd.Label) ?? TextRule.Line("technology", cmd.Technology)) is { } textError)
            return new CommandResult<IReadOnlyList<object>>.Invalid([textError]);

        if (state.RefuseNewId(cmd.Id) is { } idRefusal)
            return idRefusal;

        if (ModelInvariants.Relationship(state, cmd.FromId, cmd.ToId) is { } endError)
            return new CommandResult<IReadOnlyList<object>>.Invalid([endError]);

        return new CommandResult<IReadOnlyList<object>>.Success(
            [new ModelEvent.RelationshipAdded(cmd.Id, cmd.FromId, cmd.ToId, cmd.Label, cmd.Technology, null, cmd.Kind)]);
    }

    public static CommandResult<IReadOnlyList<object>> UpdateRelationship(ModelState state, UpdateRelationshipCommand cmd)
    {
        if (!state.Relationships.TryGetValue(cmd.RelationshipId, out var rel))
            return new CommandResult<IReadOnlyList<object>>.NotFound("Relationship not found");

        var textError =
            (cmd.SetLabel ? TextRule.Line("label", cmd.Label) : null)
            ?? (cmd.SetTechnology ? TextRule.Line("technology", cmd.Technology) : null)
            ?? (cmd.SetLineShape ? TextRule.Line("lineShape", cmd.LineShape, TextRule.MaxIdentifier) : null);
        if (textError is not null)
            return new CommandResult<IReadOnlyList<object>>.Invalid([textError]);

        var events = new List<object>();

        if (cmd.SetLabel && cmd.Label != rel.Label)
            events.Add(new ModelEvent.RelationshipLabelChanged(cmd.RelationshipId, cmd.Label));

        if (cmd.SetTechnology && cmd.Technology != rel.Technology)
            events.Add(new ModelEvent.RelationshipTechnologyChanged(cmd.RelationshipId, cmd.Technology));

        if (cmd.SetLineShape && cmd.LineShape != rel.LineShape)
            events.Add(new ModelEvent.RelationshipLineShapeChanged(cmd.RelationshipId, cmd.LineShape));

        if (cmd.SetKind && cmd.Kind.HasValue && cmd.Kind.Value != rel.Kind)
            events.Add(new ModelEvent.RelationshipKindChanged(cmd.RelationshipId, cmd.Kind.Value));

        return new CommandResult<IReadOnlyList<object>>.Success(events);
    }

    public static CommandResult<IReadOnlyList<object>> RemoveRelationship(ModelState state, RelationshipId relationshipId)
    {
        if (!state.Relationships.ContainsKey(relationshipId))
            return new CommandResult<IReadOnlyList<object>>.NotFound("Relationship not found");

        return new CommandResult<IReadOnlyList<object>>.Success(
            [new ModelEvent.RelationshipRemoved(relationshipId)]);
    }

    public static CommandResult<IReadOnlyList<object>> AddMessage(ModelState state, AddMessageCommand cmd)
    {
        if (string.IsNullOrWhiteSpace(cmd.Name))
            return new CommandResult<IReadOnlyList<object>>.Invalid([new ValidationError("name", "Name is required")]);

        var textError = TextRule.Line("name", cmd.Name)
            ?? TextRule.Lines("description", cmd.Description)
            ?? TextRule.Tags("tags", cmd.Tags);
        if (textError is not null)
            return new CommandResult<IReadOnlyList<object>>.Invalid([textError]);

        if (state.RefuseNewId(cmd.Id) is { } idRefusal)
            return idRefusal;

        if (ModelInvariants.Message(state, cmd.ProducerId, cmd.ConsumerIds) is { } endError)
            return new CommandResult<IReadOnlyList<object>>.Invalid([endError]);

        return new CommandResult<IReadOnlyList<object>>.Success(
            [new ModelEvent.MessageAdded(cmd.Id, cmd.Name, cmd.Description,
                cmd.ProducerId, cmd.ConsumerIds, cmd.SchemaId, cmd.Tags)]);
    }

    public static CommandResult<IReadOnlyList<object>> UpdateMessage(ModelState state, UpdateMessageCommand cmd)
    {
        if (!state.Messages.TryGetValue(cmd.MessageId, out var msg))
            return new CommandResult<IReadOnlyList<object>>.NotFound("Message not found");

        if (cmd.SetName && string.IsNullOrWhiteSpace(cmd.Name))
            return new CommandResult<IReadOnlyList<object>>.Invalid([new ValidationError("name", "Name is required")]);

        var textError =
            (cmd.SetName ? TextRule.Line("name", cmd.Name) : null)
            ?? (cmd.SetDescription ? TextRule.Lines("description", cmd.Description) : null)
            ?? TextRule.Tags("tags", cmd.Tags);
        if (textError is not null)
            return new CommandResult<IReadOnlyList<object>>.Invalid([textError]);

        if (ModelInvariants.Message(state, cmd.SetProducerId ? cmd.ProducerId : null, cmd.SetConsumerIds ? cmd.ConsumerIds : null) is { } endError)
            return new CommandResult<IReadOnlyList<object>>.Invalid([endError]);

        var hasChanges =
            (cmd.SetName && cmd.Name != msg.Name) ||
            (cmd.SetDescription && cmd.Description != msg.Description) ||
            (cmd.SetProducerId && cmd.ProducerId != msg.ProducerId) ||
            (cmd.SetConsumerIds && cmd.ConsumerIds is not null && !cmd.ConsumerIds.SequenceEqual(msg.ConsumerIds)) ||
            (cmd.SetSchemaId && cmd.SchemaId != msg.SchemaId) ||
            (cmd.Tags is not null && !cmd.Tags.SequenceEqual(msg.Tags));

        if (!hasChanges)
            return new CommandResult<IReadOnlyList<object>>.Success([]);

        return new CommandResult<IReadOnlyList<object>>.Success(
            [new ModelEvent.MessageUpdated(
                cmd.MessageId,
                cmd.SetName ? cmd.Name : null,
                cmd.SetDescription ? cmd.Description : null,
                cmd.SetProducerId ? cmd.ProducerId : null,
                cmd.SetConsumerIds ? cmd.ConsumerIds : null,
                cmd.SetSchemaId ? cmd.SchemaId : null,
                cmd.SetSchemaId,
                cmd.Tags)]);
    }

    public static CommandResult<IReadOnlyList<object>> RemoveMessage(ModelState state, MessageId messageId)
    {
        if (!state.Messages.ContainsKey(messageId))
            return new CommandResult<IReadOnlyList<object>>.NotFound("Message not found");

        return new CommandResult<IReadOnlyList<object>>.Success(
            [new ModelEvent.MessageRemoved(messageId)]);
    }

    public static CommandResult<IReadOnlyList<object>> RecordElementProvenance(
        ModelState state, ElementId elementId, FactStatus status, Provenance? provenance)
    {
        if (!state.Elements.TryGetValue(elementId, out var element))
            return new CommandResult<IReadOnlyList<object>>.NotFound("Element not found");

        var validationError = FactRules.Validate(status, provenance);
        if (validationError is not null)
            return new CommandResult<IReadOnlyList<object>>.Invalid([validationError]);

        if (element.FactStatus == status && ProvenanceEquivalent(element.Provenance, provenance) && element.Stale is null)
            return new CommandResult<IReadOnlyList<object>>.Success([]);

        return new CommandResult<IReadOnlyList<object>>.Success(
            [new ModelEvent.ElementProvenanceRecorded(elementId, status, provenance)]);
    }

    /// <summary>The longest external key a source binding holds: the longest fact key an import keeps.</summary>
    public const int MaxExternalKey = 1000;

    /// <summary>
    /// Binds the element to <paramref name="externalKey"/> of the Source <paramref name="sourceId"/>.
    /// A pair that another element holds is refused: one key of one Source names one element.
    /// A pair the element holds already writes nothing.
    /// </summary>
    public static CommandResult<IReadOnlyList<object>> BindElementSource(
        ModelState state, ElementId elementId, Guid sourceId, string externalKey)
    {
        if (!state.Elements.TryGetValue(elementId, out var element))
            return new CommandResult<IReadOnlyList<object>>.NotFound("Element not found");
        if (sourceId == Guid.Empty || externalKey.Length is 0 or > MaxExternalKey || externalKey.Any(char.IsControl))
            return new CommandResult<IReadOnlyList<object>>.Invalid([new ValidationError("externalKey", $"A source binding names a Source and a key of 1 to {MaxExternalKey} characters with no control character.")]);

        var binding = new SourceBinding(sourceId, externalKey);
        if (element.Bindings.Contains(binding))
            return new CommandResult<IReadOnlyList<object>>.Success([]);
        if (state.Elements.Values.FirstOrDefault(e => e.Id != elementId && e.Bindings.Contains(binding)) is { } holder)
            return new CommandResult<IReadOnlyList<object>>.Conflict($"Element '{holder.Name}' is already bound to this key of the Source.");

        return new CommandResult<IReadOnlyList<object>>.Success([new ModelEvent.ElementSourceBound(elementId, sourceId, externalKey)]);
    }

    public static CommandResult<IReadOnlyList<object>> RecordRelationshipProvenance(
        ModelState state, RelationshipId relationshipId, FactStatus status, Provenance? provenance)
    {
        if (!state.Relationships.TryGetValue(relationshipId, out var relationship))
            return new CommandResult<IReadOnlyList<object>>.NotFound("Relationship not found");

        var validationError = FactRules.Validate(status, provenance);
        if (validationError is not null)
            return new CommandResult<IReadOnlyList<object>>.Invalid([validationError]);

        if (relationship.FactStatus == status && ProvenanceEquivalent(relationship.Provenance, provenance) && relationship.Stale is null)
            return new CommandResult<IReadOnlyList<object>>.Success([]);

        return new CommandResult<IReadOnlyList<object>>.Success(
            [new ModelEvent.RelationshipProvenanceRecorded(relationshipId, status, provenance)]);
    }

    public static CommandResult<IReadOnlyList<object>> RecordMessageProvenance(
        ModelState state, MessageId messageId, FactStatus status, Provenance? provenance)
    {
        if (!state.Messages.TryGetValue(messageId, out var message))
            return new CommandResult<IReadOnlyList<object>>.NotFound("Message not found");

        var validationError = FactRules.Validate(status, provenance);
        if (validationError is not null)
            return new CommandResult<IReadOnlyList<object>>.Invalid([validationError]);

        if (message.FactStatus == status && ProvenanceEquivalent(message.Provenance, provenance) && message.Stale is null)
            return new CommandResult<IReadOnlyList<object>>.Success([]);

        return new CommandResult<IReadOnlyList<object>>.Success(
            [new ModelEvent.MessageProvenanceRecorded(messageId, status, provenance)]);
    }

    /// <summary>
    /// Compares the claim-bearing fields only. Acceptance stamps change on every call and
    /// must not by themselves produce a new event. A fact with a stale mark is the exception:
    /// recording the same claim again is how a person says the fact still holds, so it
    /// produces the event that clears the mark.
    /// </summary>
    private static bool ProvenanceEquivalent(Provenance? a, Provenance? b) =>
        a?.Source == b?.Source && a?.Revision == b?.Revision && a?.Confidence == b?.Confidence;

    private static void CollectCascadeRemovals(ModelState state, ElementId elementId, List<object> events, HashSet<Guid> seenRelationships)
    {
        // Recursively remove children first
        var children = state.Elements.Values.Where(e => e.ParentId == elementId).ToList();
        foreach (var child in children)
            CollectCascadeRemovals(state, child.Id, events, seenRelationships);

        // Remove relationships referencing this element, deduplicating across the cascade
        foreach (var rel in state.Relationships.Values)
        {
            if (rel.FromId == elementId || rel.ToId == elementId)
            {
                if (seenRelationships.Add(rel.Id))
                    events.Add(new ModelEvent.RelationshipRemoved(rel.Id));
            }
        }

        foreach (var msg in state.Messages.Values)
        {
            if (msg.ProducerId == elementId || msg.ConsumerIds.Contains(elementId))
            {
                if (seenRelationships.Add(msg.Id)) // reuse set for dedup
                    events.Add(new ModelEvent.MessageRemoved(msg.Id));
            }
        }

        events.Add(new ModelEvent.ElementRemoved(elementId));
    }
}
