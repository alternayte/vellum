using Vellum.Modules.Modelling;

namespace Vellum.Modules.Trace.Engine;

/// <summary>
/// How a message moves along the bindings of a channel. The validator and the engine both
/// read the rules here, so a handler the validator accepts names a subscription the engine
/// can reach.
/// </summary>
public static class ChannelRouting
{
    /// <summary>Whether a binding passes a message (<see cref="RoutingKeys.Matches"/>).</summary>
    public static bool Matches(string? bindingKey, string? routingKey) => RoutingKeys.Matches(bindingKey, routingKey);

    /// <summary>
    /// Every channel a message published to <paramref name="channel"/> can reach through
    /// bindings, with the channel itself. Routing keys are not read: a route that the key of
    /// one step closes stays reachable, and the trace says why it was not followed.
    /// </summary>
    public static IReadOnlySet<ChannelId> Reachable(TraceModel model, ChannelId channel)
    {
        var targets = model.Bindings.Values.ToLookup(b => b.SourceChannelId, b => b.TargetChannelId);
        var reached = new HashSet<ChannelId> { channel };
        var open = new Queue<ChannelId>([channel]);
        while (open.TryDequeue(out var current))
        {
            foreach (var target in targets[current])
            {
                if (reached.Add(target))
                    open.Enqueue(target);
            }
        }

        return reached;
    }
}
