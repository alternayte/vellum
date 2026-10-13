using Vellum.Modules.Modelling.Model;

namespace Vellum.Modules.Trace.Engine;

/// <summary>
/// How a message moves along the bindings of a channel. The validator and the engine both
/// read the rules here, so a handler the validator accepts names a subscription the engine
/// can reach.
/// </summary>
public static class ChannelRouting
{
    /// <summary>
    /// Whether a binding passes a message. A binding without a key passes every message. A
    /// binding with a key passes a message whose key equals it or matches it as an AMQP topic
    /// pattern: words are separated by <c>.</c>, <c>*</c> stands for exactly one word and
    /// <c>#</c> for zero or more. A message without a key passes no binding that has one.
    /// </summary>
    public static bool Matches(string? bindingKey, string? routingKey)
    {
        if (bindingKey is null)
            return true;
        if (routingKey is null)
            return false;
        if (string.Equals(bindingKey, routingKey, StringComparison.Ordinal))
            return true;

        var pattern = bindingKey.Split('.');
        var words = routingKey.Split('.');

        // reachable[j] says whether the pattern words read so far can match the first j words.
        var reachable = new bool[words.Length + 1];
        reachable[0] = true;
        foreach (var part in pattern)
        {
            var next = new bool[words.Length + 1];
            if (part == "#")
            {
                var open = false;
                for (var j = 0; j <= words.Length; j++)
                    next[j] = open |= reachable[j];
            }
            else
            {
                for (var j = 1; j <= words.Length; j++)
                    next[j] = reachable[j - 1] && (part == "*" || string.Equals(part, words[j - 1], StringComparison.Ordinal));
            }
            reachable = next;
        }

        return reachable[words.Length];
    }

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
