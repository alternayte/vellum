using System.Text;
using Vellum.Modules.Import.Extraction;

namespace Vellum.Modules.Import.Scans;

/// <summary>What the extraction step of a scan gives.</summary>
/// <param name="FileGaps">The files given to the extractors that do not count as read after all, each with the reason: a note took them out.</param>
/// <param name="ExtractionGap">The extraction gap of the scan row: null, <c>limit</c> or <c>failed</c>.</param>
/// <param name="Notes">The notes of the extraction, as a scan keeps them.</param>
internal sealed record ScanExtractionResult(
    ExtractionResult Result, IReadOnlyDictionary<string, string> FileGaps, string? ExtractionGap, IReadOnlyList<ScanNote> Notes);

/// <summary>
/// The extraction step of a scan: the files that count as read go to the extractors, and the
/// notes decide which of them still count as read. It is the same for every kind of Source,
/// and <c>vellum scan</c> compiles this same file, so a push and a server scan of the same
/// files give the same facts, the same manifest and the same notes.
/// </summary>
internal static class ScanExtraction
{
    /// <param name="files">The files that count as read, with their bytes, which are UTF-8 text (the content clearance passed them).</param>
    public static ScanExtractionResult Run(
        string repository, string revision, IReadOnlyList<(string Path, byte[] Content)> files, ScanLimits limits)
    {
        var input = new ExtractionInput(repository, revision,
            [.. files.Select(f => new SourceFile(f.Path, Encoding.UTF8.GetString(f.Content)))]);
        var result = EvidenceExtractor.Extract(input, new ExtractionLimits(
            MaxFiles: limits.MaxFiles, MaxFileBytes: limits.MaxFileBytes, MaxTotalBytes: limits.MaxTotalBytes));

        // What the extraction says about single files. A file that it withheld is not kept;
        // a file that it read in part does not count as looked at. The first note of a file decides.
        var read = files.Select(f => f.Path).ToHashSet(StringComparer.Ordinal);
        var gaps = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var note in result.Notes)
        {
            if (FileGap(note) is { } reason && read.Contains(note.Path))
                gaps.TryAdd(note.Path, reason);
        }

        return new ScanExtractionResult(result, gaps, ExtractionGap(result),
            [.. result.Notes.Select(n => new ScanNote(n.Reason, n.Path, n.Line, NoteMessage(n)))]);
    }

    /// <summary>The reason a note takes a file out of what the scan looked at, or null when it does not.</summary>
    private static string? FileGap(ExtractionNote note)
    {
        if (note.Path.Length == 0)
            return null;
        return note.Kind switch
        {
            NoteKind.Skipped => note.Reason,
            NoteKind.ParseFailure => CoverageGap.ParseFailure,
            NoteKind.LimitReached => note.Reason.StartsWith("limit:", StringComparison.Ordinal) ? note.Reason : "limit:" + note.Reason,
            _ => null,
        };
    }

    /// <summary>
    /// Null when every fact that is missing is explained by a file that <see cref="FileGap"/>
    /// takes out. Else the extraction left out facts across files (a bound on facts, on files
    /// or on notes, or a failure with no file), and no absence can be judged.
    /// </summary>
    private static string? ExtractionGap(ExtractionResult result)
    {
        if (result.IsComplete)
            return null;
        var cuts = result.Notes.Where(n => n.Kind is NoteKind.ParseFailure or NoteKind.LimitReached).ToList();
        if (cuts.Any(n => n.Kind == NoteKind.ParseFailure && n.Path.Length == 0))
            return CoverageGap.Failed;
        var acrossFiles = cuts.Count == 0 || cuts.Any(n =>
            n.Path.Length == 0 || n.Reason is "limit:facts" or "limit:files" or "limit:total-bytes" or "limit:notes");
        return acrossFiles ? CoverageGap.Limit : null;
    }

    private static string NoteMessage(ExtractionNote note) => note.Kind switch
    {
        NoteKind.Undecided => "A rule found a site it could not decide from the text. No fact was made for it.",
        NoteKind.Redacted or NoteKind.Conflict or NoteKind.LimitReached or NoteKind.Skipped => ScanNotes.Message(note.Reason),
        _ => "The file could not be parsed. Its facts are missing from this scan.",
    };
}
