using Vellum.Kernel.Results;

namespace Vellum.Modules.Modelling.Model;

public sealed record AddElementCommand(
    Guid Id, ElementKind Kind, string Name, string? Description,
    string? Technology, Guid? OwnerId, ElementStatus Status,
    Guid? ParentId, string[] Tags);

public sealed record UpdateElementCommand(
    Guid ElementId,
    string? Name = null, bool SetName = false,
    string? Description = null, bool SetDescription = false,
    string? Technology = null, bool SetTechnology = false,
    Guid? OwnerId = null, bool SetOwnerId = false,
    Guid? ParentId = null, bool SetParentId = false,
    ElementStatus? Status = null,
    string[]? Tags = null);

public sealed record AddRelationshipCommand(
    Guid Id, Guid FromId, Guid ToId, string? Label, string? Technology);

public sealed record UpdateRelationshipCommand(
    Guid RelationshipId,
    string? Label = null, bool SetLabel = false,
    string? Technology = null, bool SetTechnology = false);

public sealed record AddMessageCommand(
    Guid Id, string Name, string? Description,
    Guid ProducerId, Guid[] ConsumerIds,
    Guid? SchemaId, string[] Tags);

public sealed record UpdateMessageCommand(
    Guid MessageId,
    string? Name = null, bool SetName = false,
    string? Description = null, bool SetDescription = false,
    Guid? ProducerId = null, bool SetProducerId = false,
    Guid[]? ConsumerIds = null, bool SetConsumerIds = false,
    Guid? SchemaId = null, bool SetSchemaId = false,
    string[]? Tags = null);

public static class ModelDecider
{
    private static readonly IReadOnlyDictionary<ElementKind, ElementKind?> ValidParentKinds = new Dictionary<ElementKind, ElementKind?>
    {
        [ElementKind.Actor] = null,
        [ElementKind.System] = null,
        [ElementKind.App] = ElementKind.System,
        [ElementKind.Store] = ElementKind.System,
        [ElementKind.Component] = ElementKind.App,
    };

    public static CommandResult<IReadOnlyList<ModelEvent>> AddElement(ModelState state, AddElementCommand cmd)
    {
        if (string.IsNullOrWhiteSpace(cmd.Name))
            return new CommandResult<IReadOnlyList<ModelEvent>>.Invalid([new ValidationError("name", "Name is required")]);

        if (state.Elements.ContainsKey(cmd.Id))
            return new CommandResult<IReadOnlyList<ModelEvent>>.Conflict("Element with this ID already exists");

        var parentError = ValidateContainment(state, cmd.Kind, cmd.ParentId);
        if (parentError is not null)
            return new CommandResult<IReadOnlyList<ModelEvent>>.Invalid([parentError]);

        return new CommandResult<IReadOnlyList<ModelEvent>>.Success(
            [new ModelEvent.ElementAdded(cmd.Id, cmd.Kind, cmd.Name, cmd.Description,
                cmd.Technology, cmd.OwnerId, cmd.Status, cmd.ParentId, cmd.Tags)]);
    }

    public static CommandResult<IReadOnlyList<ModelEvent>> UpdateElement(ModelState state, UpdateElementCommand cmd)
    {
        if (!state.Elements.TryGetValue(cmd.ElementId, out var element))
            return new CommandResult<IReadOnlyList<ModelEvent>>.NotFound("Element not found");

        var events = new List<ModelEvent>();

        if (cmd.SetName && cmd.Name != element.Name)
        {
            if (string.IsNullOrWhiteSpace(cmd.Name))
                return new CommandResult<IReadOnlyList<ModelEvent>>.Invalid([new ValidationError("name", "Name is required")]);
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
            var parentError = ValidateContainment(state, element.Kind, cmd.ParentId);
            if (parentError is not null)
                return new CommandResult<IReadOnlyList<ModelEvent>>.Invalid([parentError]);
            events.Add(new ModelEvent.ElementReparented(cmd.ElementId, cmd.ParentId));
        }

        if (cmd.Status.HasValue && cmd.Status.Value != element.Status)
            events.Add(new ModelEvent.ElementStatusChanged(cmd.ElementId, cmd.Status.Value));

        if (cmd.Tags is not null && !cmd.Tags.SequenceEqual(element.Tags))
            events.Add(new ModelEvent.ElementRetagged(cmd.ElementId, cmd.Tags));

        return new CommandResult<IReadOnlyList<ModelEvent>>.Success(events);
    }

