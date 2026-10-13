using System.Text;

namespace Vellum.Modules.Import.Extraction;

/// <summary>
/// One spelling for a route path, the same for every extractor, so that the same route gives
/// the same key in every framework and in an OpenAPI document:
/// <list type="bullet">
/// <item>a leading slash, and single slashes;</item>
/// <item>no trailing slash, except for the root;</item>
/// <item>parameters as <c>{name}</c> with the name as written, and with constraints, defaults
/// and wildcard marks removed (<c>{id:guid}</c>, <c>{*rest}</c>, <c>{path...}</c>).</item>
/// </list>
/// A fact holds three spellings: <c>path</c> (this one, also in the key), <c>routeTemplate</c>
/// (<see cref="Template"/>: every parameter as <c>{}</c>, so <c>/orders/{id}</c> matches
/// <c>/orders/{orderId}</c>) and <c>declaredRoute</c> (the text as the source has it).
/// </summary>
internal static class RoutePath
{
    public static string Join(string prefix, string template) =>
        template.Length == 0 ? prefix : prefix.TrimEnd('/') + "/" + template.TrimStart('/');

    public static string Normalize(string raw)
    {
        var builder = new StringBuilder(raw.Length + 1);
        builder.Append('/');
        for (var i = 0; i < raw.Length; i++)
        {
            var c = raw[i];
            if (c == '/')
            {
                if (builder[^1] != '/')
                    builder.Append('/');
            }
            else if (c == '{')
            {
                int depth = 1, end = i + 1;
                while (end < raw.Length && depth > 0)
                {
                    if (raw[end] == '{') depth++;
                    else if (raw[end] == '}') depth--;
                    end++;
                }
                var inner = raw.AsSpan(i + 1, Math.Max(0, end - i - (depth == 0 ? 2 : 1))).TrimStart('*');
                var cut = inner.IndexOfAny(':', '=', '?');
                if (cut >= 0)
                    inner = inner[..cut];
                if (inner.EndsWith("...", StringComparison.Ordinal))
                    inner = inner[..^3];
                // "{$}" in a Go pattern matches the end of the path and names nothing.
                if (!inner.SequenceEqual("$"))
                    builder.Append('{').Append(inner).Append('}');
                i = end - 1;
            }
            else
            {
                builder.Append(c);
            }
        }
        if (builder.Length > 1 && builder[^1] == '/')
            builder.Length--;
        return builder.ToString();
    }

    /// <summary>A normalised path with every parameter as <c>{}</c>.</summary>
    public static string Template(string path)
    {
        var builder = new StringBuilder(path.Length);
        for (var i = 0; i < path.Length; i++)
        {
            builder.Append(path[i]);
            if (path[i] != '{')
                continue;
            var close = path.IndexOf('}', i);
            if (close < 0)
                continue;
            builder.Append('}');
            i = close;
        }
        return builder.ToString();
    }

    /// <summary>The three route attributes of an operation fact.</summary>
    public static (string Name, string? Value)[] Attributes(string path, string declared) =>
        [("path", path), ("routeTemplate", Template(path)), ("declaredRoute", declared)];

    /// <summary>The gin and echo spelling: <c>:id</c> and <c>*rest</c> become <c>{id}</c> and <c>{rest}</c>.</summary>
    public static string FromColonParameters(string raw)
    {
        var segments = raw.Split('/');
        for (var i = 0; i < segments.Length; i++)
        {
            var segment = segments[i];
            if (segment.Length > 0 && segment[0] is ':' or '*')
                segments[i] = "{" + (segment.Length > 1 ? segment[1..] : "wildcard") + "}";
        }
        return string.Join("/", segments);
    }
}
