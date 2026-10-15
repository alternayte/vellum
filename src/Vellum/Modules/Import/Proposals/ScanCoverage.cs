using Vellum.Modules.Import.Extraction;
using Vellum.Modules.Import.Scans;

namespace Vellum.Modules.Import.Proposals;

/// <summary>
/// Where one scan looked. Absence of evidence counts only when the scan looked where the
/// evidence was, so every conclusion from a missing fact goes through <see cref="GapFor"/>.
/// <para>
/// A path is covered when two things hold. First, the file itself: the scan listed it,
/// fetched it, cleared it and gave it to the extractors; or the file is not in the tree,
/// the tree was listed in full, and the scope, the exclude globs and the path filter of
/// this scan would have read a file at that path. Second, its surroundings: no file that an
/// extractor reads was left out in the directory of the path or in a directory above it. A
/// fact depends on more than the lines it cites (a route belongs to the service that the
/// project file next to it declares), so a project file that was withheld, cut by a limit,
/// excluded or failed leaves every fact below it unjudged.
/// </para>
/// <para>In every other case the scan did not look, and the reason is one of the classes of <see cref="CoverageGap"/>.</para>
/// </summary>
internal sealed class ScanCoverage
{
    private readonly Dictionary<string, string?> _files;
    /// <summary>For each directory that directly holds a left-out file an extractor reads: the first reason.</summary>
    private readonly Dictionary<string, string> _gapDirectories = new(StringComparer.Ordinal);
    private readonly Func<string, string?> _absentGap;
    private readonly string? _globalGap;

    /// <param name="files">Every listed path. The value is null for a file that was extracted, else the reason it was not.</param>
    /// <param name="absentGap">For a path that is not in the tree: null when this scan would have read it, else the reason.</param>
    /// <param name="globalGap">A class of <see cref="CoverageGap"/> when facts are missing for a reason no file explains.</param>
    public ScanCoverage(IReadOnlyDictionary<string, string?> files, Func<string, string?> absentGap, string? globalGap)
    {
        _files = new Dictionary<string, string?>(files, StringComparer.Ordinal);
        _absentGap = absentGap;
        _globalGap = globalGap;
        foreach (var (path, reason) in _files)
        {
            if (reason is not null)
                NoteGap(path, reason);
        }
    }

    /// <summary>A scan that looked everywhere: for planning against facts that have no scan behind them.</summary>
    public static ScanCoverage Everything => new(new Dictionary<string, string?>(), _ => null, null);

    /// <summary>Marks a listed file as not looked at, when the extraction says after the fact that it left something of it out.</summary>
    public void Exclude(string path, string reason)
    {
        if (_files.TryGetValue(path, out var existing) && existing is null)
        {
            _files[path] = reason;
            NoteGap(path, reason);
        }
    }

    /// <summary>
    /// Null when the scan looked at every path of <paramref name="evidence"/> and at
    /// everything around it. Else the class of the first reason it did not: the fact is then
    /// "not covered", and nothing follows from its absence.
    /// </summary>
    public string? GapFor(IReadOnlyList<EvidenceRef> evidence)
    {
        if (_globalGap is not null)
            return _globalGap;
        // A link with no evidence path names no place to look.
        if (evidence.Count == 0)
            return CoverageGap.Failed;

        foreach (var item in evidence)
        {
            var reason = _files.TryGetValue(item.Path, out var state) ? state : _absentGap(item.Path);
            if (reason is not null)
                return CoverageGap.Of(reason);

            for (var directory = DirectoryOf(item.Path); ; directory = DirectoryOf(directory))
            {
                if (_gapDirectories.TryGetValue(directory, out var around))
                    return CoverageGap.Of(around);
                if (directory.Length == 0)
                    break;
            }
        }
        return null;
    }

    /// <summary>
    /// A left-out file counts for its directory when an extractor would read it: its name
    /// is of a kind the extractors read, and the path filter does not refuse it. A README, an
    /// image or a <c>.env</c> file is never read under any rules, so leaving it out hides nothing.
    /// </summary>
    private void NoteGap(string path, string reason)
    {
        if (PathFilter.SkipReason(path, out _) is not null || !PathFilter.IsRelevantPath(path))
            return;
        _gapDirectories.TryAdd(DirectoryOf(path), reason);
    }

    private static string DirectoryOf(string path)
    {
        var slash = path.LastIndexOf('/');
        return slash < 0 ? "" : path[..slash];
    }
}
