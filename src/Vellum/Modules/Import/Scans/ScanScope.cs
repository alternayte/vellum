using System.Text;
using System.Text.RegularExpressions;
using Vellum.Modules.GitHub;
using Vellum.Modules.Import.Extraction;
using Vellum.Shared;

namespace Vellum.Modules.Import.Scans;

/// <summary>One file of the tree with what the scan does with it.</summary>
/// <param name="Reason">Null for a file the scan reads. Else why it does not: a note code or a code of <see cref="CoverageGap"/>.</param>
internal sealed record ListedFile(GitHubTreeEntry Entry, string? Reason);

/// <summary>The files of a commit, each with whether the scan reads it, and why the others are not read.</summary>
/// <param name="Files">Every file of the commit that a scan can record, in path order.</param>
/// <param name="Listed">Every file of the commit.</param>
/// <param name="Skipped">The files that are not read, for any reason.</param>
/// <param name="Partial">True when a bound left out a file that would have been read.</param>
internal sealed record ScanSelection(
    IReadOnlyList<ListedFile> Files, int Listed, int Skipped, bool Partial, IReadOnlyList<ScanNote> Notes);

/// <summary>
/// Decides from the file list alone which blobs a scan asks GitHub for. Nothing is fetched
/// to make the decision: a path that looks like a secret, a binary, a dependency directory
/// or a file outside the scope is never requested. Every file keeps the reason it was left
/// out, so the scan can say later where it looked and where it did not.
/// </summary>
internal static class ScanScope
{
    public static ScanSelection Select(
        IReadOnlyList<GitHubTreeEntry> entries, ScanLimits limits,
        IReadOnlyList<string> includePaths, IReadOnlyList<string> excludePaths)
    {
        var globs = limits.ExcludeGlobs.Select(Glob).ToList();
        var notes = new List<ScanNote>();
        var files = new List<ListedFile>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var blobs = entries.Where(e => e.Type == "blob").OrderBy(e => e.Path, StringComparer.Ordinal).ToList();
        long total = 0;
        var selected = 0;
        var partial = false;
        string? cut = null;

        foreach (var entry in blobs)
        {
            // A path that cannot be a key or a text column gets a note and no row.
            if (entry.Path.Length > ScanFileEntity.MaxPath)
            {
                notes.Add(ScanNotes.Of("path-too-long", ""));
                continue;
            }
            if (TextRule.Line("path", entry.Path, ScanFileEntity.MaxPath) is not null)
            {
                notes.Add(ScanNotes.Of("invalid-path", ""));
                continue;
            }
            if (!seen.Add(entry.Path))
            {
                notes.Add(ScanNotes.Of("duplicate-path", entry.Path));
                continue;
            }

            if (StaticGap(entry.Path, globs, includePaths, excludePaths, out var notePath, out var noted) is { } gap)
            {
                // The refusals of the extraction filter are reported, even outside the scope.
                if (noted)
                    notes.Add(ScanNotes.Of(gap, notePath));
                files.Add(new ListedFile(entry, gap));
                continue;
            }

            if (cut is not null)
            {
                files.Add(new ListedFile(entry, cut));
                continue;
            }

            var size = entry.Size ?? 0;
            if (size > limits.MaxFileBytes)
            {
                notes.Add(ScanNotes.Of("limit:file-bytes", entry.Path));
                files.Add(new ListedFile(entry, "limit:file-bytes"));
                partial = true;
                continue;
            }

            // Reaching a bound ends the intake: the files after it in path order are not
            // read, so the files read do not depend on which of them are small.
            if (cut is null && selected >= limits.MaxFiles)
            {
                notes.Add(ScanNotes.Of("limit:files", entry.Path));
                cut = "limit:files";
            }
            else if (cut is null && total + size > limits.MaxTotalBytes)
            {
                notes.Add(ScanNotes.Of("limit:total-bytes", entry.Path));
                cut = "limit:total-bytes";
            }
            if (cut is not null)
            {
                files.Add(new ListedFile(entry, cut));
                partial = true;
                continue;
            }

            total += size;
            selected++;
            files.Add(new ListedFile(entry, null));
        }

        return new ScanSelection(files, blobs.Count, blobs.Count - selected, partial, [.. notes.Distinct()]);
    }

    /// <summary>
    /// For a path that is not in the tree: null when this scan would have read a file there,
    /// so its absence means the file is gone. Else the reason the scan would not have read it.
    /// </summary>
    public static string? AbsentGap(
        string path, ScanLimits limits, IReadOnlyList<string> includePaths, IReadOnlyList<string> excludePaths) =>
        StaticGap(path, [.. limits.ExcludeGlobs.Select(Glob)], includePaths, excludePaths, out _, out _);

    /// <summary>A function over paths for <see cref="AbsentGap"/>, with the globs compiled once.</summary>
    public static Func<string, string?> AbsentGap(ScanLimits limits, IReadOnlyList<string> includePaths, IReadOnlyList<string> excludePaths)
    {
        var globs = limits.ExcludeGlobs.Select(Glob).ToList();
        return path => StaticGap(path, globs, includePaths, excludePaths, out _, out _);
    }

    /// <summary>Why the path is left out by what does not depend on the tree: the path filter, the file type, the scope and the globs.</summary>
    private static string? StaticGap(
        string path, IReadOnlyList<Regex> globs, IReadOnlyList<string> includePaths, IReadOnlyList<string> excludePaths,
        out string notePath, out bool noted)
    {
        noted = false;
        if (PathFilter.SkipReason(path, out notePath) is { } reason)
        {
            noted = true;
            return reason;
        }
        if (!PathFilter.IsRelevant(path[(path.LastIndexOf('/') + 1)..]))
            return CoverageGap.NotRelevant;
        if (!InScope(path, includePaths, excludePaths))
            return CoverageGap.OutOfScope;
        return globs.Any(glob => glob.IsMatch(path)) ? CoverageGap.ExcludedGlob : null;
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
