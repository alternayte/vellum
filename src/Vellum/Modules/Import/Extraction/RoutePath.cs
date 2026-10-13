using System.Text;

namespace Vellum.Modules.Import.Extraction;

/// <summary>
/// One spelling for a route path, so that the same route gives the same key in every
/// framework: a leading slash, single slashes, and parameters as <c>{name}</c> with
/// constraints, defaults and wildcards removed. The framework spelling stays in the
/// <c>routeTemplate</c> attribute of the fact.
/// </summary>
internal static class RoutePath
{
    public static string Join(string prefix, string template) =>
        template.Length == 0 ? prefix : prefix.TrimEnd('/') + "/" + template.TrimStart('/');

    /// <param name="keepTrailingSlash">Go's ServeMux gives a trailing slash a meaning (the subtree), so it is kept there.</param>
    public static string Normalize(string raw, bool keepTrailingSlash = false)
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
        if (!keepTrailingSlash && builder.Length > 1 && builder[^1] == '/')
            builder.Length--;
        return builder.ToString();
    }

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
