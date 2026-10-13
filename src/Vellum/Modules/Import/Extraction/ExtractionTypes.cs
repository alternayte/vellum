using System.Globalization;

namespace Vellum.Modules.Import.Extraction;

/// <param name="Path">Relative to the repository root, with '/' separators.</param>
public sealed record SourceFile(string Path, string Content);

/// <param name="Repository">For example <c>github.com/acme/orders</c>.</param>
/// <param name="CommitSha">The immutable commit the files were read at.</param>
public sealed record ExtractionInput(string Repository, string CommitSha, IReadOnlyList<SourceFile> Files);

/// <summary>Where a fact is established: lines of one file, and the rule that read them.</summary>
/// <param name="Rule">For example <c>dotnet.minimal-api-route</c>.</param>
public sealed record SourceEvidence(string Path, int StartLine, int EndLine, string Rule, int RuleVersion)
{
    /// <summary><c>github.com/acme/orders@&lt;sha&gt;:src/Api/Program.cs:41-44</c></summary>
    public string Pointer(string repository, string commitSha) => string.Create(
        CultureInfo.InvariantCulture, $"{repository}@{commitSha}:{Path}:{StartLine}-{EndLine}");
}

public enum FactKind
{
    Service, Operation, Channel, Message, Subscription, Publication,
    DataStore, DataAccess, Dependency, Schema, Deployment,
}

/// <summary>
/// One thing the source establishes, and no more. A route fact says a route is declared; it
/// does not say the route is reachable. Facts point at other facts by key in an attribute.
/// </summary>
/// <param name="Key">
/// Stable identity. The same repository content gives the same key, and a key does not
/// change when lines move inside a file.
/// </param>
public sealed record ExtractedFact(
    FactKind Kind, string Key, string Name,
    IReadOnlyDictionary<string, string> Attributes,
    IReadOnlyList<SourceEvidence> Evidence);

public enum NoteKind
{
    /// <summary>A file was not read. The reason says why.</summary>
    Skipped,
    /// <summary>A file could not be parsed, or an extractor failed on it.</summary>
    ParseFailure,
    /// <summary>A bound was reached. The result is not complete.</summary>
    LimitReached,
    /// <summary>A rule found a site it could not decide statically. No fact was emitted for it.</summary>
    Undecided,
    /// <summary>A value looked like a credential or was too long, and was dropped.</summary>
    Redacted,
    /// <summary>Two sources gave different values for one attribute of one fact. The first was kept.</summary>
    Conflict,
}

/// <param name="Reason">A fixed code such as <c>secret-file</c> or <c>dynamic-route</c>. Never repository content.</param>
/// <param name="Line">0 when the note is about the whole file.</param>
public sealed record ExtractionNote(NoteKind Kind, string Reason, string Path, int Line);

/// <param name="IsComplete">
/// False when a bound was reached or an extractor failed. An incomplete result holds what was
/// extracted before the stop and must not be shown as a full scan.
/// </param>
public sealed record ExtractionResult(
    string Repository, string CommitSha,
    IReadOnlyList<ExtractedFact> Facts, IReadOnlyList<ExtractionNote> Notes, bool IsComplete);

/// <summary>
/// Bounds on one extraction. The first three defaults equal the <c>import.*</c> defaults of
/// the settings module.
/// </summary>
/// <param name="MaxFiles">Relevant files read. Reaching it ends the intake: later files in path order are not read; the files read are extracted.</param>
/// <param name="MaxFileBytes">UTF-8 size of one file. A larger file is not read; the extraction continues.</param>
/// <param name="MaxTotalBytes">UTF-8 size of all files read. Reaching it ends the intake in the same way.</param>
/// <param name="MaxFacts">Distinct facts. Reaching it stops the extraction; the facts found before it are kept.</param>
/// <param name="MaxSchemaBytes">Size of one schema body in a fact. A larger body is left out.</param>
/// <param name="MaxNesting">Depth of a JSON or YAML document. A deeper document is not read.</param>
/// <param name="MaxNotes">Notes kept. Later notes are dropped; completeness is tracked apart from the notes.</param>
public sealed record ExtractionLimits(
    int MaxFiles = 2000,
    long MaxFileBytes = 256 * 1024,
    long MaxTotalBytes = 20 * 1024 * 1024,
    int MaxFacts = 20_000,
    int MaxSchemaBytes = 64 * 1024,
    int MaxNesting = 64,
    int MaxNotes = 2000)
{
    public static readonly ExtractionLimits Default = new();
}

/// <summary>A rule of an extractor. The version changes when its output for the same input changes.</summary>
public sealed record ExtractionRule(string Id, int Version)
{
    internal SourceEvidence At(string path, int startLine, int endLine) =>
        new(path, startLine, Math.Max(startLine, endLine), Id, Version);
}