    public static CommandResult<IReadOnlyList<ModelEvent>> RemoveElement(ModelState state, Guid elementId)
    {
        if (!state.Elements.ContainsKey(elementId))
            return new CommandResult<IReadOnlyList<ModelEvent>>.NotFound("Element not found");

        var events = new List<ModelEvent>();
        CollectCascadeRemovals(state, elementId, events, new HashSet<Guid>());
        return new CommandResult<IReadOnlyList<ModelEvent>>.Success(events);
    }

    public static CommandResult<IReadOnlyList<ModelEvent>> AddRelationship(ModelState state, AddRelationshipCommand cmd)
    {
        if (state.Relationships.ContainsKey(cmd.Id))
            return new CommandResult<IReadOnlyList<ModelEvent>>.Conflict("Relationship with this ID already exists");

        if (!state.Elements.ContainsKey(cmd.FromId))
            return new CommandResult<IReadOnlyList<ModelEvent>>.Invalid([new ValidationError("fromId", "Source element not found")]);

        if (!state.Elements.ContainsKey(cmd.ToId))
            return new CommandResult<IReadOnlyList<ModelEvent>>.Invalid([new ValidationError("toId", "Target element not found")]);

        return new CommandResult<IReadOnlyList<ModelEvent>>.Success(
            [new ModelEvent.RelationshipAdded(cmd.Id, cmd.FromId, cmd.ToId, cmd.Label, cmd.Technology, null)]);
    }

    public static CommandResult<IReadOnlyList<ModelEvent>> UpdateRelationship(ModelState state, UpdateRelationshipCommand cmd)
    {
        if (!state.Relationships.TryGetValue(cmd.RelationshipId, out var rel))
            return new CommandResult<IReadOnlyList<ModelEvent>>.NotFound("Relationship not found");

        var events = new List<ModelEvent>();

        if (cmd.SetLabel && cmd.Label != rel.Label)
            events.Add(new ModelEvent.RelationshipLabelChanged(cmd.RelationshipId, cmd.Label));

        if (cmd.SetTechnology && cmd.Technology != rel.Technology)
            events.Add(new ModelEvent.RelationshipTechnologyChanged(cmd.RelationshipId, cmd.Technology));

        return new CommandResult<IReadOnlyList<ModelEvent>>.Success(events);
    }

    public static CommandResult<IReadOnlyList<ModelEvent>> RemoveRelationship(ModelState state, Guid relationshipId)
    {
        if (!state.Relationships.ContainsKey(relationshipId))
            return new CommandResult<IReadOnlyList<ModelEvent>>.NotFound("Relationship not found");

        return new CommandResult<IReadOnlyList<ModelEvent>>.Success(
            [new ModelEvent.RelationshipRemoved(relationshipId)]);
    }

    public static CommandResult<IReadOnlyList<ModelEvent>> AddMessage(ModelState state, AddMessageCommand cmd)
    {
        if (string.IsNullOrWhiteSpace(cmd.Name))
            return new CommandResult<IReadOnlyList<ModelEvent>>.Invalid([new ValidationError("name", "Name is required")]);

        if (state.Messages.ContainsKey(cmd.Id))
            return new CommandResult<IReadOnlyList<ModelEvent>>.Conflict("Message with this ID already exists");

        if (!state.Elements.ContainsKey(cmd.ProducerId))
            return new CommandResult<IReadOnlyList<ModelEvent>>.Invalid([new ValidationError("producerId", "Producer element not found")]);

        foreach (var consumerId in cmd.ConsumerIds)
        {
            if (!state.Elements.ContainsKey(consumerId))
                return new CommandResult<IReadOnlyList<ModelEvent>>.Invalid([new ValidationError("consumerIds", $"Consumer element {consumerId} not found")]);
        }

        return new CommandResult<IReadOnlyList<ModelEvent>>.Success(
            [new ModelEvent.MessageAdded(cmd.Id, cmd.Name, cmd.Description,
                cmd.ProducerId, cmd.ConsumerIds, cmd.SchemaId, cmd.Tags)]);
    }

