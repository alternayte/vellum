namespace Vellum.Modules.Trace.Engine;

/// <summary>
/// Whether a binding passes a message, by its keys. The trace engine and the importer both
/// read this rule, so a relationship that a scan derives from a route is a route the engine
/// plays. It has no dependency, so the <c>vellum</c> command line compiles it too.
/// </summary>
public static class RoutingKeys
{
    /// <summary>
    /// A binding without a key passes every message. A binding with a key passes a message
    /// whose key equals it or matches it as an AMQP topic pattern: words are separated by
    /// <c>.</c>, <c>*</c> stands for exactly one word and <c>#</c> for zero or more. A message
    /// without a key passes no binding that has one.
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
}
