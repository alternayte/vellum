using Vellum.Modules.Modelling.Model;
using Vellum.Modules.Trace.Engine;

namespace Vellum.Modules.Trace.Scenarios;

/// <summary>Turns a pinned architecture model into what the trace engine plays against.</summary>
public static class TraceModelBuilder
{
    public static TraceModel Build(ModelState model, IReadOnlyDictionary<SchemaId, string> schemas) => new(
        model.Elements.ToDictionary(e => e.Key, e => e.Value.Name),
        model.Operations.ToDictionary(o => o.Key, o => new OperationContract(
            o.Value.Id, o.Value.OwnerId, o.Value.Name, o.Value.Transport,
            o.Value.RequestSchemaId, o.Value.ResponseSchemaId, o.Value.TimeoutMs, o.Value.Retry)),
        model.Channels.ToDictionary(c => c.Key, c => new ChannelContract(c.Value.Id, c.Value.Name, c.Value.Kind)),
        model.Subscriptions.ToDictionary(s => s.Key, s => new SubscriptionContract(
            s.Value.Id, s.Value.ChannelId, s.Value.ConsumerId, s.Value.Retry, s.Value.DeadLetterChannelId)),
        model.Messages.ToDictionary(m => m.Key, m => new MessageContract(m.Value.Id, m.Value.Name, m.Value.SchemaId)),
        schemas);

    /// <summary>The schemas the model's operations and messages name.</summary>
    public static IReadOnlySet<Guid> ReferencedSchemaIds(ModelState model)
    {
        var ids = new HashSet<Guid>();
        foreach (var operation in model.Operations.Values)
        {
            if (operation.RequestSchemaId is { } request) ids.Add(request);
            if (operation.ResponseSchemaId is { } response) ids.Add(response);
        }
        foreach (var message in model.Messages.Values)
        {
            if (message.SchemaId is { } schema) ids.Add(schema);
        }
        return ids;
    }

    /// <summary>
    /// A display name for every ID a trace event can carry: elements, operations, channels,
    /// messages and subscriptions. Sorted by ID, so the same model gives the same document.
    /// </summary>
    public static IReadOnlyDictionary<Guid, string> Labels(ModelState model)
    {
        var labels = new SortedDictionary<Guid, string>();
        foreach (var element in model.Elements.Values) labels[element.Id] = element.Name;
        foreach (var operation in model.Operations.Values) labels[operation.Id] = operation.Name;
        foreach (var channel in model.Channels.Values) labels[channel.Id] = channel.Name;
        foreach (var message in model.Messages.Values) labels[message.Id] = message.Name;
        foreach (var subscription in model.Subscriptions.Values)
            labels[subscription.Id] = ContractDecider.SubscriptionLabel(model, subscription);
        return labels;
    }
}
