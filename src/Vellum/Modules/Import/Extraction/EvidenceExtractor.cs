using System.Text;

namespace Vellum.Modules.Import.Extraction;

/// <summary>One family of rules. An extractor reads the accepted files and adds facts and notes.</summary>
internal interface IExtractor
{
    string Id { get; }
    IReadOnlyList<ExtractionRule> Rules { get; }
    void Run(ExtractionContext context);
}

/// <summary>
/// Deterministic evidence extraction: a pure function from file content to facts with exact
/// source lines. It reads text and nothing else. It does not run a build tool, load an
/// assembly, evaluate a template, follow a URL or act on anything the text says.
/// <para>
/// A fact states only what the source establishes. A rule that cannot be sure emits nothing
/// for that site and adds a note. No value that can hold a secret enters a fact. The work on
/// one file is bounded by an operation counter (<see cref="ExtractionLimits.MaxWorkPerFile"/>).
/// </para>
/// </summary>
public static class EvidenceExtractor
{
    private static readonly IExtractor[] Extractors =
    [
        new OpenApiExtractor(), new AsyncApiExtractor(), new ComposeExtractor(),
        new DotNetExtractor(), new GoExtractor(), new DeploymentExtractor(),
        new StructurizrExtractor(), new BackstageExtractor(), new AdrExtractor(),
    ];

    /// <summary>Every rule with its version, in a stable order.</summary>
    public static IReadOnlyList<ExtractionRule> Rules { get; } =
        Extractors.SelectMany(e => e.Rules).OrderBy(r => r.Id, StringComparer.Ordinal).ToList();

    /// <summary>
    /// Never throws for any input. The same files give the same result in any input order,
    /// under any culture.
    /// </summary>
    public static ExtractionResult Extract(ExtractionInput input, ExtractionLimits? limits = null) =>
        Extract(input, limits, out _);

    /// <summary>
    /// As <see cref="Extract(ExtractionInput, ExtractionLimits?)"/>, and also gives the operations
    /// spent on each file, so a test can hold the extractors to a bound that no clock decides.
    /// </summary>
    internal static ExtractionResult Extract(ExtractionInput input, ExtractionLimits? limits, out IReadOnlyDictionary<string, long> work)
    {
        var context = new ExtractionContext(limits ?? ExtractionLimits.Default);
        try
        {
            context.Accept(input?.Files ?? []);
        }
        catch (Exception exception)
        {
            context.Failed(FailureReason("intake", exception), "");
        }

        foreach (var extractor in Extractors)
        {
            if (context.Stopped)
                break;
            try
            {
                extractor.Run(context);
            }
            catch (Exception exception)
            {
                // The extractors are written not to throw, and each guards its work on one file
                // (see ExtractionContext.Guarded). This is the last resort: the facts of the other
                // extractors stay, the failure is a note, and the result is marked incomplete.
                context.Failed(FailureReason(extractor.Id, exception), "");
            }
        }

        work = context.WorkSpent();
        try
        {
            return context.Result(input?.Repository ?? "", input?.CommitSha ?? "");
        }
        catch (Exception exception)
        {
            return new ExtractionResult(input?.Repository ?? "", input?.CommitSha ?? "", [],
                [new ExtractionNote(NoteKind.ParseFailure, FailureReason("result", exception), "", 0)], false);
        }
    }

    /// <summary>A fixed code. The message of the exception can hold repository content and is never used.</summary>
    internal static string FailureReason(string where, Exception exception) =>
        (exception is OutOfMemoryException or InsufficientMemoryException ? "out-of-memory:" : "extractor-failure:") + where;
}

/// <summary>A file that passed the path filter and the bounds, with normalised line ends.</summary>
internal sealed class SourceText
{
    public SourceText(string path, string content)
    {
        Path = path;
        Content = content;
        var slash = path.LastIndexOf('/');
        Directory = slash < 0 ? "" : path[..slash];
        Name = slash < 0 ? path : path[(slash + 1)..];
        Extension = PathFilter.Extension(Name).ToLowerInvariant();
    }

    public string Path { get; }
    public string Content { get; }
    /// <summary>The directory of the file, or "" at the repository root.</summary>
    public string Directory { get; }
    public string Name { get; }
    /// <summary>Lower case, with the dot.</summary>
    public string Extension { get; }
    public bool IsYaml => Extension is ".yaml" or ".yml";
    public bool IsJson => Extension == ".json";

    /// <summary>True when the file is in <paramref name="directory"/> or below it.</summary>
    public bool IsUnder(string directory) =>
        directory.Length == 0 || Directory == directory
        || (Directory.Length > directory.Length && Directory[directory.Length] == '/' && Directory.StartsWith(directory, StringComparison.Ordinal));
}

internal static class TextUtil
{
    private const char ByteOrderMark = (char)0xFEFF;
    private const char Replacement = (char)0xFFFD;

    /// <summary>
    /// Removes a byte order mark, makes every line end a line feed and replaces a lone
    /// surrogate, so that line numbers are the ones an editor shows and every later string
    /// is valid UTF-16.
    /// </summary>
    public static string Normalize(string content)
    {
        var start = content.Length > 0 && content[0] == ByteOrderMark ? 1 : 0;
        var clean = start == 0;
        for (var i = start; clean && i < content.Length; i++)
            clean = content[i] != '\r' && !char.IsSurrogate(content[i]);
        if (clean)
            return content;

        var builder = new StringBuilder(content.Length);
        for (var i = start; i < content.Length; i++)
        {
            var c = content[i];
            if (c == '\r')
            {
                builder.Append('\n');
                if (i + 1 < content.Length && content[i + 1] == '\n')
                    i++;
            }
            else if (char.IsHighSurrogate(c) && i + 1 < content.Length && char.IsLowSurrogate(content[i + 1]))
            {
                builder.Append(c).Append(content[++i]);
            }
            else
            {
                builder.Append(char.IsSurrogate(c) ? Replacement : c);
            }
        }
        return builder.ToString();
    }

    public static string FixSurrogates(string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            if (!char.IsSurrogate(value[i]))
                continue;
            if (char.IsHighSurrogate(value[i]) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
            {
                i++;
                continue;
            }
            return string.Create(value.Length, value, static (span, source) =>
            {
                for (var j = 0; j < source.Length; j++)
                {
                    var c = source[j];
                    if (char.IsHighSurrogate(c) && j + 1 < source.Length && char.IsLowSurrogate(source[j + 1]))
                    {
                        span[j] = c;
                        span[j + 1] = source[j + 1];
                        j++;
                    }
                    else
                    {
                        span[j] = char.IsSurrogate(c) ? Replacement : c;
                    }
                }
            });
        }
        return value;
    }

    /// <summary>Lower-case letters and digits, with every other run of characters as one '-'.</summary>
    public static string Slug(string value)
    {
        var builder = new StringBuilder(value.Length);
        var pending = false;
        foreach (var c in value)
        {
            var lower = c is >= 'A' and <= 'Z' ? (char)(c + 32) : c;
            if (lower is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                if (pending && builder.Length > 0)
                    builder.Append('-');
                pending = false;
                builder.Append(lower);
            }
            else
            {
                pending = true;
            }
        }
        return builder.ToString();
    }

    public static string Bounded(string value, int max) => value.Length <= max ? value : value[..max];

    public static string Join(IEnumerable<string> values) => string.Join(",", values);

    public static string JoinSorted(IEnumerable<string> values) =>
        string.Join(",", values.Distinct(StringComparer.Ordinal).OrderBy(v => v, StringComparer.Ordinal));
}