    public static CommandResult<IReadOnlyList<ModelEvent>> UpdateMessage(ModelState state, UpdateMessageCommand cmd)
    {
        if (!state.Messages.TryGetValue(cmd.MessageId, out var msg))
            return new CommandResult<IReadOnlyList<ModelEvent>>.NotFound("Message not found");

        if (cmd.SetName && string.IsNullOrWhiteSpace(cmd.Name))
            return new CommandResult<IReadOnlyList<ModelEvent>>.Invalid([new ValidationError("name", "Name is required")]);

        if (cmd.SetProducerId && cmd.ProducerId.HasValue && !state.Elements.ContainsKey(cmd.ProducerId.Value))
            return new CommandResult<IReadOnlyList<ModelEvent>>.Invalid([new ValidationError("producerId", "Producer element not found")]);

        if (cmd.SetConsumerIds && cmd.ConsumerIds is not null)
        {
            foreach (var consumerId in cmd.ConsumerIds)
            {
                if (!state.Elements.ContainsKey(consumerId))
                    return new CommandResult<IReadOnlyList<ModelEvent>>.Invalid([new ValidationError("consumerIds", $"Consumer element {consumerId} not found")]);
            }
        }

        var hasChanges =
            (cmd.SetName && cmd.Name != msg.Name) ||
            (cmd.SetDescription && cmd.Description != msg.Description) ||
            (cmd.SetProducerId && cmd.ProducerId != msg.ProducerId) ||
            (cmd.SetConsumerIds && cmd.ConsumerIds is not null && !cmd.ConsumerIds.SequenceEqual(msg.ConsumerIds)) ||
            (cmd.SetSchemaId && cmd.SchemaId != msg.SchemaId) ||
            (cmd.Tags is not null && !cmd.Tags.SequenceEqual(msg.Tags));

        if (!hasChanges)
            return new CommandResult<IReadOnlyList<ModelEvent>>.Success([]);

        return new CommandResult<IReadOnlyList<ModelEvent>>.Success(
            [new ModelEvent.MessageUpdated(
                cmd.MessageId,
                cmd.SetName ? cmd.Name : null,
                cmd.SetDescription ? cmd.Description : null,
                cmd.SetProducerId ? cmd.ProducerId : null,
                cmd.SetConsumerIds ? cmd.ConsumerIds : null,
                cmd.SetSchemaId ? cmd.SchemaId : null,
                cmd.SetSchemaId)]);
    }

    public static CommandResult<IReadOnlyList<ModelEvent>> RemoveMessage(ModelState state, Guid messageId)
    {
        if (!state.Messages.ContainsKey(messageId))
            return new CommandResult<IReadOnlyList<ModelEvent>>.NotFound("Message not found");

        return new CommandResult<IReadOnlyList<ModelEvent>>.Success(
            [new ModelEvent.MessageRemoved(messageId)]);
    }

    private static void CollectCascadeRemovals(ModelState state, Guid elementId, List<ModelEvent> events, HashSet<Guid> seenRelationships)
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

    private static ValidationError? ValidateContainment(ModelState state, ElementKind kind, Guid? parentId)
    {
        var requiredParentKind = ValidParentKinds[kind];

        if (requiredParentKind is null)
        {
            if (parentId.HasValue)
                return new ValidationError("parentId", $"{kind} must be top-level (no parent)");
            return null;
        }

        if (!parentId.HasValue)
            return new ValidationError("parentId", $"{kind} requires a parent of kind {requiredParentKind}");

        if (!state.Elements.TryGetValue(parentId.Value, out var parent))
            return new ValidationError("parentId", "Parent element not found");

        if (parent.Kind != requiredParentKind)
            return new ValidationError("parentId", $"{kind} requires a parent of kind {requiredParentKind}, got {parent.Kind}");

        return null;
    }
}
