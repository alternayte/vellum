using System.Text;
using System.Text.RegularExpressions;
using Vellum.Modules.GitHub;
using Vellum.Modules.Import.Extraction;

namespace Vellum.Modules.Import.Scans;

/// <summary>The files of a commit that a scan reads, and why the others are not read.</summary>
/// <param name="Listed">Every file of the commit.</param>
/// <param name="Skipped">The files that are not read, for any reason.</param>
/// <param name="Partial">True when a bound left out a file that would have been read.</param>
internal sealed record ScanSelection(
    IReadOnlyList<GitHubTreeEntry> Files, int Listed, int Skipped, bool Partial, IReadOnlyList<ScanNote> Notes);

/// <summary>
/// Decides from the file list alone which blobs a scan asks GitHub for. Nothing is fetched
/// to make the decision: a path that looks like a secret, a binary, a dependency directory
/// or a file outside the scope is never requested.
/// </summary>
internal static class ScanScope
{
    public static ScanSelection Select(
        IReadOnlyList<GitHubTreeEntry> entries, ScanLimits limits,
        IReadOnlyList<string> includePaths, IReadOnlyList<string> excludePaths)
    {
        var globs = limits.ExcludeGlobs.Select(Glob).ToList();
        var notes = new List<ScanNote>();
        var selected = new List<GitHubTreeEntry>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var blobs = entries.Where(e => e.Type == "blob").OrderBy(e => e.Path, StringComparer.Ordinal).ToList();
        long total = 0;
        var partial = false;
        var cut = false;

        foreach (var entry in blobs)
        {
            // The extraction filter runs first, so its refusals are reported even for a path
            // that the scope would leave out anyway.
            if (PathFilter.SkipReason(entry.Path, out var notePath) is { } reason)
            {
                notes.Add(ScanNotes.Of(reason, notePath));
                continue;
            }
            if (!seen.Add(entry.Path))
            {
                notes.Add(ScanNotes.Of("duplicate-path", entry.Path));
                continue;
            }

            var name = entry.Path[(entry.Path.LastIndexOf('/') + 1)..];
            if (cut || !PathFilter.IsRelevant(name) || !InScope(entry.Path, includePaths, excludePaths)
                || globs.Any(glob => glob.IsMatch(entry.Path)))
                continue;

            var size = entry.Size ?? 0;
            if (size > limits.MaxFileBytes)
            {
                notes.Add(ScanNotes.Of("limit:file-bytes", entry.Path));
                partial = true;
                continue;
            }

            // Reaching a bound ends the intake: the files after it in path order are not
            // read, so the files read do not depend on which of them are small.
            if (selected.Count >= limits.MaxFiles)
            {
                notes.Add(ScanNotes.Of("limit:files", entry.Path));
                partial = cut = true;
                continue;
            }
            if (total + size > limits.MaxTotalBytes)
            {
                notes.Add(ScanNotes.Of("limit:total-bytes", entry.Path));
                partial = cut = true;
                continue;
            }

            total += size;
            selected.Add(entry);
        }

        return new ScanSelection(selected, blobs.Count, blobs.Count - selected.Count, partial, [.. notes.Distinct()]);
    }

    /// <summary>A path is in scope when an include prefix holds it, or there is none, and no exclude prefix holds it.</summary>
    public static bool InScope(string path, IReadOnlyList<string> includePaths, IReadOnlyList<string> excludePaths) =>
        (includePaths.Count == 0 || includePaths.Any(prefix => Under(path, prefix)))
        && !excludePaths.Any(prefix => Under(path, prefix));

    private static bool Under(string path, string prefix) =>
        path.Length == prefix.Length
            ? string.Equals(path, prefix, StringComparison.Ordinal)
            : path.Length > prefix.Length && path[prefix.Length] == '/' && path.StartsWith(prefix, StringComparison.Ordinal);

    /// <summary>
    /// An <c>import.excludeGlobs</c> pattern as an expression over the whole path:
    /// <c>**/</c> is any number of directories, <c>**</c> is anything, <c>*</c> is anything
    /// but a slash and <c>?</c> is one character but a slash. The expression cannot backtrack.
    /// </summary>
    public static Regex Glob(string pattern)
    {
        var expression = new StringBuilder("^");
        for (var i = 0; i < pattern.Length; i++)
        {
            var c = pattern[i];
            if (c == '*' && i + 1 < pattern.Length && pattern[i + 1] == '*')
            {
                if (i + 2 < pattern.Length && pattern[i + 2] == '/')
                {
                    expression.Append("(?:.*/)?");
                    i += 2;
                }
                else
                {
                    expression.Append(".*");
                    i++;
                }
            }
            else if (c == '*')
                expression.Append("[^/]*");
            else if (c == '?')
                expression.Append("[^/]");
            else
                expression.Append(Regex.Escape(c.ToString()));
        }
        expression.Append('$');
        return new Regex(expression.ToString(), RegexOptions.NonBacktracking | RegexOptions.CultureInvariant);
    }
}
