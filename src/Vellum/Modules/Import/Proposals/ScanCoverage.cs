using Vellum.Modules.Import.Scans;

namespace Vellum.Modules.Import.Proposals;

/// <summary>
/// Where one scan looked. Absence of evidence counts only when the scan looked where the
/// evidence was, so every conclusion from a missing fact goes through <see cref="GapFor"/>.
/// <para>
/// A path is covered in two cases. The scan listed the file, fetched it, cleared it and
/// gave it to the extractors. Or the file is not in the tree, the tree was listed in full,
/// and the scope, the exclude globs and the path filter of this scan would have read a
/// file at that path. In every other case the scan did not look, and the reason is one of
/// the classes of <see cref="CoverageGap"/>.
/// </para>
/// </summary>
internal sealed class ScanCoverage
{
    private readonly Dictionary<string, string?> _files;
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
    }

    /// <summary>A scan that looked everywhere: for planning against facts that have no scan behind them.</summary>
    public static ScanCoverage Everything { get; } = new(new Dictionary<string, string?>(), _ => null, null);

    public int Extracted => _files.Count(f => f.Value is null);

    /// <summary>Marks a listed file as not looked at, when the extraction says after the fact that it left something of it out.</summary>
    public void Exclude(string path, string reason)
    {
        if (_files.TryGetValue(path, out var existing) && existing is null)
            _files[path] = reason;
    }

    /// <summary>
    /// Null when the scan looked at every path of <paramref name="evidence"/>. Else the class
    /// of the first reason it did not: the fact is then "not covered", and nothing follows
    /// from its absence.
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
        }
        return null;
    }
}
